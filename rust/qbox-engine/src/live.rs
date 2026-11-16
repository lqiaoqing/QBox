//! 读取 QBOXDLT1 增量日志，按 C# LiveVolumeIndex 的语义叠加到只读基础索引。

use std::collections::{HashMap, HashSet};
use std::fs::{self, File};
use std::io::{self, Read, Seek, SeekFrom};
use std::path::Path;
use std::sync::{Arc, RwLock};
use std::time::SystemTime;

use serde::Deserialize;
use sha2::{Digest, Sha256};

use crate::cancel::{CancelToken, Canceled};
use crate::history::{HistorySignals, QueryHistory};
use crate::qbi::{
    build_volume_image, name_identity, ordinal_cmp, LightName, MaterializedFile, MaterializedName,
    OrdinalIgnoreCaseSet, VolumeIndex,
};
use crate::search::SearchCollector;

const CHANGE_MAGIC: &[u8; 8] = b"QBOXDLT1";
const CHANGE_HEADER: usize = 40;
const MAXIMUM_FRAME: usize = 64 * 1024 * 1024;
const RECORD_MASK: u64 = 0x0000_ffffffffffff;
const MAXIMUM_DEPTH: usize = 1024;

#[derive(Debug)]
pub enum LiveError {
    Io(io::Error),
    Invalid(String),
    BaseChanged,
    Canceled(Canceled),
}

impl std::fmt::Display for LiveError {
    fn fmt(&self, formatter: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        match self {
            Self::Io(error) => write!(formatter, "{error}"),
            Self::Invalid(message) => write!(formatter, "{message}"),
            Self::BaseChanged => write!(formatter, "增量日志已切换到新基础索引"),
            Self::Canceled(error) => write!(formatter, "{error}"),
        }
    }
}

impl std::error::Error for LiveError {}

impl From<io::Error> for LiveError {
    fn from(error: io::Error) -> Self {
        Self::Io(error)
    }
}

impl From<Canceled> for LiveError {
    fn from(error: Canceled) -> Self {
        Self::Canceled(error)
    }
}

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub struct JournalCheckpoint {
    pub journal_id: u64,
    pub next_usn: i64,
}

#[derive(Clone, Debug)]
pub struct IndexChange {
    pub file: u64,
    pub value: Option<MaterializedFile>,
}

#[derive(Deserialize)]
#[serde(rename_all = "camelCase")]
struct RawCheckpoint {
    journal_id: u64,
    next_usn: i64,
}

#[derive(Deserialize)]
#[serde(rename_all = "camelCase")]
struct RawFileReference {
    low: u64,
    high: u64,
}

#[derive(Deserialize)]
#[serde(rename_all = "camelCase")]
struct RawIndexedName {
    parent: RawFileReference,
    name: String,
    namespace: u8,
    attributes: u32,
}

#[derive(Deserialize)]
#[serde(rename_all = "camelCase")]
struct RawIndexedFile {
    file: RawFileReference,
    is_directory: bool,
    last_usn: Option<i64>,
    link_count: u16,
    names: Vec<RawIndexedName>,
}

#[derive(Deserialize)]
#[serde(rename_all = "camelCase")]
struct RawChange {
    file: RawFileReference,
    value: Option<RawIndexedFile>,
}

#[derive(Deserialize)]
#[serde(rename_all = "camelCase")]
struct RawChangeSet {
    checkpoint: RawCheckpoint,
    changes: Vec<RawChange>,
}

fn invalid(message: impl Into<String>) -> LiveError {
    LiveError::Invalid(message.into())
}

enum NameView {
    Index(LightName),
    Owned {
        parent: u64,
        namespace: u8,
        text: String,
    },
}

impl NameView {
    fn parent(&self) -> u64 {
        match self {
            Self::Index(name) => name.parent,
            Self::Owned { parent, .. } => *parent,
        }
    }

    fn namespace(&self) -> u8 {
        match self {
            Self::Index(name) => name.namespace,
            Self::Owned { namespace, .. } => *namespace,
        }
    }

