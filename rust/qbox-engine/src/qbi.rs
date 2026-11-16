//! 只读映射 `QBXNTFS1` 索引，布局与 `VolumeIndexStore` / `MappedIndexStorage` 一致，不写回文件。

use std::cell::RefCell;
use std::collections::{HashMap, HashSet};
use std::fs::File;
use std::io;
use std::path::{Path, PathBuf};
use std::sync::Arc;

use memmap2::{Mmap, MmapOptions};
use sha2::{Digest, Sha256};

use crate::cancel::{CancelToken, Canceled};
use crate::fuzzy::PreparedQuery;
use crate::live::{IndexHistoryMap, LiveVolume};
use crate::pinyin::PreparedApplicationName;
use crate::search::{SearchCandidate, SearchCollector, SearchItemKind, UniqueSearchSource};

pub const MAGIC: &[u8; 8] = b"QBXNTFS1";

#[derive(Clone, Copy, Debug)]
pub struct IndexNode {
    pub file: u64,
    pub first_name: i32,
    pub name_count: i32,
    pub directory: u8,
    pub has_usn: u8,
    pub usn: i64,
    pub link_count: u16,
}

impl IndexNode {
    pub fn is_directory(self) -> bool {
        self.directory != 0
    }
}

#[derive(Clone, Copy, Debug)]
pub struct IndexName {
    pub parent: i32,
    pub offset: i32,
    pub length: i32,
    pub namespace: u8,
    pub attributes: u32,
}

#[derive(Clone, Debug)]
pub struct VolumeGeometry {
    pub root: String,
    pub serial: u64,
}

enum Storage {
    Mmap(Mmap),
    Owned(Vec<u8>),
}

impl Storage {
    fn bytes(&self) -> &[u8] {
        match self {
            Self::Mmap(mapped) => mapped,
            Self::Owned(owned) => owned,
        }
    }
}

pub struct VolumeIndex {
    pub path: PathBuf,
    pub format_version: i32,
    pub geometry: VolumeGeometry,
    storage: Storage,
    nodes_offset: usize,
    names_offset: usize,
    text_offset: usize,
    // 名称表为奇数项时文本区可能不对齐；此时保留一份对齐副本，避免 u16 悬垂指针。
    aligned_text: Option<Vec<u16>>,
    pub node_count: i32,
    pub name_count: i32,
    pub text_count: i32,
    pub root_node: i32,
    pub journal_id: u64,
    pub next_usn: i64,
    pub created_at_ticks: i64,
    presence: Option<NamePresenceIndex>,
    phonetic: Option<PhoneticNameIndex>,
}

impl VolumeIndex {
    pub fn load(path: impl AsRef<Path>) -> io::Result<Self> {
        let path = path.as_ref().to_path_buf();
        let file = File::open(&path)?;
        let mmap = unsafe { MmapOptions::new().map(&file)? };
        Self::parse(path, Storage::Mmap(mmap), true)
    }

    // overlay 在内存里按 v1 布局生成后走同一条解析路径，避免两套搜索实现分叉。
    pub fn from_owned_image(path: impl Into<PathBuf>, bytes: Vec<u8>) -> io::Result<Self> {
        Self::parse(path.into(), Storage::Owned(bytes), true)
    }

    fn parse(path: PathBuf, storage: Storage, verify_digest: bool) -> io::Result<Self> {
        let data = storage.bytes();
        if data.len() < 128 {
            return Err(io::Error::new(io::ErrorKind::InvalidData, "索引文件被截断"));
        }
        let body_len = data.len() - 32;
        if verify_digest {
            let actual = Sha256::digest(&data[..body_len]);
            if actual.as_slice() != &data[body_len..] {
                return Err(io::Error::new(io::ErrorKind::InvalidData, "索引校验失败"));
            }
        }
        if &data[..8] != MAGIC {
            return Err(io::Error::new(
                io::ErrorKind::InvalidData,
                "索引格式版本无效",
            ));
        }
        let mut cur = 8;
        let version = read_i32(data, &mut cur)?;
        if version != 1 && version != 2 {
            return Err(io::Error::new(
                io::ErrorKind::InvalidData,
                "索引格式版本无效",
            ));
        }
        let root = read_binary_string(data, &mut cur)?;
        let serial = read_u64(data, &mut cur)?;
        let _sector = read_i32(data, &mut cur)?;
        let _cluster = read_i32(data, &mut cur)?;
        let _record = read_i32(data, &mut cur)?;
        let _mft_valid = read_i64(data, &mut cur)?;
        let _mft_start = read_i64(data, &mut cur)?;
        let _major = read_u16(data, &mut cur)?;
        let _minor = read_u16(data, &mut cur)?;
        let journal_id = read_u64(data, &mut cur)?;
        let next_usn = read_i64(data, &mut cur)?;
        let created_at_ticks = read_i64(data, &mut cur)?;
        let node_count = read_i32(data, &mut cur)?;
        let name_count = read_i32(data, &mut cur)?;
        let text_count = read_i32(data, &mut cur)?;
        let expected = node_count as i64 * 28 + name_count as i64 * 17 + text_count as i64 * 2;
        if node_count <= 0
            || name_count < node_count
            || text_count <= 0
            || (version == 1 && text_count < name_count)
            || expected != (body_len as i64 - cur as i64)
        {
            return Err(io::Error::new(
                io::ErrorKind::InvalidData,
                "索引计数与文件长度不符",
            ));
        }
        let nodes_offset = cur;
        let names_offset = cur + node_count as usize * 28;
        let text_offset = names_offset + name_count as usize * 17;
        let text_bytes = &data[text_offset..text_offset + text_count as usize * 2];
        let aligned_text = (text_bytes.as_ptr() as usize & 1 != 0).then(|| {
            text_bytes
                .chunks_exact(2)
                .map(|pair| u16::from_le_bytes([pair[0], pair[1]]))
                .collect()
        });
        let mut volume = Self {
            path,
            format_version: version,
            geometry: VolumeGeometry { root, serial },
            storage,
            nodes_offset,
            names_offset,
            text_offset,
            aligned_text,
            node_count,
            name_count,
            text_count,
            root_node: -1,
            journal_id,
            next_usn,
            created_at_ticks,
            presence: None,
            phonetic: None,
        };
        for i in 0..volume.node_count {
            if volume.node(i).file & 0x0000_ffffffffffff == 5 {
                volume.root_node = i;
                break;
            }
        }
        Ok(volume)
    }

