//! 忠实移植收集器、分桶次序与内存 SearchEngine，避免并行扫描改变收集序号。

use std::cmp::Ordering;
use std::collections::BinaryHeap;
use std::sync::Arc;

use crate::cancel::{CancelToken, Canceled};
use crate::fuzzy::{FuzzyMatcher, MatchResult, PreparedQuery};
use crate::history::{nullable_cmp, HistorySignals, HistoryView, SelectionHistory};

#[derive(Clone, Copy, Debug, PartialEq, Eq, PartialOrd, Ord)]
pub enum SearchItemKind {
    File = 0,
    Directory = 1,
    Application = 2,
    Shortcut = 3,
}

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum RankGroup {
    ExactQueryHistory,
    PrefixQueryHistory,
    Ordinary,
    ApplicationEntry,
    ExecutableEntry,
}

#[derive(Clone, Copy, Debug, PartialEq, Eq, PartialOrd, Ord)]
pub enum LauncherLane {
    ExactHistory = 0,
    PrefixHistoryOrApplication = 1,
    DirectExecutable = 2,
    StrongDirectory = 3,
    Mixed = 4,
    WeakDirectory = 5,
    Supplemental = 6,
}

impl LauncherLane {
    pub const ALL: [LauncherLane; 7] = [
        Self::ExactHistory,
        Self::PrefixHistoryOrApplication,
        Self::DirectExecutable,
        Self::StrongDirectory,
        Self::Mixed,
        Self::WeakDirectory,
        Self::Supplemental,
    ];

    pub fn from_index(index: usize) -> Self {
        Self::ALL[index]
    }
}

#[derive(Clone, Debug, PartialEq, Eq)]
pub struct SearchCandidate {
    pub id: String,
    pub name: String,
    pub target: String,
    pub kind: SearchItemKind,
    pub arguments: Option<String>,
    pub normal_priority: u64,
    pub is_application_entry: bool,
    pub app_user_model_id: Option<String>,
    pub is_direct_executable: bool,
}

impl SearchCandidate {
    pub fn new(
        id: impl Into<String>,
        name: impl Into<String>,
        target: impl Into<String>,
        kind: SearchItemKind,
    ) -> Self {
        Self {
            id: id.into(),
            name: name.into(),
            target: target.into(),
            kind,
            arguments: None,
            normal_priority: 0,
            is_application_entry: false,
            app_user_model_id: None,
            is_direct_executable: false,
        }
    }

    pub fn application(
        id: impl Into<String>,
        name: impl Into<String>,
        target: impl Into<String>,
    ) -> Self {
        let mut value = Self::new(id, name, target, SearchItemKind::Application);
        value.is_application_entry = true;
        value
    }
}

#[derive(Clone, Debug)]
pub struct SearchHit {
    pub candidate: SearchCandidate,
    pub match_result: MatchResult,
    pub group: RankGroup,
    pub history: HistorySignals,
}

#[derive(Clone, Debug)]
pub struct SearchResponse {
    pub query: String,
    pub total_matches: i32,
    pub items: Vec<SearchHit>,
}

pub trait UniqueSearchSource: Send + Sync {
    fn materialize(
        &self,
        key: u64,
        name: &str,
        kind: SearchItemKind,
        priority: u64,
    ) -> SearchCandidate;
    fn identity(&self, key: u64) -> String;
}

pub struct SliceSource {
    pub items: Vec<SearchCandidate>,
}

impl UniqueSearchSource for SliceSource {
    fn materialize(
        &self,
        key: u64,
        _name: &str,
        _kind: SearchItemKind,
        _priority: u64,
    ) -> SearchCandidate {
        self.items[key as usize].clone()
    }
    fn identity(&self, key: u64) -> String {
        self.items[key as usize].id.clone()
    }
}

pub struct TestFileSource;

impl UniqueSearchSource for TestFileSource {
    fn materialize(
        &self,
        key: u64,
        name: &str,
        kind: SearchItemKind,
        _priority: u64,
    ) -> SearchCandidate {
        SearchCandidate::new(format!("test:{key}"), name, "test", kind)
    }
    fn identity(&self, key: u64) -> String {
        format!("test:{key}")
    }
}