    fn text(&self, base: &VolumeIndex) -> String {
        match self {
            Self::Index(name) => {
                crate::qbi::utf16_to_string(base.text_units(name.offset, name.length))
            }
            Self::Owned { text, .. } => text.clone(),
        }
    }
}

fn combine(parent: &str, name: &str) -> String {
    if parent.ends_with('\\') {
        format!("{parent}{name}")
    } else {
        format!("{parent}\\{name}")
    }
}

fn expected_header(base: &VolumeIndex) -> [u8; CHANGE_HEADER] {
    let mut header = [0u8; CHANGE_HEADER];
    header[..8].copy_from_slice(CHANGE_MAGIC);
    let identity = format!(
        "{}|{}|{}|{}|{}",
        base.geometry.root,
        base.geometry.serial,
        base.journal_id,
        base.next_usn,
        base.created_at_ticks
    );
    header[8..].copy_from_slice(&Sha256::digest(identity.as_bytes()));
    header
}

// 每帧独立校验，遇到未写完的尾帧即停止，不消费也不跳过后续删除记录。
fn read_frames(
    path: &Path,
    base: &VolumeIndex,
    cursor: u64,
) -> Result<(Vec<(JournalCheckpoint, Vec<IndexChange>)>, u64), LiveError> {
    if !path.exists() {
        return Ok((Vec::new(), cursor));
    }
    let mut file = File::open(path)?;
    let length = file.metadata()?.len();
    let mut header = [0u8; CHANGE_HEADER];
    if length < CHANGE_HEADER as u64 {
        return Err(invalid("增量日志头不完整"));
    }
    file.read_exact(&mut header)?;
    if header != expected_header(base) {
        return Err(LiveError::BaseChanged);
    }
    let mut position = if cursor == 0 {
        CHANGE_HEADER as u64
    } else {
        cursor
    };
    if position < CHANGE_HEADER as u64 || position > length {
        return Err(LiveError::BaseChanged);
    }
    file.seek(SeekFrom::Start(position))?;
    let mut batches = Vec::new();
    loop {
        if length - position < 4 {
            break;
        }
        let mut count = [0u8; 4];
        file.read_exact(&mut count)?;
        let count = i32::from_le_bytes(count);
        if count <= 0 || count as usize > MAXIMUM_FRAME {
            return Err(invalid("增量日志帧长度无效"));
        }
        let mut payload = vec![0u8; count as usize];
        let mut digest = [0u8; 32];
        if !read_exact_or_partial(&mut file, &mut payload)? {
            break;
        }
        if !read_exact_or_partial(&mut file, &mut digest)? {
            break;
        }
        if Sha256::digest(&payload).as_slice() != digest {
            return Err(invalid("增量日志校验失败"));
        }
        let raw: RawChangeSet = serde_json::from_slice(&payload)
            .map_err(|error| invalid(format!("增量日志帧无法解析：{error}")))?;
        let checkpoint = JournalCheckpoint {
            journal_id: raw.checkpoint.journal_id,
            next_usn: raw.checkpoint.next_usn,
        };
        let mut changes = Vec::with_capacity(raw.changes.len());
        for change in raw.changes {
            changes.push(convert_change(change)?);
        }
        batches.push((checkpoint, changes));
        position += 4 + payload.len() as u64 + 32;
    }
    Ok((batches, position))
}

fn read_exact_or_partial(file: &mut File, buffer: &mut [u8]) -> Result<bool, io::Error> {
    let mut filled = 0;
    while filled < buffer.len() {
        let read = file.read(&mut buffer[filled..])?;
        if read == 0 {
            return Ok(false);
        }
        filled += read;
    }
    Ok(true)
}