    pub fn node(&self, index: i32) -> IndexNode {
        let at = self.nodes_offset + index as usize * 28;
        let b = &self.storage.bytes()[at..at + 28];
        IndexNode {
            file: u64::from_le_bytes(b[0..8].try_into().unwrap()),
            first_name: i32::from_le_bytes(b[8..12].try_into().unwrap()),
            name_count: i32::from_le_bytes(b[12..16].try_into().unwrap()),
            directory: b[16],
            has_usn: b[17],
            usn: i64::from_le_bytes(b[18..26].try_into().unwrap()),
            link_count: u16::from_le_bytes(b[26..28].try_into().unwrap()),
        }
    }

    pub fn name(&self, index: i32) -> IndexName {
        let at = self.names_offset + index as usize * 17;
        let b = &self.storage.bytes()[at..at + 17];
        IndexName {
            parent: i32::from_le_bytes(b[0..4].try_into().unwrap()),
            offset: i32::from_le_bytes(b[4..8].try_into().unwrap()),
            length: i32::from_le_bytes(b[8..12].try_into().unwrap()),
            namespace: b[12],
            attributes: u32::from_le_bytes(b[13..17].try_into().unwrap()),
        }
    }

    pub fn text_units(&self, offset: i32, length: i32) -> &[u16] {
        if let Some(text) = &self.aligned_text {
            return &text[offset as usize..offset as usize + length as usize];
        }
        let start = self.text_offset + offset as usize * 2;
        let bytes = &self.storage.bytes()[start..start + length as usize * 2];
        // UTF-16 单元与 C# char 偏移一致；小端平台且已对齐时直接重解释。
        debug_assert_eq!(bytes.as_ptr() as usize & 1, 0);
        unsafe { std::slice::from_raw_parts(bytes.as_ptr() as *const u16, length as usize) }
    }

    // 按 C# VolumeIndex.FindFile 的语义把节点物化成带名称的只读记录；节点本身已按文件编号排序。
    pub fn find_file(&self, file: u64) -> Option<MaterializedFile> {
        let mut low = 0i32;
        let mut high = self.node_count - 1;
        while low <= high {
            let at = low + (high - low) / 2;
            let node = self.node(at);
            // node.File / FileReference.Low 是完整 64 位文件引用；只有记录号才取低 48 位。
            if node.file < file {
                low = at + 1;
                continue;
            }
            if node.file > file {
                high = at - 1;
                continue;
            }
            let mut names = Vec::with_capacity(node.name_count as usize);
            for index in node.first_name..(node.first_name + node.name_count) {
                let name = self.name(index);
                names.push(MaterializedName {
                    parent: self.node(name.parent).file,
                    name: utf16_to_string(self.text_units(name.offset, name.length)),
                    namespace: name.namespace,
                    attributes: name.attributes,
                });
            }
            return Some(MaterializedFile {
                file: node.file,
                is_directory: node.is_directory(),
                last_usn: (node.has_usn != 0).then_some(node.usn),
                link_count: node.link_count,
                names,
            });
        }
        None
    }

    pub fn file_names(&self, file: u64) -> Option<LightFile> {
        let mut low = 0i32;
        let mut high = self.node_count - 1;
        while low <= high {
            let at = low + (high - low) / 2;
            let node = self.node(at);
            if node.file < file {
                low = at + 1;
                continue;
            }
            if node.file > file {
                high = at - 1;
                continue;
            }
            let mut names = Vec::with_capacity(node.name_count as usize);
            for index in node.first_name..(node.first_name + node.name_count) {
                let name = self.name(index);
                names.push(LightName {
                    parent: self.node(name.parent).file,
                    namespace: name.namespace,
                    offset: name.offset,
                    length: name.length,
                });
            }
            return Some(LightFile {
                is_directory: node.is_directory(),
                names,
            });
        }
        None
    }

    pub fn root_file(&self) -> u64 {
        self.node(self.root_node).file
    }

    pub fn path_for(&self, name_index: i32) -> String {
        path_for(self, name_index)
    }

    pub fn prepare_search(&mut self, cancel: CancelToken) -> Result<(), Canceled> {
        if self.presence.is_none() {
            self.presence = Some(NamePresenceIndex::build(self, cancel)?);
        }
        if self.phonetic.is_none() {
            self.phonetic = Some(PhoneticNameIndex::build(self, cancel)?);
        }
        Ok(())
    }

    pub fn search_into(
        self: &Arc<Self>,
        collector: &mut SearchCollector<'_>,
        cancel: CancelToken,
    ) -> Result<(), Canceled> {
        self.search_into_live(collector, cancel, None, false, false, None, None, None)
    }