pub struct CatalogSource {
    pub items: Vec<SearchCandidate>,
}

impl UniqueSearchSource for CatalogSource {
    fn materialize(
        &self,
        key: u64,
        name: &str,
        kind: SearchItemKind,
        priority: u64,
    ) -> SearchCandidate {
        let _ = (kind, priority);
        let mut candidate = self.items[key as usize].clone();
        candidate.name = name.to_string();
        candidate
    }
    fn identity(&self, key: u64) -> String {
        self.items[key as usize].id.clone()
    }
}

pub fn mixed_key(normal_tag: bool, normal_key: u64, has_score: bool, score: i32, kind: u8) -> u64 {
    if !normal_tag {
        return if has_score { score.max(0) as u64 } else { 0 };
    }
    if normal_key != 0 {
        return normal_key;
    }
    if kind != 1 {
        return if has_score { score.max(0) as u64 } else { 0 };
    }
    if has_score {
        let clamped = (score as i64).clamp(-0x800000, 0x7fffff) + 0x800000;
        (0x10000000u64 | clamped as u64) << 32
    } else {
        0x1080000000000000
    }
}

pub fn mixed_key_item(kind: SearchItemKind, normal_key: u64, score: i32) -> u64 {
    let kind_byte = if matches!(kind, SearchItemKind::Shortcut | SearchItemKind::Application) {
        3
    } else {
        1
    };
    mixed_key(true, normal_key, true, score, kind_byte)
}

pub fn compare_native(
    mode: &str,
    left_tag: bool,
    left_key: u64,
    left_score_tag: bool,
    left_score: i32,
    left_kind: u8,
    left_used: Option<i64>,
    right_tag: bool,
    right_key: u64,
    right_score_tag: bool,
    right_score: i32,
    right_kind: u8,
    right_used: Option<i64>,
) -> i32 {
    let compare = if mode == "mixed" {
        mixed_key(
            right_tag,
            right_key,
            right_score_tag,
            right_score,
            right_kind,
        )
        .cmp(&mixed_key(
            left_tag,
            left_key,
            left_score_tag,
            left_score,
            left_kind,
        ))
    } else {
        let right = if right_score_tag { right_score } else { 0 };
        let left = if left_score_tag { left_score } else { 0 };
        right.cmp(&left)
    };
    match if compare != Ordering::Equal {
        compare
    } else {
        nullable_cmp(right_used, left_used)
    } {
        Ordering::Less => -1,
        Ordering::Equal => 0,
        Ordering::Greater => 1,
    }
}

pub fn lane(
    kind: SearchItemKind,
    application: bool,
    score: i32,
    history: HistorySignals,
    direct_executable: bool,
) -> LauncherLane {
    if history.exact_query.is_some() {
        LauncherLane::ExactHistory
    } else if history.prefix_query.is_some() {
        LauncherLane::PrefixHistoryOrApplication
    } else if application {
        if direct_executable {
            LauncherLane::DirectExecutable
        } else {
            LauncherLane::PrefixHistoryOrApplication
        }
    } else if kind == SearchItemKind::Directory {
        if score > 1 {
            LauncherLane::StrongDirectory
        } else {
            LauncherLane::WeakDirectory
        }
    } else {
        LauncherLane::Mixed
    }
}

pub fn group(history: HistorySignals, application: bool, direct_executable: bool) -> RankGroup {
    if history.exact_query.is_some() {
        RankGroup::ExactQueryHistory
    } else if history.prefix_query.is_some() {
        RankGroup::PrefixQueryHistory
    } else if application {
        if direct_executable {
            RankGroup::ExecutableEntry
        } else {
            RankGroup::ApplicationEntry
        }
    } else {
        RankGroup::Ordinary
    }
}