fn convert_change(raw: RawChange) -> Result<IndexChange, LiveError> {
    if raw.file.high != 0 {
        return Err(invalid("增量记录或名称无效"));
    }
    let record = raw.file.low & RECORD_MASK;
    let value = match raw.value {
        None => None,
        Some(value) => {
            if record == 5 && !value.is_directory {
                return Err(invalid("增量记录或名称无效"));
            }
            if value.file.low != raw.file.low || value.file.high != raw.file.high {
                return Err(invalid("增量记录或名称无效"));
            }
            if value.names.is_empty() {
                return Err(invalid("增量记录或名称无效"));
            }
            let mut names = Vec::with_capacity(value.names.len());
            for name in value.names {
                if name.parent.high != 0
                    || name.namespace > 3
                    || name.name.is_empty()
                    || name.name.contains(['\\', '/', '\0'])
                    || name.name == ".."
                {
                    return Err(invalid("增量记录或名称无效"));
                }
                names.push(MaterializedName {
                    parent: name.parent.low,
                    name: name.name,
                    namespace: name.namespace,
                    attributes: name.attributes,
                });
            }
            Some(MaterializedFile {
                file: value.file.low,
                is_directory: value.is_directory,
                last_usn: value.last_usn,
                link_count: value.link_count,
                names,
            })
        }
    };
    Ok(IndexChange {
        file: raw.file.low,
        value,
    })
}

pub struct LiveVolume {
    pub base: Arc<VolumeIndex>,
    pub changes: HashMap<u64, IndexChange>,
    pub overlay: Option<Arc<VolumeIndex>>,
    pub changed_directories: bool,
    pub checkpoint: JournalCheckpoint,
    pub cursor: u64,
    base_length: u64,
    base_modified: Option<SystemTime>,
    reachable: RwLock<HashMap<u64, bool>>,
    paths: RwLock<HashMap<u64, Option<String>>>,
}

pub struct LiveLoadResult {
    pub volume: Arc<LiveVolume>,
    pub warning: Option<String>,
}

impl LiveVolume {
    pub fn load(path: impl AsRef<Path>, cancel: CancelToken) -> Result<LiveLoadResult, LiveError> {
        let path = path.as_ref().to_path_buf();
        let stamp = fs::metadata(&path)?;
        let mut base = VolumeIndex::load(&path)?;
        base.prepare_search(cancel)?;
        let base = Arc::new(base);
        let change_path = path.with_extension("qbd");
        let base_checkpoint = JournalCheckpoint {
            journal_id: base.journal_id,
            next_usn: base.next_usn,
        };
        match read_frames(&change_path, &base, 0) {
            Ok((batches, cursor)) => {
                let volume = Self::create(
                    Arc::clone(&base),
                    HashMap::new(),
                    base_checkpoint,
                    0,
                    stamp.len(),
                    stamp.modified().ok(),
                    cancel,
                )?;
                let volume = apply_changes(volume, batches, cursor, cancel)?;
                Ok(LiveLoadResult {
                    volume,
                    warning: None,
                })
            }
            // .qbd 头与当前 .qbi 不匹配时 C# LiveIndexFile.Load 保留基础快照。
            Err(LiveError::BaseChanged) => {
                let volume = Self::create(
                    Arc::clone(&base),
                    HashMap::new(),
                    base_checkpoint,
                    0,
                    stamp.len(),
                    stamp.modified().ok(),
                    cancel,
                )?;
                Ok(LiveLoadResult {
                    volume,
                    warning: None,
                })
            }
            Err(error) => {
                // 基础快照仍然可用；增量日志损坏时保留它并报告警告。
                let volume = Self::create(
                    Arc::clone(&base),
                    HashMap::new(),
                    base_checkpoint,
                    0,
                    stamp.len(),
                    stamp.modified().ok(),
                    cancel,
                )?;
                Ok(LiveLoadResult {
                    volume,
                    warning: Some(format!("增量日志不可用，保留基础索引：{error}")),
                })
            }
        }
    }

