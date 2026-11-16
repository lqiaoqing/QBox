//! 只读映射 `QBXNTFS1` 索引，布局与 `VolumeIndexStore` / `MappedIndexStorage` 一致，不写回文件。

use std::collections::HashMap;
use std::fs::File;
use std::io;
use std::path::{Path, PathBuf};
use std::sync::Arc;

use memmap2::{Mmap, MmapOptions};
use sha2::{Digest, Sha256};

use crate::cancel::{CancelToken, Canceled};
use crate::fuzzy::PreparedQuery;
use crate::history::HistorySignals;
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

pub struct VolumeIndex {
    pub path: PathBuf,
    pub format_version: i32,
    pub geometry: VolumeGeometry,
    mmap: Mmap,
    nodes_offset: usize,
    names_offset: usize,
    text_offset: usize,
    pub node_count: i32,
    pub name_count: i32,
    pub text_count: i32,
    pub root_node: i32,
    presence: Option<NamePresenceIndex>,
    phonetic: Option<PhoneticNameIndex>,
}

impl VolumeIndex {
    pub fn load(path: impl AsRef<Path>) -> io::Result<Self> {
        let path = path.as_ref().to_path_buf();
        let file = File::open(&path)?;
        let mmap = unsafe { MmapOptions::new().map(&file)? };
        if mmap.len() < 128 {
            return Err(io::Error::new(io::ErrorKind::InvalidData, "索引文件被截断"));
        }
        let body_len = mmap.len() - 32;
        let actual = Sha256::digest(&mmap[..body_len]);
        if actual.as_slice() != &mmap[body_len..] {
            return Err(io::Error::new(io::ErrorKind::InvalidData, "索引校验失败"));
        }
        if &mmap[..8] != MAGIC {
            return Err(io::Error::new(
                io::ErrorKind::InvalidData,
                "索引格式版本无效",
            ));
        }
        let mut cur = 8;
        let version = read_i32(&mmap, &mut cur)?;
        if version != 1 && version != 2 {
            return Err(io::Error::new(
                io::ErrorKind::InvalidData,
                "索引格式版本无效",
            ));
        }
        let root = read_binary_string(&mmap, &mut cur)?;
        let serial = read_u64(&mmap, &mut cur)?;
        let _sector = read_i32(&mmap, &mut cur)?;
        let _cluster = read_i32(&mmap, &mut cur)?;
        let _record = read_i32(&mmap, &mut cur)?;
        let _mft_valid = read_i64(&mmap, &mut cur)?;
        let _mft_start = read_i64(&mmap, &mut cur)?;
        let _major = read_u16(&mmap, &mut cur)?;
        let _minor = read_u16(&mmap, &mut cur)?;
        let _journal = read_u64(&mmap, &mut cur)?;
        let _usn = read_i64(&mmap, &mut cur)?;
        let _ticks = read_i64(&mmap, &mut cur)?;
        let node_count = read_i32(&mmap, &mut cur)?;
        let name_count = read_i32(&mmap, &mut cur)?;
        let text_count = read_i32(&mmap, &mut cur)?;
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
        let mut volume = Self {
            path,
            format_version: version,
            geometry: VolumeGeometry { root, serial },
            mmap,
            nodes_offset,
            names_offset,
            text_offset,
            node_count,
            name_count,
            text_count,
            root_node: -1,
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
        let b = &self.mmap[at..at + 28];
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
        let b = &self.mmap[at..at + 17];
        IndexName {
            parent: i32::from_le_bytes(b[0..4].try_into().unwrap()),
            offset: i32::from_le_bytes(b[4..8].try_into().unwrap()),
            length: i32::from_le_bytes(b[8..12].try_into().unwrap()),
            namespace: b[12],
            attributes: u32::from_le_bytes(b[13..17].try_into().unwrap()),
        }
    }

    pub fn text_units(&self, offset: i32, length: i32) -> &[u16] {
        let start = self.text_offset + offset as usize * 2;
        let bytes = &self.mmap[start..start + length as usize * 2];
        // UTF-16 单元与 C# char 偏移一致；小端平台直接重解释。
        unsafe { std::slice::from_raw_parts(bytes.as_ptr() as *const u16, length as usize) }
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
        cancel.check()?;
        let presence = self.presence.as_ref().expect("必须先 PrepareSearch");
        let phonetic = self.phonetic.as_ref().expect("必须先 PrepareSearch");
        let source: Arc<dyn UniqueSearchSource> = Arc::new(VolumeSource {
            volume: Arc::clone(self),
        });
        let query_text = collector.query().text().to_string();
        let query = PreparedQuery::new(&query_text);
        for i in presence.select(query.text(), self, cancel)? {
            cancel.check()?;
            let node_index = presence.owner(i);
            if node_index == self.root_node {
                continue;
            }
            let node = self.node(node_index);
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
            let friendly_match = query.can_match_utf16(friendly, cancel)?;
            let display = if friendly_match {
                friendly
            } else if (shortcut || executable) && query.can_match_utf16(raw, cancel)? {
                raw
            } else if let Some(alias) = matching_dos_alias(self, node, name.parent, &query, cancel)?
            {
                let alias_name = self.name(alias);
                self.text_units(alias_name.offset, alias_name.length)
            } else {
                continue;
            };
            let display_text = utf16_to_string(display);
            let application = executable
                && friendly_match
                && !query.text().to_ascii_lowercase().contains(".exe")
                && !in_package_directory(self, name.parent)
                && query.score_utf16(display, cancel)? > 1;
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
            collector.add_matched(
                source.clone(),
                key,
                &display_text,
                kind,
                0,
                HistorySignals::default(),
                application,
                application,
                None,
                false,
            )?;
        }
        for entry in phonetic.select(query.text()) {
            let node = self.node(entry.node);
            let name = self.name(entry.name);
            if entry.node == self.root_node {
                continue;
            }
            if name.namespace == 2 && has_long_name(self, node, name.parent) {
                continue;
            }
            let raw = self.text_units(name.offset, name.length);
            let display = entry.prepared.text();
            if query.can_match(display)
                || query.can_match_utf16(raw, cancel)?
                || matching_dos_alias(self, node, name.parent, &query, cancel)?.is_some()
            {
                continue;
            }
            let match_value = entry.prepared.match_phonetic(query.text(), true, cancel)?;
            if match_value.score == 0 {
                continue;
            }
            let path = path_for(self, entry.name);
            let shortcut = !node.is_directory() && ends_with_ascii_ignore(raw, ".lnk");
            let executable = !node.is_directory() && ends_with_ascii_ignore(raw, ".exe");
            let application = executable && !in_package_path(self, &path);
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
            collector.add_matched(
                source.clone(),
                key,
                display,
                kind,
                0,
                HistorySignals::default(),
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
        SearchCandidate::new(
            self.identity_at(key),
            name,
            path_for(&self.volume, key as u32 as i32),
            kind,
        )
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

fn in_package_path(volume: &VolumeIndex, path: &str) -> bool {
    let root = &volume.geometry.root;
    let windows_apps = format!("{root}WindowsApps\\");
    let program_files = format!("{root}Program Files\\WindowsApps\\");
    starts_with_ignore_ascii(path, &windows_apps) || starts_with_ignore_ascii(path, &program_files)
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

fn name_identity(units: &[u16]) -> String {
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
