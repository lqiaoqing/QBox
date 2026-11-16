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
    reachable: RwLock<Arc<HashMap<u64, bool>>>,
    paths: RwLock<Arc<HashMap<u64, Option<String>>>>,
    // 发生过目录变更的记录号。祖先不在此集合中时，路径和可达性与静态基础索引相同。
    changed_directory_ids: HashSet<u64>,
    needs_live: RwLock<Arc<HashMap<u64, bool>>>,
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
            reachable: RwLock::new(Arc::new(HashMap::new())),
            paths: RwLock::new(Arc::new(HashMap::new())),
            changed_directory_ids: HashSet::new(),
            needs_live: RwLock::new(Arc::new(HashMap::new())),
        };
        volume.changed_directory_ids = collect_changed_directory_ids(&volume.base, &volume.changes);
        volume.changed_directories = !volume.changed_directory_ids.is_empty();
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
        let mut reachable = SeededMap::empty();
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
        local: &mut SeededMap<bool>,
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

    fn remember_reachable(&self, _id: u64, _value: bool) {
        // 查询过程只写入 SeededMap::fresh，结束时一次性合并到全局表。
    }

    fn directory_path(
        &self,
        id: u64,
        visited: &mut HashSet<u64>,
        local: &mut SeededMap<Option<String>>,
    ) -> Option<String> {
        if id == self.base.root_file() {
            return Some(self.base.geometry.root.clone());
        }
        if let Some(cached) = local.get(&id).cloned() {
            return cached;
        }
        if let Some(cached) = self.paths.read().expect("path cache").get(&id).cloned() {
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
        // 全局路径表改在查询结束时一次性合并，避免这里反复写锁。
        path
    }

    pub fn new_query_caches(&self) -> (SeededMap<bool>, SeededMap<Option<String>>, SeededMap<bool>) {
        (
            SeededMap::new(Arc::clone(&self.reachable.read().expect("reachable cache"))),
            SeededMap::new(Arc::clone(&self.paths.read().expect("path cache"))),
            SeededMap::new(Arc::clone(&self.needs_live.read().expect("needs live cache"))),
        )
    }

    pub fn store_query_caches(
        &self,
        reachable: SeededMap<bool>,
        paths: SeededMap<Option<String>>,
        needs_live: SeededMap<bool>,
    ) {
        merge_seeded_map(&self.reachable, reachable);
        merge_seeded_map(&self.paths, paths);
        merge_seeded_map(&self.needs_live, needs_live);
    }

    pub fn parent_reachable(&self, parent: u64) -> bool {
        self.parent_reachable_cached(parent, &mut SeededMap::empty(), &mut SeededMap::empty())
    }

    pub fn parent_reachable_cached(
        &self,
        parent: u64,
        cache: &mut SeededMap<bool>,
        needs_live: &mut SeededMap<bool>,
    ) -> bool {
        if parent == self.base.root_file() {
            return true;
        }
        if let Some(cached) = cache.get(&parent).copied() {
            return cached;
        }
        // 父链上没有任何目录变更时，基础索引里的记录仍按建索引时的结构可达。
        if !self.parent_chain_needs_live(parent, needs_live) {
            cache.insert(parent, true);
            return true;
        }
        let value = self.directory_reachable(parent, &mut HashSet::new(), cache);
        cache.insert(parent, value);
        value
    }

    pub fn path_for_index(&self, volume: &VolumeIndex, name_index: i32) -> Option<String> {
        self.path_for_index_cached(volume, name_index, &mut SeededMap::empty(), &mut SeededMap::empty())
    }

    // 单次查询传入自己的目录路径缓存，避免每个名称重复解析父链。
    pub fn path_for_index_cached(
        &self,
        volume: &VolumeIndex,
        name_index: i32,
        cache: &mut SeededMap<Option<String>>,
        needs_live: &mut SeededMap<bool>,
    ) -> Option<String> {
        let name = volume.name(name_index);
        let parent_id = volume.node(name.parent).file;
        if !self.parent_chain_needs_live(parent_id, needs_live) {
            return self.preferred_path_from_base(volume, name_index, cache);
        }
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

    // 目录自身或其任一硬链接的祖先出现在变更集中时，才走 live 可达性 / 路径解析。
    fn parent_chain_needs_live(&self, parent: u64, cache: &mut SeededMap<bool>) -> bool {
        if parent == self.base.root_file() || !self.changed_directories {
            return false;
        }
        if let Some(&needed) = cache.get(&parent) {
            return needed;
        }
        if self.changed_directory_ids.contains(&(parent & RECORD_MASK)) {
            cache.insert(parent, true);
            return true;
        }
        let needed = match self.base.directory_name_links(parent) {
            Some(links) if !links.is_empty() => links
                .iter()
                .any(|(id, _)| self.parent_chain_needs_live(*id, cache)),
            // 不在基础索引里的父目录来自 overlay，必须按 live 视图解析。
            _ => true,
        };
        cache.insert(parent, needed);
        needed
    }

    // 未变父链沿用 live 的“优先非 DOS 名”规则，但只读基础索引，避免全局路径锁。
    fn preferred_path_from_base(
        &self,
        volume: &VolumeIndex,
        name_index: i32,
        cache: &mut SeededMap<Option<String>>,
    ) -> Option<String> {
        let name = volume.name(name_index);
        let parent_id = volume.node(name.parent).file;
        let parent = self.preferred_directory_path_from_base(parent_id, cache)?;
        Some(combine(
            &parent,
            &crate::qbi::utf16_to_string(volume.text_units(name.offset, name.length)),
        ))
    }

    fn preferred_directory_path_from_base(
        &self,
        id: u64,
        cache: &mut SeededMap<Option<String>>,
    ) -> Option<String> {
        if id == self.base.root_file() {
            return Some(self.base.geometry.root.clone());
        }
        if let Some(cached) = cache.get(&id) {
            return cached.clone();
        }
        let Some(index) = self.base.find_node_index(id) else {
            cache.insert(id, None);
            return None;
        };
        let node = self.base.node(index);
        if !node.is_directory() {
            cache.insert(id, None);
            return None;
        }
        let mut names: Vec<_> = (node.first_name..(node.first_name + node.name_count))
            .map(|name_index| self.base.name(name_index))
            .collect();
        names.sort_by_key(|name| name.namespace == 2);
        let mut path = None;
        for name in names {
            let parent_id = self.base.node(name.parent).file;
            if let Some(parent) = self.preferred_directory_path_from_base(parent_id, cache) {
                path = Some(combine(
                    &parent,
                    &crate::qbi::utf16_to_string(self.base.text_units(name.offset, name.length)),
                ));
                break;
            }
        }
        cache.insert(id, path.clone());
        path
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



/// 查询开始时只克隆 Arc，新结果写入 fresh；没有新增时结束不必拷贝整表。
pub struct SeededMap<V> {
    fresh: HashMap<u64, V>,
    shared: Arc<HashMap<u64, V>>,
}

impl<V> SeededMap<V> {
    fn new(shared: Arc<HashMap<u64, V>>) -> Self {
        Self {
            fresh: HashMap::new(),
            shared,
        }
    }

    pub fn empty() -> Self {
        Self::new(Arc::new(HashMap::new()))
    }

    fn get(&self, key: &u64) -> Option<&V> {
        // 预热之后 fresh 为空，避免每个候选多一次空表探测。
        if self.fresh.is_empty() {
            self.shared.get(key)
        } else {
            self.fresh.get(key).or_else(|| self.shared.get(key))
        }
    }

    fn insert(&mut self, key: u64, value: V) {
        self.fresh.insert(key, value);
    }

}

fn merge_seeded_map<V: Clone>(
    slot: &RwLock<Arc<HashMap<u64, V>>>,
    seeded: SeededMap<V>,
) {
    if seeded.fresh.is_empty() {
        return;
    }
    let mut next = (*seeded.shared).clone();
    next.extend(seeded.fresh);
    *slot.write().expect("query cache") = Arc::new(next);
}

fn collect_changed_directory_ids(
    base: &VolumeIndex,
    changes: &HashMap<u64, IndexChange>,
) -> HashSet<u64> {
    let mut ids = HashSet::new();
    for change in changes.values() {
        if change
            .value
            .as_ref()
            .is_some_and(|value| value.is_directory)
        {
            ids.insert(change.file & RECORD_MASK);
        }
        // 与 C# LiveVolumeIndex 一致：看基础索引里改前是不是目录，删除目录也要进入 live 解析。
        if base
            .find_file(change.file)
            .is_some_and(|file| file.is_directory)
        {
            ids.insert(change.file & RECORD_MASK);
        }
    }
    ids
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


#[cfg(test)]
mod tests {
    use super::*;
    use crate::qbi::{build_volume_image, MaterializedName, VolumeGeometry};

    fn file(
        file: u64,
        directory: bool,
        parent: u64,
        name: &str,
        namespace: u8,
    ) -> MaterializedFile {
        MaterializedFile {
            file,
            is_directory: directory,
            last_usn: None,
            link_count: 1,
            names: vec![MaterializedName {
                parent,
                name: name.to_string(),
                namespace,
                attributes: 0,
            }],
        }
    }

    fn name_index(base: &VolumeIndex, expected: &str) -> i32 {
        (0..base.name_count)
            .find(|&i| {
                crate::qbi::utf16_to_string(base.text_units(base.name(i).offset, base.name(i).length))
                    == expected
            })
            .expect(expected)
    }

    #[test]
    fn unchanged_parent_chain_keeps_static_paths() {
        let geometry = VolumeGeometry {
            root: "C:\\".to_string(),
            serial: 1,
        };
        let files = vec![
            file(5, true, 5, ".", 3),
            file(100, true, 5, "DirA", 1),
            file(200, true, 5, "DirB", 1),
            file(300, false, 100, "a.txt", 1),
            file(400, false, 200, "b.txt", 1),
        ];
        let image = build_volume_image(&geometry, 7, 8, 9, &files).expect("image");
        let mut base = VolumeIndex::from_owned_image("memory.qbi", image).expect("parse");
        base.prepare_search(CancelToken::NONE).expect("PrepareSearch");
        let base = Arc::new(base);
        let name_a = name_index(&base, "a.txt");
        let name_b = name_index(&base, "b.txt");
        let mut changes = HashMap::new();
        changes.insert(
            100,
            IndexChange {
                file: 100,
                value: Some(file(100, true, 5, "DirA2", 1)),
            },
        );
        let volume = LiveVolume::create(
            Arc::clone(&base),
            changes,
            JournalCheckpoint {
                journal_id: 7,
                next_usn: 8,
            },
            0,
            0,
            None,
            CancelToken::NONE,
        )
        .expect("live volume");
        assert!(volume.changed_directories);
        assert!(volume.changed_directory_ids.contains(&100));
        assert!(!volume.changed_directory_ids.contains(&200));
        assert!(volume.parent_chain_needs_live(100, &mut SeededMap::empty()));
        assert!(!volume.parent_chain_needs_live(200, &mut SeededMap::empty()));
        assert_eq!(
            volume.path_for_index(&base, name_a).as_deref(),
            Some("C:\\DirA2\\a.txt")
        );
        assert_eq!(
            volume.path_for_index(&base, name_b).as_deref(),
            Some("C:\\DirB\\b.txt")
        );
        assert!(volume.parent_reachable(100));
        assert!(volume.parent_reachable(200));
    }
}