    #[allow(clippy::too_many_arguments)]
    fn create(
        base: Arc<VolumeIndex>,
        changes: HashMap<u64, IndexChange>,
        checkpoint: JournalCheckpoint,
        cursor: u64,
        base_length: u64,
        base_modified: Option<SystemTime>,
        cancel: CancelToken,
    ) -> Result<Arc<Self>, LiveError> {
        let mut volume = Self {
            base,
            changes,
            overlay: None,
            changed_directories: false,
            checkpoint,
            cursor,
            base_length,
            base_modified,
            reachable: RwLock::new(HashMap::new()),
            paths: RwLock::new(HashMap::new()),
        };
        volume.changed_directories = volume.changes.values().any(|change| {
            change
                .value
                .as_ref()
                .is_some_and(|value| value.is_directory)
                || volume
                    .find_file_raw(change.file)
                    .is_some_and(|file| file.is_directory)
        });
        volume.overlay = volume.build_overlay(checkpoint, cancel)?;
        Ok(Arc::new(volume))
    }

    fn build_overlay(
        &self,
        checkpoint: JournalCheckpoint,
        cancel: CancelToken,
    ) -> Result<Option<Arc<VolumeIndex>>, LiveError> {
        if self.changes.is_empty() {
            return Ok(None);
        }
        let mut files: HashMap<u64, MaterializedFile> = HashMap::new();
        for change in self.changes.values() {
            if let Some(file) = &change.value {
                self.include_file(file, &mut files, 0)?;
            }
        }
        // 删除或复用的父记录不能留下伪路径，其他仍有效的硬链接继续参与搜索。
        let mut removed = Vec::new();
        let mut reachable = HashMap::new();
        for (key, file) in files.iter_mut() {
            file.names.retain(|name| {
                self.directory_reachable(name.parent, &mut HashSet::new(), &mut reachable)
            });
            if file.names.is_empty() {
                removed.push(*key);
            }
        }
        for key in removed {
            files.remove(&key);
        }
        if files.is_empty() {
            return Ok(None);
        }
        let list: Vec<MaterializedFile> = files.into_values().collect();
        let image = build_volume_image(
            &self.base.geometry,
            checkpoint.journal_id,
            checkpoint.next_usn,
            0,
            &list,
        )
        .map_err(|error| LiveError::Io(error))?;
        let mut overlay = VolumeIndex::from_owned_image(
            self.base.path.with_extension("overlay"),
            image,
        )
        .map_err(LiveError::Io)?;
        overlay.prepare_search(cancel)?;
        Ok(Some(Arc::new(overlay)))
    }

    fn include_file(
        &self,
        file: &MaterializedFile,
        files: &mut HashMap<u64, MaterializedFile>,
        depth: usize,
    ) -> Result<(), LiveError> {
        if depth >= MAXIMUM_DEPTH {
            return Err(invalid("增量目录层级超过安全范围"));
        }
        if files.insert(file.file, file.clone()).is_some() {
            return Ok(());
        }
        for name in &file.names {
            if let Some(parent) = self.find_file_raw(name.parent) {
                if parent.is_directory {
                    self.include_file(&parent, files, depth + 1)?;
                }
            }
        }
        Ok(())
    }

    pub fn find_file(&self, file: u64) -> Option<MaterializedFile> {
        self.find_file_raw(file)
    }

    fn find_file_raw(&self, file: u64) -> Option<MaterializedFile> {
        let record = file & RECORD_MASK;
        if let Some(change) = self.changes.get(&record) {
            return if change.file == file {
                change.value.clone()
            } else {
                None
            };
        }
        self.base.find_file(file)
    }

    fn file_names_for(&self, file: u64) -> Option<(bool, Vec<NameView>)> {
        let record = file & RECORD_MASK;
        if let Some(change) = self.changes.get(&record) {
            if change.file != file {
                return None;
            }
            let value = change.value.as_ref()?;
            return Some((
                value.is_directory,
                value
                    .names
                    .iter()
                    .map(|name| NameView::Owned {
                        parent: name.parent,
                        namespace: name.namespace,
                        text: name.name.clone(),
                    })
                    .collect(),
            ));
        }
        self.base.file_names(file).map(|light| {
            (
                light.is_directory,
                light.names.into_iter().map(NameView::Index).collect(),
            )
        })
    }

