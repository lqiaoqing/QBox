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