pub fn compare_lane(
    lane: LauncherLane,
    left_kind: SearchItemKind,
    left_key: u64,
    left_score: i32,
    left: HistorySignals,
    right_kind: SearchItemKind,
    right_key: u64,
    right_score: i32,
    right: HistorySignals,
) -> Ordering {
    // 原程序的历史桶只按关联时间排序；无历史的应用位于此前缀桶的末尾。
    if lane == LauncherLane::ExactHistory {
        return nullable_cmp(right.exact_query, left.exact_query);
    }
    if lane == LauncherLane::PrefixHistoryOrApplication {
        let time = nullable_cmp(right.prefix_query, left.prefix_query);
        if time != Ordering::Equal || left.prefix_query.is_some() {
            return time;
        }
        // QBox 合并多个应用来源时按匹配分值择优，不能让先遍历到的来源占满展示区。
        return right_score.cmp(&left_score);
    }
    let compare = if lane == LauncherLane::Mixed {
        mixed_key_item(right_kind, right_key, right_score)
            .cmp(&mixed_key_item(left_kind, left_key, left_score))
    } else {
        right_score.cmp(&left_score)
    };
    if compare != Ordering::Equal {
        compare
    } else {
        nullable_cmp(right.last_used, left.last_used)
    }
}

pub fn merge_lanes<T: Clone>(lanes: &[Vec<T>]) -> Vec<T> {
    let mixed = &lanes[LauncherLane::Mixed as usize];
    let take = mixed.len().min(10);
    let mut result = Vec::new();
    result.extend(lanes[LauncherLane::ExactHistory as usize].iter().cloned());
    result.extend(
        lanes[LauncherLane::PrefixHistoryOrApplication as usize]
            .iter()
            .cloned(),
    );
    result.extend(
        lanes[LauncherLane::DirectExecutable as usize]
            .iter()
            .cloned(),
    );
    result.extend(
        lanes[LauncherLane::StrongDirectory as usize]
            .iter()
            .cloned(),
    );
    result.extend(mixed.iter().take(take).cloned());
    result.extend(lanes[LauncherLane::WeakDirectory as usize].iter().cloned());
    result.extend(mixed.iter().skip(take).cloned());
    result.extend(lanes[LauncherLane::Supplemental as usize].iter().cloned());
    result
}

#[derive(Clone)]
struct Row {
    source: Arc<dyn UniqueSearchSource>,
    key: u64,
    name: String,
    kind: SearchItemKind,
    priority: u64,
    score: i32,
    lane: LauncherLane,
    history: HistorySignals,
    application_entry: bool,
    direct_executable: bool,
    display_match: Option<MatchResult>,
    ordinal: i64,
}

fn best_first(left: &Row, right: &Row) -> Ordering {
    best_first_keys(
        left.lane,
        left.kind,
        left.priority,
        left.score,
        left.history,
        left.ordinal,
        right,
    )
}

fn best_first_keys(
    left_lane: LauncherLane,
    left_kind: SearchItemKind,
    left_priority: u64,
    left_score: i32,
    left_history: crate::history::HistorySignals,
    left_ordinal: i64,
    right: &Row,
) -> Ordering {
    let compare = left_lane.cmp(&right.lane);
    if compare != Ordering::Equal {
        return compare;
    }
    let compare = compare_lane(
        left_lane,
        left_kind,
        left_priority,
        left_score,
        left_history,
        right.kind,
        right.priority,
        right.score,
        right.history,
    );
    if compare != Ordering::Equal {
        compare
    } else {
        left_ordinal.cmp(&right.ordinal)
    }
}

#[derive(Clone)]
struct HeapRow(Row);

impl PartialEq for HeapRow {
    fn eq(&self, other: &Self) -> bool {
        best_first(&self.0, &other.0) == Ordering::Equal
    }
}
impl Eq for HeapRow {}
impl PartialOrd for HeapRow {
    fn partial_cmp(&self, other: &Self) -> Option<Ordering> {
        Some(self.cmp(other))
    }
}
impl Ord for HeapRow {
    fn cmp(&self, other: &Self) -> Ordering {
        // 更差的行更大，使二叉堆的 peek 对应 C# PriorityQueue 中 WorstFirst 的最小值。
        best_first(&self.0, &other.0)
    }
}