    pub fn includes_base(&self, file: u64) -> bool {
        !self.changes.contains_key(&(file & RECORD_MASK))
    }

    pub fn includes_overlay(&self, file: u64) -> bool {
        self.changes
            .get(&(file & RECORD_MASK))
            .is_some_and(|change| change.value.is_some() && change.file == file)
    }

    fn directory_reachable(
        &self,
        id: u64,
        visited: &mut HashSet<u64>,
        local: &mut HashMap<u64, bool>,
    ) -> bool {
        if id == self.base.root_file() {
            return true;
        }
        if let Some(cached) = local.get(&id).copied() {
            return cached;
        }
        if let Some(cached) = self
            .reachable
            .read()
            .expect("reachable cache")
            .get(&id)
            .copied()
        {
            local.insert(id, cached);
            return cached;
        }
        if visited.len() >= MAXIMUM_DEPTH || !visited.insert(id) {
            return false;
        }
        let mut ordered: Vec<NameView> = match self.file_names_for(id) {
            Some((true, names)) => names,
            _ => {
                visited.remove(&id);
                local.insert(id, false);
                self.remember_reachable(id, false);
                return false;
            }
        };
        ordered.sort_by_key(|name| name.namespace() == 2);
        let valid = ordered
            .iter()
            .any(|name| self.directory_reachable(name.parent(), visited, local));
        visited.remove(&id);
        local.insert(id, valid);
        self.remember_reachable(id, valid);
        valid
    }

    fn remember_reachable(&self, id: u64, value: bool) {
        self.reachable
            .write()
            .expect("reachable cache")
            .insert(id, value);
    }

    fn directory_path(
        &self,
        id: u64,
        visited: &mut HashSet<u64>,
        local: &mut HashMap<u64, Option<String>>,
    ) -> Option<String> {
        if id == self.base.root_file() {
            return Some(self.base.geometry.root.clone());
        }
        if let Some(cached) = local.get(&id).cloned() {
            return cached;
        }
        if let Some(cached) = self
            .paths
            .read()
            .expect("path cache")
            .get(&id)
            .cloned()
        {
            local.insert(id, cached.clone());
            return cached;
        }
        if visited.len() >= MAXIMUM_DEPTH || !visited.insert(id) {
            return None;
        }
        let mut ordered: Vec<NameView> = match self.file_names_for(id) {
            Some((true, names)) => names,
            _ => {
                visited.remove(&id);
                return None;
            }
        };
        ordered.sort_by_key(|name| name.namespace() == 2);
        let mut path = None;
        for name in ordered {
            if let Some(parent) = self.directory_path(name.parent(), visited, local) {
                path = Some(combine(&parent, &name.text(&self.base)));
                break;
            }
        }
        visited.remove(&id);
        local.insert(id, path.clone());
        let mut cache = self.paths.write().expect("path cache");
        if cache.len() < 8192 {
            cache.insert(id, path.clone());
        }
        path
    }

    pub fn parent_reachable(&self, parent: u64) -> bool {
        self.parent_reachable_cached(parent, &mut HashMap::new())
    }

    pub fn parent_reachable_cached(&self, parent: u64, cache: &mut HashMap<u64, bool>) -> bool {
        if parent == self.base.root_file() {
            return true;
        }
        if let Some(cached) = cache.get(&parent).copied() {
            return cached;
        }
        let value = self.directory_reachable(parent, &mut HashSet::new(), cache);
        cache.insert(parent, value);
        value
    }

    pub fn path_for_index(&self, volume: &VolumeIndex, name_index: i32) -> Option<String> {
        self.path_for_index_cached(volume, name_index, &mut HashMap::new())
    }