    // 与 C# VolumeIndex.SearchInto 相同：base/overlay 过滤、changedDirectories 路径解析、快捷方式替换与历史信号。
    #[allow(clippy::too_many_arguments)]
    pub fn search_into_live(
        self: &Arc<Self>,
        collector: &mut SearchCollector<'_>,
        cancel: CancelToken,
        live: Option<&Arc<LiveVolume>>,
        overlay: bool,
        resolve_paths: bool,
        replaced_shortcut_ids: Option<&HashSet<String>>,
        represented_executables: Option<&OrdinalIgnoreCaseSet>,
        history: Option<&IndexHistoryMap>,
    ) -> Result<(), Canceled> {
        cancel.check()?;
        assert!(
            !resolve_paths || live.is_some(),
            "resolve_paths 需要 live 视图"
        );
        let presence = self.presence.as_ref().expect("必须先 PrepareSearch");
        let phonetic = self.phonetic.as_ref().expect("必须先 PrepareSearch");
        let source: Arc<dyn UniqueSearchSource> = Arc::new(VolumeSource {
            volume: Arc::clone(self),
            live: live.cloned(),
        });
        let query_has_exe = contains_dot_exe_ignore_case(collector.query().text());
        // 单次查询内复用父目录可达性与目录路径，避免每个候选都进全局缓存锁。
        let mut reachable_cache: HashMap<u64, bool> = HashMap::new();
        let mut path_cache: HashMap<u64, Option<String>> = HashMap::new();
        let package_windows_apps = format!("{}WindowsApps\\", self.geometry.root);
        let package_program_files = format!("{}Program Files\\WindowsApps\\", self.geometry.root);
        let selected = presence.select(collector.query().text(), self, cancel)?;
        for i in selected {
            cancel.check()?;
            let node_index = presence.owner(i);
            if node_index == self.root_node {
                continue;
            }
            let node = self.node(node_index);
            if let Some(live) = live {
                let included = if overlay {
                    live.includes_overlay(node.file)
                } else {
                    live.includes_base(node.file)
                };
                if !included {
                    continue;
                }
            }
            let name = self.name(i);
            if name.namespace == 2 && has_long_name(self, node, name.parent) {
                continue;
            }
            let raw = self.text_units(name.offset, name.length);
            let shortcut = !node.is_directory() && ends_with_ascii_ignore(raw, ".lnk");
            let executable = !node.is_directory() && ends_with_ascii_ignore(raw, ".exe");
            let friendly = if shortcut || executable {
                &raw[..raw.len() - 4]
            } else {
                raw
            };
            let friendly_match = collector.query().can_match_utf16(friendly, cancel)?;
            let display = if friendly_match {
                friendly
            } else if (shortcut || executable) && collector.query().can_match_utf16(raw, cancel)? {
                raw
            } else if let Some(alias) =
                matching_dos_alias(self, node, name.parent, collector.query(), cancel)?
            {
                let alias_name = self.name(alias);
                self.text_units(alias_name.offset, alias_name.length)
            } else {
                continue;
            };
            // 普通候选只验证父链；完整路径留给 EXE 来源判定和最终展示的少量结果。
            if resolve_paths {
                let live = live.unwrap();
                let parent = self.node(name.parent).file;
                if !live.parent_reachable_cached(parent, &mut reachable_cache) {
                    continue;
                }
            }
            // 只有可能成为直接应用入口的 EXE 才需要完整路径；C# 的结果条件与这里等价。
            let needs_live_path = executable && friendly_match && !query_has_exe;
            let live_path = if resolve_paths && needs_live_path {
                live.unwrap().path_for_index_cached(self, i, &mut path_cache)
            } else {
                None
            };
            let mut application = needs_live_path;
            if application {
                let package = if resolve_paths {
                    live_path.as_deref().is_some_and(|path| {
                        in_package_path(path, &package_windows_apps, &package_program_files)
                    })
                } else {
                    in_package_directory(self, name.parent)
                };
                application = !package;
            }
            if application && collector.query().score_utf16(display, cancel)? <= 1 {
                application = false;
            }
            if application {
                if let Some(represented) = represented_executables {
                    let path = live_path
                        .clone()
                        .unwrap_or_else(|| path_for(self, i));
                    if represented.contains(&path) {
                        application = false;
                    }
                }
            }
            let kind = if node.is_directory() {
                SearchItemKind::Directory
            } else if shortcut {
                SearchItemKind::Shortcut
            } else if application {
                SearchItemKind::Application
            } else {
                SearchItemKind::File
            };
            let key = ((node_index as u32 as u64) << 32) | (i as u32 as u64);
            if shortcut {
                if let Some(replaced) = replaced_shortcut_ids {
                    if replaced.contains(&source.identity(key)) {
                        continue;
                    }
                }
            }
            let signals = history
                .map(|history| {
                    history.signals_for(
                        self.geometry.serial,
                        node.file,
                        self.node(name.parent).file,
                        raw,
                    )
                })
                .unwrap_or_default();
            collector.add_matched_utf16(
                &source,
                key,
                display,
                kind,
                0,
                signals,
                application,
                application,
                None,
                false,
            )?;
        }
        let phonetic_hits = phonetic.select(collector.query().text());
        for entry in phonetic_hits {
            let node = self.node(entry.node);
            let name = self.name(entry.name);
            if entry.node == self.root_node {
                continue;
            }
            if let Some(live) = live {
                let included = if overlay {
                    live.includes_overlay(node.file)
                } else {
                    live.includes_base(node.file)
                };
                if !included {
                    continue;
                }
            }
            if name.namespace == 2 && has_long_name(self, node, name.parent) {
                continue;
            }
            let raw = self.text_units(name.offset, name.length);
            let display = entry.prepared.text();
            // 与字面入口互斥，包含 DOS 别名的目标也只统计一次。
            if collector.query().can_match(display)
                || collector.query().can_match_utf16(raw, cancel)?
                || matching_dos_alias(self, node, name.parent, collector.query(), cancel)?.is_some()
            {
                continue;
            }
            let match_value =
                entry
                    .prepared
                    .match_phonetic(collector.query().text(), true, cancel)?;
            if match_value.score == 0 {
                continue;
            }
            let path = if resolve_paths {
                live.unwrap()
                    .path_for_index_cached(self, entry.name, &mut path_cache)
            } else {
                Some(path_for(self, entry.name))
            };
            let Some(path) = path else {
                continue;
            };
            let shortcut = !node.is_directory() && ends_with_ascii_ignore(raw, ".lnk");
            let executable = !node.is_directory() && ends_with_ascii_ignore(raw, ".exe");
            let application = executable
                && !in_package_path(&path, &package_windows_apps, &package_program_files)
                && !represented_executables.is_some_and(|set| set.contains(&path));
            let kind = if node.is_directory() {
                SearchItemKind::Directory
            } else if shortcut {
                SearchItemKind::Shortcut
            } else if application {
                SearchItemKind::Application
            } else {
                SearchItemKind::File
            };
            let key = ((entry.node as u32 as u64) << 32) | (entry.name as u32 as u64);
            if shortcut {
                if let Some(replaced) = replaced_shortcut_ids {
                    if replaced.contains(&source.identity(key)) {
                        continue;
                    }
                }
            }
            let signals = history
                .map(|history| {
                    history.signals_for(
                        self.geometry.serial,
                        node.file,
                        self.node(name.parent).file,
                        raw,
                    )
                })
                .unwrap_or_default();
            collector.add_matched(
                &source,
                key,
                display,
                kind,
                0,
                signals,
                application,
                application,
                match_value.phonetic_match,
                true,
            )?;
        }
        Ok(())
    }
}