enum NameRef<'a> {
    Utf8(&'a str),
    Utf16(&'a [u16]),
}

impl NameRef<'_> {
    fn score_upper_bound(&self, query: &PreparedQuery<'_>) -> i32 {
        match self {
            Self::Utf8(name) => query.score_upper_bound(name),
            Self::Utf16(name) => query.score_upper_bound_utf16(name),
        }
    }

    fn score_matched(
        &self,
        query: &PreparedQuery<'_>,
        cancel: crate::cancel::CancelToken,
    ) -> Result<i32, crate::cancel::Canceled> {
        match self {
            Self::Utf8(name) => query.score_matched_with(name, cancel),
            Self::Utf16(name) => query.score_matched_utf16(name, cancel),
        }
    }

    fn to_owned_name(&self) -> String {
        match self {
            Self::Utf8(name) => name.to_string(),
            Self::Utf16(name) => crate::qbi::utf16_to_string(name),
        }
    }
}

pub struct SearchCollector<'a> {
    limit: i32,
    cancel: CancelToken,
    query: PreparedQuery<'a>,
    best: [BinaryHeap<HeapRow>; 7],
    total: i32,
}

impl<'a> SearchCollector<'a> {
    pub fn new(query: &'a str, limit: i32) -> Self {
        Self::new_with(query, limit, CancelToken::NONE)
    }

    pub fn new_with(query: &'a str, limit: i32, cancel: CancelToken) -> Self {
        assert!(limit >= 0, "limit 不能为负");
        Self {
            limit,
            cancel,
            query: FuzzyMatcher.prepare(query),
            best: std::array::from_fn(|_| BinaryHeap::new()),
            total: 0,
        }
    }