    // 单次查询传入自己的目录路径缓存，避免每个名称重复解析父链。
    pub fn path_for_index_cached(
        &self,
        volume: &VolumeIndex,
        name_index: i32,
        cache: &mut HashMap<u64, Option<String>>,
    ) -> Option<String> {
        let name = volume.name(name_index);
        let parent_id = volume.node(name.parent).file;
        let parent = if parent_id == self.base.root_file() {
            Some(self.base.geometry.root.clone())
        } else {
            self.directory_path(parent_id, &mut HashSet::new(), cache)
        }?;
        Some(combine(
            &parent,
            &crate::qbi::utf16_to_string(volume.text_units(name.offset, name.length)),
        ))
    }

    pub fn has_path(&self, volume: &VolumeIndex, name_index: i32) -> bool {
        self.parent_reachable(volume.node(volume.name(name_index).parent).file)
    }

    pub fn refresh(self: &Arc<Self>, cancel: CancelToken) -> Result<Option<Arc<Self>>, LiveError> {
        let stamp = fs::metadata(&self.base.path)?;
        if stamp.len() != self.base_length || stamp.modified().ok() != self.base_modified {
            return Err(LiveError::BaseChanged);
        }
        let change_path = self.base.path.with_extension("qbd");
        let (batches, cursor) = read_frames(&change_path, &self.base, self.cursor)?;
        if batches.is_empty() && cursor == self.cursor {
            return Ok(None);
        }
        Ok(Some(apply_changes(
            Arc::clone(self),
            batches,
            cursor,
            cancel,
        )?))
    }

    pub fn search_into(
        self: &Arc<Self>,
        collector: &mut SearchCollector<'_>,
        history: &IndexHistoryMap,
        cancel: CancelToken,
        replaced_shortcut_ids: Option<&HashSet<String>>,
        represented_executables: Option<&OrdinalIgnoreCaseSet>,
    ) -> Result<(), Canceled> {
        self.base.search_into_live(
            collector,
            cancel,
            Some(self),
            false,
            self.changed_directories,
            replaced_shortcut_ids,
            represented_executables,
            Some(history),
        )?;
        if let Some(overlay) = &self.overlay {
            overlay.search_into_live(
                collector,
                cancel,
                Some(self),
                true,
                true,
                replaced_shortcut_ids,
                represented_executables,
                Some(history),
            )?;
        }
        Ok(())
    }
}

fn apply_changes(
    volume: Arc<LiveVolume>,
    batches: Vec<(JournalCheckpoint, Vec<IndexChange>)>,
    cursor: u64,
    cancel: CancelToken,
) -> Result<Arc<LiveVolume>, LiveError> {
    if batches.is_empty() {
        return Ok(volume);
    }
    let mut changes = volume.changes.clone();
    let mut checkpoint = volume.checkpoint;
    for (batch_checkpoint, batch_changes) in batches {
        cancel.check()?;
        if batch_checkpoint.journal_id != checkpoint.journal_id
            || batch_checkpoint.next_usn < checkpoint.next_usn
        {
            return Err(invalid("增量日志的检查点发生回退"));
        }
        for change in batch_changes {
            changes.insert(change.file & RECORD_MASK, change);
        }
        checkpoint = batch_checkpoint;
    }
    LiveVolume::create(
        Arc::clone(&volume.base),
        changes,
        checkpoint,
        cursor,
        volume.base_length,
        volume.base_modified,
        cancel,
    )
}

pub struct LiveSet {
    pub volumes: Vec<Arc<LiveVolume>>,
    pub warnings: Vec<String>,
}