struct VolumeSource {
    volume: Arc<VolumeIndex>,
    live: Option<Arc<LiveVolume>>,
}

impl VolumeSource {
    fn identity_at(&self, key: u64) -> String {
        let volume = &self.volume;
        let node_index = (key >> 32) as i32;
        let name_index = key as u32 as i32;
        let node = volume.node(node_index);
        let name = volume.name(name_index);
        let parent_file = volume.node(name.parent).file;
        let hash = name_identity(volume.text_units(name.offset, name.length));
        format!(
            "ntfs:{:016x}:{:016x}:{:016x}:{hash}",
            volume.geometry.serial, node.file, parent_file
        )
    }
}

impl UniqueSearchSource for VolumeSource {
    fn materialize(
        &self,
        key: u64,
        name: &str,
        kind: SearchItemKind,
        _priority: u64,
    ) -> SearchCandidate {
        let name_index = key as u32 as i32;
        let target = self
            .live
            .as_ref()
            .and_then(|live| live.path_for_index(&self.volume, name_index))
            .unwrap_or_else(|| self.volume.path_for(name_index));
        SearchCandidate::new(self.identity_at(key), name, target, kind)
    }
    fn identity(&self, key: u64) -> String {
        self.identity_at(key)
    }
}

fn has_long_name(volume: &VolumeIndex, node: IndexNode, parent: i32) -> bool {
    let end = node.first_name + node.name_count;
    for i in node.first_name..end {
        let name = volume.name(i);
        if name.parent == parent && name.namespace != 2 {
            return true;
        }
    }
    false
}

fn matching_dos_alias(
    volume: &VolumeIndex,
    node: IndexNode,
    parent: i32,
    query: &PreparedQuery<'_>,
    cancel: CancelToken,
) -> Result<Option<i32>, Canceled> {
    let end = node.first_name + node.name_count;
    for i in node.first_name..end {
        let name = volume.name(i);
        if name.parent == parent
            && name.namespace == 2
            && query.can_match_utf16(volume.text_units(name.offset, name.length), cancel)?
        {
            return Ok(Some(i));
        }
    }
    Ok(None)
}

fn in_package_directory(volume: &VolumeIndex, mut parent: i32) -> bool {
    while parent != volume.root_node {
        let name = volume.name(volume.node(parent).first_name);
        let text = volume.text_units(name.offset, name.length);
        if eq_ignore_ascii(text, "WindowsApps") {
            if name.parent == volume.root_node {
                return true;
            }
            let container = volume.name(volume.node(name.parent).first_name);
            if container.parent == volume.root_node
                && eq_ignore_ascii(
                    volume.text_units(container.offset, container.length),
                    "Program Files",
                )
            {
                return true;
            }
        }
        parent = name.parent;
    }
    false
}

fn in_package_path(path: &str, windows_apps: &str, program_files: &str) -> bool {
    starts_with_ignore_ascii(path, windows_apps) || starts_with_ignore_ascii(path, program_files)
}

fn contains_dot_exe_ignore_case(text: &str) -> bool {
    text.as_bytes()
        .windows(4)
        .any(|window| window.eq_ignore_ascii_case(b".exe"))
}

fn path_for(volume: &VolumeIndex, name_index: i32) -> String {
    let mut parts = vec![name_index];
    let mut parent = volume.name(name_index).parent;
    while parent != volume.root_node {
        let first = volume.node(parent).first_name;
        parts.push(first);
        parent = volume.name(first).parent;
    }
    let mut result = volume.geometry.root.clone();
    for (i, part_index) in parts.iter().rev().enumerate() {
        let part = volume.name(*part_index);
        result.push_str(&utf16_to_string(
            volume.text_units(part.offset, part.length),
        ));
        if i + 1 != parts.len() {
            result.push('\\');
        }
    }
    result
}

