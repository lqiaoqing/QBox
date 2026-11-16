//! 内存版 `SelectionHistory` / `HistoryView`，覆盖校验所需的查询规范化与信号计算。

use std::collections::HashMap;

pub const QUERIES_PER_TARGET: usize = 8;
pub const RECENT_QUERY_LIMIT: usize = 500;

#[derive(Clone, Copy, Debug, Default, PartialEq, Eq, PartialOrd, Ord)]
pub struct Instant(pub i64);

impl Instant {
    pub fn from_unix_seconds(seconds: i64) -> Self {
        Self(seconds)
    }

    pub fn now() -> Self {
        use std::time::{SystemTime, UNIX_EPOCH};
        let secs = SystemTime::now()
            .duration_since(UNIX_EPOCH)
            .map(|value| value.as_secs() as i64)
            .unwrap_or(0);
        Self(secs)
    }
}

#[derive(Clone, Copy, Debug, Default, PartialEq, Eq)]
pub struct HistorySignals {
    pub exact_query: Option<Instant>,
    pub prefix_query: Option<Instant>,
    pub last_used: Option<Instant>,
}

#[derive(Clone, Debug)]
struct QueryUse {
    query: String,
    last_used: Instant,
}

#[derive(Clone, Debug)]
struct TargetUse {
    last_used: Instant,
    queries: Vec<QueryUse>,
}

#[derive(Clone, Debug, Default)]
pub struct SelectionHistory {
    targets: HashMap<String, TargetUse>,
}

impl SelectionHistory {
    pub fn new() -> Self {
        Self::default()
    }

    pub fn for_query(&self, query: &str) -> HistoryView<'_> {
        HistoryView {
            targets: &self.targets,
            query: normalize_query(query),
        }
    }

    pub fn record_selection(&mut self, target_id: &str, query: &str, selected_at: Option<Instant>) {
        assert!(!target_id.trim().is_empty(), "targetId 不能为空");
        let normalized = normalize_query(query);
        let now = selected_at.unwrap_or_else(Instant::now);
        let old = self.targets.get(target_id).cloned();
        let mut queries = old
            .as_ref()
            .map(|target| target.queries.clone())
            .unwrap_or_default();
        if !normalized.is_empty() {
            let previous = queries
                .iter()
                .find(|item| item.query == normalized)
                .map(|item| item.last_used);
            let time = match previous {
                Some(previous) if previous > now => previous,
                _ => now,
            };
            queries.retain(|item| item.query != normalized);
            queries.insert(
                0,
                QueryUse {
                    query: normalized,
                    last_used: time,
                },
            );
            queries.truncate(QUERIES_PER_TARGET);
        }
        let last_used = match old {
            Some(old) if old.last_used > now => old.last_used,
            _ => now,
        };
        self.targets
            .insert(target_id.to_string(), TargetUse { last_used, queries });
    }
}

pub struct HistoryView<'a> {
    targets: &'a HashMap<String, TargetUse>,
    query: String,
}

impl HistoryView<'_> {
    pub fn get(&self, target_id: &str) -> HistorySignals {
        let Some(target) = self.targets.get(target_id) else {
            return HistorySignals::default();
        };
        let mut exact = None;
        let mut prefix = None;
        if !self.query.is_empty() {
            for use_item in &target.queries {
                if use_item.query == self.query {
                    exact = Some(use_item.last_used);
                } else if use_item.query.starts_with(&self.query)
                    && prefix.is_none_or(|current| use_item.last_used > current)
                {
                    prefix = Some(use_item.last_used);
                }
            }
        }
        HistorySignals {
            exact_query: exact,
            prefix_query: if exact.is_some() { None } else { prefix },
            last_used: Some(target.last_used),
        }
    }
}

// 一次查询的历史快照：C# 按 history.ForQuery(query) 计算好信号后以二进制传给 Rust。
#[derive(Clone, Debug, Default)]
pub struct QueryHistory {
    signals: HashMap<String, HistorySignals>,
}

impl QueryHistory {
    pub fn new() -> Self {
        Self::default()
    }

    pub fn from_signals(entries: impl IntoIterator<Item = (String, HistorySignals)>) -> Self {
        Self {
            signals: entries.into_iter().collect(),
        }
    }

    pub fn get(&self, target_id: &str) -> HistorySignals {
        self.signals.get(target_id).copied().unwrap_or_default()
    }

    pub fn enumerate(&self) -> impl Iterator<Item = (&str, HistorySignals)> {
        self.signals
            .iter()
            .map(|(target, signals)| (target.as_str(), *signals))
    }

    pub fn is_empty(&self) -> bool {
        self.signals.is_empty()
    }
}

pub trait HistoryLookup {
    fn lookup(&self, target_id: &str) -> HistorySignals;
}

impl HistoryLookup for HistoryView<'_> {
    fn lookup(&self, target_id: &str) -> HistorySignals {
        self.get(target_id)
    }
}

impl HistoryLookup for QueryHistory {
    fn lookup(&self, target_id: &str) -> HistorySignals {
        self.get(target_id)
    }
}

pub const HISTORY_BLOB_MAGIC: &[u8; 8] = b"QBXHST01";