    pub fn query(&self) -> &PreparedQuery<'a> {
        &self.query
    }

    pub fn add(
        &mut self,
        source: &Arc<dyn UniqueSearchSource>,
        key: u64,
        name: &str,
        kind: SearchItemKind,
        priority: u64,
        history: HistorySignals,
        application_entry: bool,
        direct_executable: bool,
    ) -> Result<(), Canceled> {
        self.cancel.check()?;
        if !self.query.can_match_with(name, self.cancel)? {
            return Ok(());
        }
        self.add_matched(
            source,
            key,
            name,
            kind,
            priority,
            history,
            application_entry,
            direct_executable,
            None,
            false,
        )
    }

    pub fn add_matched(
        &mut self,
        source: &Arc<dyn UniqueSearchSource>,
        key: u64,
        name: &str,
        kind: SearchItemKind,
        priority: u64,
        history: HistorySignals,
        application_entry: bool,
        direct_executable: bool,
        display_match: Option<MatchResult>,
        supplemental: bool,
    ) -> Result<(), Canceled> {
        self.add_matched_name(
            source,
            key,
            NameRef::Utf8(name),
            kind,
            priority,
            history,
            application_entry,
            direct_executable,
            display_match,
            supplemental,
        )
    }

    pub fn add_matched_utf16(
        &mut self,
        source: &Arc<dyn UniqueSearchSource>,
        key: u64,
        name: &[u16],
        kind: SearchItemKind,
        priority: u64,
        history: HistorySignals,
        application_entry: bool,
        direct_executable: bool,
        display_match: Option<MatchResult>,
        supplemental: bool,
    ) -> Result<(), Canceled> {
        self.add_matched_name(
            source,
            key,
            NameRef::Utf16(name),
            kind,
            priority,
            history,
            application_entry,
            direct_executable,
            display_match,
            supplemental,
        )
    }

    fn add_matched_name(
        &mut self,
        source: &Arc<dyn UniqueSearchSource>,
        key: u64,
        name: NameRef<'_>,
        kind: SearchItemKind,
        priority: u64,
        history: HistorySignals,
        application_entry: bool,
        direct_executable: bool,
        display_match: Option<MatchResult>,
        supplemental: bool,
    ) -> Result<(), Canceled> {
        self.cancel.check()?;
        if let Some(display) = &display_match {
            if !display.is_match || display.alignment_score <= 0 {
                panic!("补充匹配必须具有有效分值");
            }
        }
        self.total = self.total.checked_add(1).expect("总命中计数溢出");
        if self.limit == 0 {
            return Ok(());
        }
        let lane_of = |value: i32| -> LauncherLane {
            if supplemental && history.exact_query.is_none() && history.prefix_query.is_none() {
                LauncherLane::Supplemental
            } else {
                lane(kind, application_entry, value, history, direct_executable)
            }
        };
        // 新增拼音缩写放在旧结果后；选择后的原历史桶仍生效，避免改变已认可的英文首屏。
        let earliest = lane_of(i32::MAX);
        if self.capacity(earliest) == 0 {
            return Ok(());
        }
        let upper = display_match
            .as_ref()
            .map(|value| value.alignment_score)
            .unwrap_or_else(|| name.score_upper_bound(&self.query));
        let upper_lane = lane_of(upper);
        let ordinal = self.total as i64;
        // 探测是否值得打分时只比较分桶键，不为名字分配 String。
        if !self.would_enter(upper_lane, kind, priority, upper, history, ordinal)
            && (upper_lane != LauncherLane::StrongDirectory
                || !self.would_enter(
                    LauncherLane::WeakDirectory,
                    kind,
                    priority,
                    1,
                    history,
                    ordinal,
                ))
        {
            return Ok(());
        }
        let score = match &display_match {
            Some(display) => display.alignment_score,
            None => name.score_matched(&self.query, self.cancel)?,
        };
        let lane = lane_of(score);
        let capacity = self.capacity(lane);
        if capacity == 0 {
            return Ok(());
        }
        if !self.would_enter(lane, kind, priority, score, history, ordinal) {
            return Ok(());
        }
        // 只有真正进入前 N 时才物化展示名。
        let row = Row {
            source: Arc::clone(source),
            key,
            name: name.to_owned_name(),
            kind,
            priority,
            score,
            lane,
            history,
            application_entry,
            direct_executable,
            display_match,
            ordinal,
        };
        let best = &mut self.best[lane as usize];
        if best.len() == capacity as usize {
            best.pop();
        }
        best.push(HeapRow(row));
        // 早桶增加后，晚桶只保留仍可能展示的条数；不能用晚桶的名额反向裁掉混合桶前十。
        for index in (lane as usize + 1)..self.best.len() {
            let capacity = self.capacity(LauncherLane::from_index(index));
            while self.best[index].len() > capacity as usize {
                self.best[index].pop();
            }
        }
        Ok(())
    }

    fn would_enter(
        &self,
        lane: LauncherLane,
        kind: SearchItemKind,
        priority: u64,
        score: i32,
        history: HistorySignals,
        ordinal: i64,
    ) -> bool {
        let bucket = &self.best[lane as usize];
        let capacity = self.capacity(lane);
        if capacity == 0 {
            return false;
        }
        if bucket.len() < capacity as usize {
            return true;
        }
        best_first_keys(
            lane,
            kind,
            priority,
            score,
            history,
            ordinal,
            &bucket.peek().unwrap().0,
        ) == Ordering::Less
    }

    fn capacity(&self, lane: LauncherLane) -> i32 {
        let mut earlier = 0i32;
        for index in 0..(lane as usize) {
            let count = self.best[index].len() as i32;
            earlier +=
                if index == LauncherLane::Mixed as usize && lane != LauncherLane::Supplemental {
                    count.min(10)
                } else {
                    count
                };
        }
        (self.limit - earlier).max(0)
    }

    pub fn finish(&self) -> Result<SearchResponse, Canceled> {
        self.cancel.check()?;
        let mut lanes: Vec<Vec<Row>> = self
            .best
            .iter()
            .map(|bucket| {
                let mut rows: Vec<Row> = bucket.iter().map(|item| item.0.clone()).collect();
                rows.sort_by(|left, right| best_first(left, right));
                rows
            })
            .collect();
        let merged = merge_lanes(&lanes);
        let take = self.limit.max(0) as usize;
        let items = merged
            .into_iter()
            .take(take)
            .map(|row| {
                let mut candidate =
                    row.source
                        .materialize(row.key, &row.name, row.kind, row.priority);
                candidate.is_application_entry = row.application_entry;
                candidate.is_direct_executable = row.direct_executable;
                let match_result = match row.display_match {
                    Some(display) => display,
                    None => self.query.match_candidate_with(&row.name, self.cancel)?,
                };
                Ok(SearchHit {
                    candidate,
                    match_result,
                    group: group(row.history, row.application_entry, row.direct_executable),
                    history: row.history,
                })
            })
            .collect::<Result<Vec<_>, _>>()?;
        let _ = &mut lanes;
        Ok(SearchResponse {
            query: self.query.text().to_string(),
            total_matches: self.total,
            items,
        })
    }
}