fn starts_with_ignore_ascii(value: &str, prefix: &str) -> bool {
    value.len() >= prefix.len()
        && value.as_bytes()[..prefix.len()].eq_ignore_ascii_case(prefix.as_bytes())
}

fn eq_ignore_ascii(units: &[u16], ascii: &str) -> bool {
    if units.len() != ascii.len() {
        return false;
    }
    units
        .iter()
        .zip(ascii.bytes())
        .all(|(&unit, expected)| unit <= 127 && (unit as u8).eq_ignore_ascii_case(&expected))
}

fn ends_with_ascii_ignore(units: &[u16], suffix: &str) -> bool {
    if units.len() <= suffix.len() {
        return false;
    }
    let start = units.len() - suffix.len();
    eq_ignore_ascii(&units[start..], suffix)
}

pub fn utf16_to_string(units: &[u16]) -> String {
    char::decode_utf16(units.iter().copied())
        .map(|item| item.unwrap_or('\u{FFFD}'))
        .collect()
}

// C# StringComparer.Ordinal 按 UTF-16 代码单元比较，补充平面字符不能按 UTF-8 字节序近似。
pub fn ordinal_cmp(left: &str, right: &str) -> std::cmp::Ordering {
    left.encode_utf16().cmp(right.encode_utf16())
}

fn upper_unit(unit: u16) -> u16 {
    let Some(ch) = char::from_u32(unit as u32) else {
        return unit;
    };
    let mut upper = ch.to_uppercase();
    let first = upper.next().unwrap_or(ch);
    if upper.next().is_none() {
        first as u32 as u16
    } else {
        unit
    }
}

// 对应对 C# `StringComparer.OrdinalIgnoreCase` 的逐 UTF-16 单元大写比较。
pub fn ordinal_ignore_case_cmp(left: &str, right: &str) -> std::cmp::Ordering {
    let mut left = left.encode_utf16();
    let mut right = right.encode_utf16();
    loop {
        match (left.next(), right.next()) {
            (None, None) => return std::cmp::Ordering::Equal,
            (None, Some(_)) => return std::cmp::Ordering::Less,
            (Some(_), None) => return std::cmp::Ordering::Greater,
            (Some(left), Some(right)) => {
                let left = upper_unit(left);
                let right = upper_unit(right);
                if left != right {
                    return left.cmp(&right);
                }
            }
        }
    }
}

pub fn starts_with_ordinal_ignore_case(value: &str, prefix: &str) -> bool {
    let mut value = value.encode_utf16();
    for expected in prefix.encode_utf16() {
        match value.next() {
            Some(actual) if upper_unit(actual) == upper_unit(expected) => {}
            _ => return false,
        }
    }
    true
}

// 对应 C# 的 OrdinalIgnoreCase 集合；快捷方式代表的可执行文件路径按此比较。
#[derive(Clone, Debug, Default)]
pub struct OrdinalIgnoreCaseSet {
    values: HashSet<Vec<u16>>,
}

impl OrdinalIgnoreCaseSet {
    pub fn from_values<'a>(values: impl IntoIterator<Item = &'a str>) -> Self {
        Self {
            values: values
                .into_iter()
                .map(|value| value.encode_utf16().map(upper_unit).collect())
                .collect(),
        }
    }

    pub fn contains(&self, value: &str) -> bool {
        thread_local! {
            static FOLD_BUFFER: RefCell<Vec<u16>> = const { RefCell::new(Vec::new()) };
        }
        FOLD_BUFFER.with(|buffer| {
            let mut buffer = buffer.borrow_mut();
            buffer.clear();
            buffer.extend(value.encode_utf16().map(upper_unit));
            self.values.get(buffer.as_slice()).is_some()
        })
    }

    pub fn is_empty(&self) -> bool {
        self.values.is_empty()
    }
}

#[derive(Clone, Debug)]
pub struct MaterializedName {
    pub parent: u64,
    pub name: String,
    pub namespace: u8,
    pub attributes: u32,
}

#[derive(Clone, Debug)]
pub struct MaterializedFile {
    pub file: u64,
    pub is_directory: bool,
    pub last_usn: Option<i64>,
    pub link_count: u16,
    pub names: Vec<MaterializedName>,
}

// live 路径只需父链与名称位置，避免为每个目录把全部名称转成 String。
#[derive(Clone, Copy, Debug)]
pub struct LightName {
    pub parent: u64,
    pub namespace: u8,
    pub offset: i32,
    pub length: i32,
}

#[derive(Clone, Debug)]
pub struct LightFile {
    pub is_directory: bool,
    pub names: Vec<LightName>,
}