// 历史 blob 布局：magic、u32 版本、u32 条目数；每项 u32 标识长度 + UTF-16 标识、
// u8 有效位（bit0 exact / bit1 prefix / bit2 last），按位依次跟 i64 UTC ticks。
pub fn parse_history_blob(bytes: &[u8]) -> Result<QueryHistory, String> {
    if bytes.is_empty() {
        return Ok(QueryHistory::new());
    }
    if bytes.len() < 16 || &bytes[..8] != HISTORY_BLOB_MAGIC {
        return Err("历史快照格式无效".to_string());
    }
    let mut cursor = 8;
    let version = read_u32(bytes, &mut cursor)?;
    if version != 1 {
        return Err(format!("不支持的历史快照版本：{version}"));
    }
    let count = read_u32(bytes, &mut cursor)? as usize;
    let mut entries = Vec::with_capacity(count);
    for _ in 0..count {
        let length = read_u32(bytes, &mut cursor)? as usize;
        let byte_length = length
            .checked_mul(2)
            .ok_or_else(|| "历史快照标识长度无效".to_string())?;
        if cursor + byte_length > bytes.len() {
            return Err("历史快照标识被截断".to_string());
        }
        let units: Vec<u16> = bytes[cursor..cursor + byte_length]
            .chunks_exact(2)
            .map(|pair| u16::from_le_bytes([pair[0], pair[1]]))
            .collect();
        cursor += byte_length;
        let id = char::decode_utf16(units)
            .map(|item| item.unwrap_or('\u{FFFD}'))
            .collect::<String>();
        if cursor >= bytes.len() {
            return Err("历史快照信号被截断".to_string());
        }
        let flags = bytes[cursor];
        cursor += 1;
        let mut read_signal = |bit: u8| -> Result<Option<Instant>, String> {
            if flags & bit == 0 {
                return Ok(None);
            }
            let value = read_i64(bytes, &mut cursor)?;
            Ok(Some(Instant(value)))
        };
        let exact_query = read_signal(1)?;
        let prefix_query = read_signal(2)?;
        let last_used = read_signal(4)?;
        entries.push((
            id,
            HistorySignals {
                exact_query,
                prefix_query,
                last_used,
            },
        ));
    }
    if cursor != bytes.len() {
        return Err("历史快照存在多余数据".to_string());
    }
    Ok(QueryHistory::from_signals(entries))
}

pub const HISTORY_SET_MAGIC: &[u8; 8] = b"QBXHSB01";

// 对照宿主的容器：按查询名存放单查询历史 blob。
pub fn parse_history_blob_set(bytes: &[u8]) -> Result<HashMap<String, QueryHistory>, String> {
    if bytes.len() < 16 || &bytes[..8] != HISTORY_SET_MAGIC {
        return Err("历史快照集合格式无效".to_string());
    }
    let mut cursor = 8;
    let version = read_u32(bytes, &mut cursor)?;
    if version != 1 {
        return Err(format!("不支持的历史快照集合版本：{version}"));
    }
    let count = read_u32(bytes, &mut cursor)? as usize;
    let mut result = HashMap::with_capacity(count);
    for _ in 0..count {
        let query_length = read_u32(bytes, &mut cursor)? as usize;
        let query_bytes = query_length
            .checked_mul(2)
            .ok_or_else(|| "历史快照查询长度无效".to_string())?;
        if cursor + query_bytes > bytes.len() {
            return Err("历史快照集合被截断".to_string());
        }
        let query = char::decode_utf16(
            bytes[cursor..cursor + query_bytes]
                .chunks_exact(2)
                .map(|pair| u16::from_le_bytes([pair[0], pair[1]])),
        )
        .map(|item| item.unwrap_or('\u{FFFD}'))
        .collect::<String>();
        cursor += query_bytes;
        let blob_length = read_u32(bytes, &mut cursor)? as usize;
        if cursor + blob_length > bytes.len() {
            return Err("历史快照集合被截断".to_string());
        }
        let history = parse_history_blob(&bytes[cursor..cursor + blob_length])?;
        cursor += blob_length;
        result.insert(query, history);
    }
    if cursor != bytes.len() {
        return Err("历史快照集合存在多余数据".to_string());
    }
    Ok(result)
}

fn read_u32(bytes: &[u8], cursor: &mut usize) -> Result<u32, String> {
    if *cursor + 4 > bytes.len() {
        return Err("历史快照被截断".to_string());
    }
    let value = u32::from_le_bytes(bytes[*cursor..*cursor + 4].try_into().unwrap());
    *cursor += 4;
    Ok(value)
}

fn read_i64(bytes: &[u8], cursor: &mut usize) -> Result<i64, String> {
    if *cursor + 8 > bytes.len() {
        return Err("历史快照被截断".to_string());
    }
    let value = i64::from_le_bytes(bytes[*cursor..*cursor + 8].try_into().unwrap());
    *cursor += 8;
    Ok(value)
}

pub fn normalize_query(query: &str) -> String {
    query.trim().to_lowercase()
}

pub fn nullable_cmp<T: Ord>(left: Option<T>, right: Option<T>) -> std::cmp::Ordering {
    match (left, right) {
        (None, None) => std::cmp::Ordering::Equal,
        (None, Some(_)) => std::cmp::Ordering::Less,
        (Some(_), None) => std::cmp::Ordering::Greater,
        (Some(left), Some(right)) => left.cmp(&right),
    }
}