pub struct SearchEngine {
    history: SelectionHistory,
}

impl SearchEngine {
    pub fn new(history: SelectionHistory) -> Self {
        Self { history }
    }

    pub fn history(&self) -> &SelectionHistory {
        &self.history
    }

    pub fn history_mut(&mut self) -> &mut SelectionHistory {
        &mut self.history
    }

    pub fn search(
        &self,
        query: &str,
        candidates: &[SearchCandidate],
        limit: i32,
        cancel: CancelToken,
    ) -> Result<SearchResponse, Canceled> {
        assert!(limit >= 0, "limit 不能为负");
        let prepared = FuzzyMatcher.prepare(query);
        let history_view = self.history.for_query(query);
        #[derive(Clone)]
        struct Ranked {
            candidate: SearchCandidate,
            score: i32,
            lane: LauncherLane,
            history: HistorySignals,
            ordinal: i64,
        }
        let mut matches: Vec<(String, Ranked)> = Vec::new();
        let mut ordinal = 0i64;
        for candidate in candidates {
            cancel.check()?;
            assert!(!candidate.id.trim().is_empty(), "candidate.Id 不能为空");
            let score = prepared.score_with(&candidate.name, cancel)?;
            if score == 0 {
                continue;
            }
            let signals = history_view.get(&candidate.id);
            let lane = lane(
                candidate.kind,
                candidate.is_application_entry,
                score,
                signals,
                candidate.is_direct_executable,
            );
            let hit = Ranked {
                candidate: candidate.clone(),
                score,
                lane,
                history: signals,
                ordinal,
            };
            ordinal += 1;
            if let Some((_, old)) = matches.iter_mut().find(|(id, _)| id == &candidate.id) {
                if hit.score > old.score {
                    let keep = old.ordinal;
                    *old = hit;
                    old.ordinal = keep;
                }
            } else {
                matches.push((candidate.id.clone(), hit));
            }
        }
        let mut lanes: Vec<Vec<Ranked>> = (0..7).map(|_| Vec::new()).collect();
        for (_, hit) in &matches {
            lanes[hit.lane as usize].push(hit.clone());
        }
        for bucket in &mut lanes {
            bucket.sort_by(|left, right| {
                let compare = left.lane.cmp(&right.lane);
                if compare != Ordering::Equal {
                    return compare;
                }
                let compare = compare_lane(
                    left.lane,
                    left.candidate.kind,
                    left.candidate.normal_priority,
                    left.score,
                    left.history,
                    right.candidate.kind,
                    right.candidate.normal_priority,
                    right.score,
                    right.history,
                );
                if compare != Ordering::Equal {
                    compare
                } else {
                    left.ordinal.cmp(&right.ordinal)
                }
            });
        }
        let items = merge_lanes(&lanes)
            .into_iter()
            .take(limit.max(0) as usize)
            .map(|hit| {
                Ok(SearchHit {
                    match_result: prepared.match_candidate_with(&hit.candidate.name, cancel)?,
                    group: group(
                        hit.history,
                        hit.candidate.is_application_entry,
                        hit.candidate.is_direct_executable,
                    ),
                    history: hit.history,
                    candidate: hit.candidate,
                })
            })
            .collect::<Result<Vec<_>, _>>()?;
        Ok(SearchResponse {
            query: query.to_string(),
            total_matches: matches.len() as i32,
            items,
        })
    }
}

pub fn history_for<'a>(history: &'a SelectionHistory, query: &str) -> HistoryView<'a> {
    history.for_query(query)
}