// 按 VolumeIndex.Create(..., shareNameText: false) 的 v1 布局生成内存卷镜像；overlay 与测试共用。
pub fn build_volume_image(
    geometry: &VolumeGeometry,
    journal_id: u64,
    next_usn: i64,
    created_at_ticks: i64,
    files: &[MaterializedFile],
) -> io::Result<Vec<u8>> {
    let invalid = |message: &str| io::Error::new(io::ErrorKind::InvalidData, message.to_string());
    if geometry.root.len() != 3
        || !geometry.root.as_bytes()[0].is_ascii_uppercase()
        || &geometry.root[1..] != ":\\"
    {
        return Err(invalid("索引卷身份或根目录无效"));
    }
    let mut source = files.to_vec();
    source.sort_by_key(|file| file.file);
    let mut identities: HashMap<u64, usize> = HashMap::with_capacity(source.len());
    for (index, file) in source.iter().enumerate() {
        if identities.insert(file.file, index).is_some() {
            return Err(invalid("索引包含重复记录或不支持的文件身份"));
        }
    }
    struct BuiltName {
        parent: i32,
        offset: i32,
        length: i32,
        namespace: u8,
        attributes: u32,
    }
    let mut nodes: Vec<(u64, i32, i32, bool, Option<i64>, u16)> = Vec::with_capacity(source.len());
    let mut names: Vec<BuiltName> = Vec::new();
    let mut text: Vec<u16> = Vec::new();
    for file in &source {
        let first = names.len();
        let is_root = file.file & 0x0000_ffffffffffff == 5;
        let mut ordered: Vec<&MaterializedName> = file.names.iter().collect();
        ordered.sort_by(|left, right| {
            (left.namespace == 2)
                .cmp(&(right.namespace == 2))
                .then(left.parent.cmp(&right.parent))
                .then_with(|| ordinal_cmp(&left.name, &right.name))
        });
        ordered.dedup_by(|left, right| left.parent == right.parent && left.name == right.name);
        for name in ordered {
            let Some(&parent) = identities.get(&name.parent) else {
                return Err(invalid("索引名称的父目录缺失"));
            };
            if !source[parent].is_directory {
                return Err(invalid("索引名称的父目录不是目录"));
            }
            // 根目录记录允许 "." 与自引用；其他记录不允许。
            if name.namespace > 3
                || name.name.is_empty()
                || name.name.contains(['\\', '/', '\0'])
                || name.name == ".."
                || (!is_root && (name.name == "." || name.parent == file.file))
            {
                return Err(invalid("索引名称或父目录无效"));
            }
            let offset = text.len();
            let units: Vec<u16> = name.name.encode_utf16().collect();
            text.extend_from_slice(&units);
            names.push(BuiltName {
                parent: parent as i32,
                offset: offset as i32,
                length: units.len() as i32,
                namespace: name.namespace,
                attributes: name.attributes,
            });
        }
        if names.len() == first {
            return Err(invalid("索引文件记录缺少名称"));
        }
        nodes.push((
            file.file,
            first as i32,
            (names.len() - first) as i32,
            file.is_directory,
            file.last_usn,
            file.link_count,
        ));
    }
    let Some(root) = source.iter().position(|file| file.file & 0x0000_ffffffffffff == 5) else {
        return Err(invalid("索引缺少根目录记录"));
    };
    if !source[root].is_directory {
        return Err(invalid("索引根记录必须是目录"));
    }
    let mut bytes = Vec::new();
    bytes.extend_from_slice(MAGIC);
    push_i32(&mut bytes, 1);
    write_binary_string(&mut bytes, &geometry.root);
    push_u64(&mut bytes, geometry.serial);
    push_i32(&mut bytes, 0);
    push_i32(&mut bytes, 0);
    push_i32(&mut bytes, 0);
    push_i64(&mut bytes, 0);
    push_i64(&mut bytes, 0);
    push_u16(&mut bytes, 0);
    push_u16(&mut bytes, 0);
    push_u64(&mut bytes, journal_id);
    push_i64(&mut bytes, next_usn);
    push_i64(&mut bytes, created_at_ticks);
    push_i32(&mut bytes, nodes.len() as i32);
    push_i32(&mut bytes, names.len() as i32);
    push_i32(&mut bytes, text.len() as i32);
    for (file, first, count, directory, last_usn, link_count) in &nodes {
        push_u64(&mut bytes, *file);
        push_i32(&mut bytes, *first);
        push_i32(&mut bytes, *count);
        bytes.push(if *directory { 1 } else { 0 });
        bytes.push(if last_usn.is_some() { 1 } else { 0 });
        push_i64(&mut bytes, last_usn.unwrap_or(0));
        push_u16(&mut bytes, *link_count);
    }
    for name in &names {
        push_i32(&mut bytes, name.parent);
        push_i32(&mut bytes, name.offset);
        push_i32(&mut bytes, name.length);
        bytes.push(name.namespace);
        push_u32(&mut bytes, name.attributes);
    }
    for unit in &text {
        push_u16(&mut bytes, *unit);
    }
    let digest = Sha256::digest(&bytes);
    bytes.extend_from_slice(&digest);
    Ok(bytes)
}

fn write_binary_string(bytes: &mut Vec<u8>, value: &str) {
    let mut length = value.len();
    while length >= 0x80 {
        bytes.push((length as u8 & 0x7f) | 0x80);
        length >>= 7;
    }
    bytes.push(length as u8);
    bytes.extend_from_slice(value.as_bytes());
}

fn push_u16(bytes: &mut Vec<u8>, value: u16) {
    bytes.extend_from_slice(&value.to_le_bytes());
}
fn push_u32(bytes: &mut Vec<u8>, value: u32) {
    bytes.extend_from_slice(&value.to_le_bytes());
}
fn push_u64(bytes: &mut Vec<u8>, value: u64) {
    bytes.extend_from_slice(&value.to_le_bytes());
}
fn push_i32(bytes: &mut Vec<u8>, value: i32) {
    bytes.extend_from_slice(&value.to_le_bytes());
}
fn push_i64(bytes: &mut Vec<u8>, value: i64) {
    bytes.extend_from_slice(&value.to_le_bytes());
}

pub(crate) fn name_identity(units: &[u16]) -> String {
    let mut hash = Sha256::new();
    for unit in units {
        hash.update(unit.to_le_bytes());
    }
    hex_lower(&hash.finalize())
}