impl LiveSet {
    pub fn load(index_dir: impl AsRef<Path>, cancel: CancelToken) -> Result<Self, LiveError> {
        let index_dir = index_dir.as_ref();
        let mut paths = Vec::new();
        for entry in fs::read_dir(index_dir)? {
            let path = entry?.path();
            if path
                .extension()
                .and_then(|value| value.to_str())
                .is_some_and(|value| value.eq_ignore_ascii_case("qbi"))
            {
                paths.push(path);
            }
        }
        paths.sort_by(|left, right| {
            ordinal_cmp(
                &left.to_string_lossy(),
                &right.to_string_lossy(),
            )
        });
        if paths.is_empty() {
            return Err(invalid("索引目录中没有 .qbi"));
        }
        let mut volumes = Vec::with_capacity(paths.len());
        let mut warnings = Vec::new();
        let mut serials = HashSet::new();
        for path in paths {
            let loaded = LiveVolume::load(&path, cancel)?;
            if !serials.insert(loaded.volume.base.geometry.serial) {
                return Err(invalid("同一个磁盘存在重复索引，请选择正确的索引目录"));
            }
            if let Some(warning) = loaded.warning {
                warnings.push(warning);
            }
            volumes.push(loaded.volume);
        }
        Ok(Self { volumes, warnings })
    }

    pub fn refresh(&self, cancel: CancelToken) -> Result<Option<Self>, LiveError> {
        let mut volumes = Vec::with_capacity(self.volumes.len());
        let mut changed = false;
        let mut warnings = Vec::new();
        for volume in &self.volumes {
            match volume.refresh(cancel) {
                Ok(Some(next)) => {
                    changed = true;
                    volumes.push(next);
                }
                Ok(None) => volumes.push(Arc::clone(volume)),
                Err(LiveError::BaseChanged) => return Err(LiveError::BaseChanged),
                Err(error) => {
                    warnings.push(format!("实时索引暂不可读，保留上次结果：{error}"));
                    volumes.push(Arc::clone(volume));
                }
            }
        }
        if !changed {
            return Ok(None);
        }
        Ok(Some(Self { volumes, warnings }))
    }

    pub fn search_into(
        &self,
        collector: &mut SearchCollector<'_>,
        history: &QueryHistory,
        cancel: CancelToken,
        replaced_shortcut_ids: Option<&HashSet<String>>,
        represented_executables: Option<&OrdinalIgnoreCaseSet>,
    ) -> Result<(), Canceled> {
        let index_history = IndexHistoryMap::new(history, &self.volumes);
        for volume in &self.volumes {
            volume.search_into(
                collector,
                &index_history,
                cancel,
                replaced_shortcut_ids,
                represented_executables,
            )?;
        }
        Ok(())
    }
}

pub struct IndexHistoryMap {
    by_serial: HashMap<u64, HashMap<(u64, u64), Vec<(String, HistorySignals)>>>,
}

impl IndexHistoryMap {
    pub fn new(history: &QueryHistory, volumes: &[Arc<LiveVolume>]) -> Self {
        let mut by_serial: HashMap<u64, HashMap<(u64, u64), Vec<(String, HistorySignals)>>> =
            HashMap::new();
        for volume in volumes {
            by_serial.entry(volume.base.geometry.serial).or_default();
        }
        for (target_id, signals) in history.enumerate() {
            let fields: Vec<&str> = target_id.split(':').collect();
            if fields.len() != 5 || fields[0] != "ntfs" {
                continue;
            }
            let Ok(serial) = u64::from_str_radix(fields[1], 16) else {
                continue;
            };
            let Ok(file) = u64::from_str_radix(fields[2], 16) else {
                continue;
            };
            let Ok(parent) = u64::from_str_radix(fields[3], 16) else {
                continue;
            };
            let Some(map) = by_serial.get_mut(&serial) else {
                continue;
            };
            map.entry((file, parent))
                .or_default()
                .push((fields[4].to_string(), signals));
        }
        Self { by_serial }
    }

    pub fn signals_for(
        &self,
        serial: u64,
        file: u64,
        parent: u64,
        name_units: &[u16],
    ) -> HistorySignals {
        let Some(map) = self.by_serial.get(&serial) else {
            return HistorySignals::default();
        };
        let Some(list) = map.get(&(file, parent)) else {
            return HistorySignals::default();
        };
        let hash = name_identity(name_units);
        list.iter()
            .find(|(value, _)| *value == hash)
            .map(|(_, signals)| *signals)
            .unwrap_or_default()
    }
}