fn hex_lower(bytes: &[u8]) -> String {
    const H: &[u8] = b"0123456789abcdef";
    let mut text = String::with_capacity(bytes.len() * 2);
    for &byte in bytes {
        text.push(H[(byte >> 4) as usize] as char);
        text.push(H[(byte & 0xf) as usize] as char);
    }
    text
}

struct NamePresenceIndex {
    ascii: Vec<Option<Vec<u64>>>,
    unicode: HashMap<u32, Vec<i32>>,
    owners: Vec<i32>,
    words: usize,
}

impl NamePresenceIndex {
    fn build(volume: &VolumeIndex, cancel: CancelToken) -> Result<Self, Canceled> {
        let words = (volume.name_count as usize + 63) / 64;
        let mut owners = vec![0i32; volume.name_count as usize];
        let mut ascii: Vec<Option<Vec<u64>>> = (0..128).map(|_| None).collect();
        let mut unicode: HashMap<u32, Vec<i32>> = HashMap::new();
        for node_index in 0..volume.node_count {
            if node_index & 4095 == 0 {
                cancel.check()?;
            }
            let node = volume.node(node_index);
            let end = node.first_name + node.name_count;
            for index in node.first_name..end {
                owners[index as usize] = node_index;
                let name = volume.name(index);
                let units = volume.text_units(name.offset, name.length);
                let mut seen_low = 0u64;
                let mut seen_high = 0u64;
                for ch in utf16_runes(units) {
                    let upper = upper_invariant(ch) as u32;
                    if upper >= 128 {
                        let names = unicode.entry(upper).or_default();
                        if names.last().copied() != Some(index) {
                            names.push(index);
                        }
                        continue;
                    }
                    let bit = 1u64 << (upper & 63);
                    let seen = if upper < 64 {
                        &mut seen_low
                    } else {
                        &mut seen_high
                    };
                    if *seen & bit != 0 {
                        continue;
                    }
                    *seen |= bit;
                    let bitmap = ascii[upper as usize].get_or_insert_with(|| vec![0u64; words]);
                    bitmap[index as usize >> 6] |= 1u64 << (index & 63);
                }
            }
        }
        cancel.check()?;
        for letter in 0..128 {
            let Some(bitmap) = ascii[letter].as_ref() else {
                continue;
            };
            let mut count = 0usize;
            let maximum = words / 4;
            for (word, bits) in bitmap.iter().enumerate() {
                if word & 4095 == 0 {
                    cancel.check()?;
                }
                count += bits.count_ones() as usize;
                if count > maximum {
                    break;
                }
            }
            if count > maximum {
                continue;
            }
            let mut names = Vec::with_capacity(count);
            for (word, mut bits) in bitmap.iter().copied().enumerate() {
                if word & 4095 == 0 {
                    cancel.check()?;
                }
                while bits != 0 {
                    names.push((word * 64 + bits.trailing_zeros() as usize) as i32);
                    bits &= bits - 1;
                }
            }
            unicode.insert(letter as u32, names);
            ascii[letter] = None;
        }
        Ok(Self {
            ascii,
            unicode,
            owners,
            words,
        })
    }

    fn owner(&self, name: i32) -> i32 {
        self.owners[name as usize]
    }

    fn select(
        &self,
        query: &str,
        volume: &VolumeIndex,
        cancel: CancelToken,
    ) -> Result<Vec<i32>, Canceled> {
        if query.is_empty() {
            return Ok((0..volume.name_count).collect());
        }
        let mut letters = Vec::new();
        for ch in query.chars() {
            let upper = upper_invariant(ch) as u32;
            if !letters.contains(&upper) {
                letters.push(upper);
            }
        }
        let mut dense = Vec::new();
        let mut sparse = Vec::new();
        for letter in letters {
            if letter < 128 {
                if let Some(map) = self.ascii[letter as usize].as_ref() {
                    dense.push(map.as_slice());
                } else if let Some(names) = self.unicode.get(&letter) {
                    sparse.push(names.as_slice());
                } else {
                    return Ok(Vec::new());
                }
            } else if let Some(names) = self.unicode.get(&letter) {
                sparse.push(names.as_slice());
            } else {
                return Ok(Vec::new());
            }
        }
        let mut selected = vec![0u64; self.words];
        if sparse.is_empty() {
            for word in 0..self.words {
                if word & 4095 == 0 {
                    cancel.check()?;
                }
                let mut bits = u64::MAX;
                for map in &dense {
                    bits &= map[word];
                }
                selected[word] = bits;
            }
        } else {
            let smallest = sparse
                .iter()
                .min_by_key(|names| names.len())
                .copied()
                .unwrap();
            let mut scanned = 0;
            for &name in smallest {
                if scanned & 4095 == 0 {
                    cancel.check()?;
                }
                scanned += 1;
                let bit = 1u64 << (name & 63);
                let word = name as usize >> 6;
                if dense.iter().any(|map| map[word] & bit == 0) {
                    continue;
                }
                if sparse
                    .iter()
                    .filter(|names| !std::ptr::eq(**names, smallest))
                    .any(|names| names.binary_search(&name).is_err())
                {
                    continue;
                }
                selected[word] |= bit;
            }
        }
        for word in 0..self.words {
            if word & 4095 == 0 {
                cancel.check()?;
            }
            let mut bits = selected[word];
            while bits != 0 {
                let index = word * 64 + bits.trailing_zeros() as usize;
                bits &= bits - 1;
                if index >= self.owners.len() {
                    continue;
                }
                let name = volume.name(index as i32);
                if name.namespace != 2 {
                    continue;
                }
                let node = volume.node(self.owners[index]);
                let mut has_long = false;
                let end = node.first_name + node.name_count;
                for other in node.first_name..end {
                    let other_name = volume.name(other);
                    if other_name.parent != name.parent || other_name.namespace == 2 {
                        continue;
                    }
                    selected[other as usize >> 6] |= 1u64 << (other & 63);
                    has_long = true;
                }
                if has_long {
                    selected[word] &= !(1u64 << (index & 63));
                }
            }
        }
        let mut result = Vec::new();
        for word in 0..self.words {
            if word & 4095 == 0 {
                cancel.check()?;
            }
            let mut bits = selected[word];
            while bits != 0 {
                let index = word * 64 + bits.trailing_zeros() as usize;
                bits &= bits - 1;
                if index < self.owners.len() {
                    result.push(index as i32);
                }
            }
        }
        Ok(result)
    }
}

struct PhoneticEntry {
    node: i32,
    name: i32,
    prepared: PreparedApplicationName,
}

struct PhoneticNameIndex {
    entries: Vec<PhoneticEntry>,
}

impl PhoneticNameIndex {
    fn build(volume: &VolumeIndex, cancel: CancelToken) -> Result<Self, Canceled> {
        let mut entries = Vec::new();
        let mut shared: HashMap<String, PreparedApplicationName> = HashMap::new();
        for node_index in 0..volume.node_count {
            cancel.check()?;
            let node = volume.node(node_index);
            let end = node.first_name + node.name_count;
            for i in node.first_name..end {
                let name = volume.name(i);
                let mut raw = volume.text_units(name.offset, name.length);
                let raw_string = utf16_to_string(raw);
                if !PreparedApplicationName::can_contain_phonetic(&raw_string) {
                    continue;
                }
                if !node.is_directory()
                    && (ends_with_ascii_ignore(raw, ".lnk") || ends_with_ascii_ignore(raw, ".exe"))
                {
                    raw = &raw[..raw.len() - 4];
                }
                let text = utf16_to_string(raw);
                let prepared = if let Some(existing) = shared.get(&text) {
                    existing.clone()
                } else {
                    let value = PreparedApplicationName::new(text.clone());
                    shared.insert(text, value.clone());
                    value
                };
                entries.push(PhoneticEntry {
                    node: node_index,
                    name: i,
                    prepared,
                });
            }
        }
        Ok(Self { entries })
    }

    fn select(&self, query: &str) -> Vec<&PhoneticEntry> {
        if query.is_empty()
            || query.len() > 128
            || !query.chars().all(|ch| ch.is_ascii_alphanumeric())
        {
            return Vec::new();
        }
        let mut mask = 0u64;
        for letter in query.chars() {
            let lower = letter.to_ascii_lowercase();
            if ('a'..='z').contains(&lower) {
                mask |= 1u64 << (lower as u8 - b'a');
            } else {
                mask |= 1u64 << (26 + lower as u8 - b'0');
            }
        }
        self.entries
            .iter()
            .filter(|entry| entry.prepared.phonetic_letters() & mask == mask)
            .collect()
    }
}

fn utf16_runes(units: &[u16]) -> Vec<char> {
    char::decode_utf16(units.iter().copied())
        .map(|item| item.unwrap_or('\u{FFFD}'))
        .collect()
}

fn upper_invariant(ch: char) -> char {
    if ch.is_ascii() {
        ch.to_ascii_uppercase()
    } else {
        let mut upper = ch.to_uppercase();
        let first = upper.next().unwrap_or(ch);
        if upper.next().is_none() {
            first
        } else {
            ch
        }
    }
}

fn read_i32(data: &[u8], cur: &mut usize) -> io::Result<i32> {
    Ok(i32::from_le_bytes(read_array(data, cur)?))
}
fn read_i64(data: &[u8], cur: &mut usize) -> io::Result<i64> {
    Ok(i64::from_le_bytes(read_array(data, cur)?))
}
fn read_u16(data: &[u8], cur: &mut usize) -> io::Result<u16> {
    Ok(u16::from_le_bytes(read_array(data, cur)?))
}
fn read_u64(data: &[u8], cur: &mut usize) -> io::Result<u64> {
    Ok(u64::from_le_bytes(read_array(data, cur)?))
}

fn read_array<const N: usize>(data: &[u8], cur: &mut usize) -> io::Result<[u8; N]> {
    if *cur + N > data.len() {
        return Err(io::Error::new(
            io::ErrorKind::UnexpectedEof,
            "索引文件被截断",
        ));
    }
    let mut buf = [0u8; N];
    buf.copy_from_slice(&data[*cur..*cur + N]);
    *cur += N;
    Ok(buf)
}

fn read_binary_string(data: &[u8], cur: &mut usize) -> io::Result<String> {
    let mut value = 0u32;
    let mut shift = 0;
    loop {
        if *cur >= data.len() {
            return Err(io::Error::new(
                io::ErrorKind::UnexpectedEof,
                "索引文件被截断",
            ));
        }
        let byte = data[*cur];
        *cur += 1;
        value |= u32::from(byte & 0x7f) << shift;
        if byte < 0x80 {
            break;
        }
        shift += 7;
        if shift > 28 {
            return Err(io::Error::new(io::ErrorKind::InvalidData, "索引根路径无效"));
        }
    }
    let len = value as usize;
    if *cur + len > data.len() {
        return Err(io::Error::new(
            io::ErrorKind::UnexpectedEof,
            "索引文件被截断",
        ));
    }
    let text = std::str::from_utf8(&data[*cur..*cur + len])
        .map_err(|_| io::Error::new(io::ErrorKind::InvalidData, "索引根路径无效"))?
        .to_string();
    *cur += len;
    Ok(text)
}
