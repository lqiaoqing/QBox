//! 忠实移植 `src/QBox.Core/Matching/FuzzyMatcher.cs` 的对齐分值与 CanMatch 行为。
//! 拼音与格式查询不在本模块；685 组 ASCII 对照样本只覆盖内部对齐分值。

use crate::cancel::{CancelToken, Canceled};

/// UTF-16 高亮区间，起点与长度与 C# `TextRange` 一致。
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub struct TextRange {
    pub start: i32,
    pub length: i32,
}

/// 与 C# `MatchResult` 对齐：`alignment_score` 是内部对齐分值，不是最终排序分。
#[derive(Clone, Debug, PartialEq, Eq)]
pub struct MatchResult {
    pub is_match: bool,
    pub alignment_score: i32,
    pub highlights: Vec<TextRange>,
}

impl MatchResult {
    pub fn none() -> Self {
        Self {
            is_match: false,
            alignment_score: 0,
            highlights: Vec::new(),
        }
    }
}

#[derive(Debug, Default)]
pub struct FuzzyMatcher;

impl FuzzyMatcher {
    pub fn prepare<'a>(&self, query: &'a str) -> PreparedQuery<'a> {
        PreparedQuery::new(query)
    }

    pub fn match_text(&self, query: &str, candidate: &str) -> MatchResult {
        self.prepare(query).match_candidate(candidate)
    }
}

pub struct PreparedQuery<'a> {
    text: &'a str,
    query: Vec<char>,
    /// 查询为 ASCII 时保存不变区域大写形式，供单字符快路径与评分快路径使用。
    ascii_upper: Option<String>,
}

impl<'a> PreparedQuery<'a> {
    pub fn new(query: &'a str) -> Self {
        let ascii_upper = if query.is_ascii() {
            Some(query.to_ascii_uppercase())
        } else {
            None
        };
        Self {
            text: query,
            query: query.chars().collect(),
            ascii_upper,
        }
    }

    pub fn text(&self) -> &str {
        self.text
    }

    pub fn score_upper_bound(&self, candidate: &str) -> i32 {
        // 每个标量至多占两个 UTF-16 单元；对齐只有扣分，因此该上界不会误删可进入前列的结果。
        let minimum_runes = if candidate.is_ascii() {
            candidate.len() as i32
        } else {
            (candidate.len() as i32 + 1) / 2
        };
        1.max(checked_i32(
            self.query.len() as i32 * 1000 - minimum_runes * 10,
        ))
    }

    pub fn can_match(&self, candidate: &str) -> bool {
        self.can_match_with(candidate, CancelToken::NONE)
            .expect("未取消")
    }

    pub fn can_match_with(&self, candidate: &str, cancel: CancelToken) -> Result<bool, Canceled> {
        cancel.check()?;
        if let (Some(ascii_upper), true) = (self.ascii_upper.as_deref(), candidate.is_ascii()) {
            let needles = ascii_upper.as_bytes();
            let haystack = candidate.as_bytes();
            // 单字符 ASCII/数字查询走等价的 IndexOf，避免短查询在全量预筛里反复切切片。
            if needles.len() == 1 {
                let letter = needles[0];
                return Ok(if letter.is_ascii_uppercase() {
                    find_any(haystack, letter, letter + 32).is_some()
                } else {
                    haystack.iter().any(|&b| b == letter)
                });
            }
            let mut remaining = haystack;
            for &letter in needles {
                let lower = if letter.is_ascii_uppercase() {
                    letter + 32
                } else {
                    letter
                };
                match find_any(remaining, letter, lower) {
                    Some(at) => remaining = &remaining[at + 1..],
                    None => return Ok(false),
                }
            }
            return Ok(true);
        }
        let mut next = 0;
        for rune in candidate.chars() {
            if next == self.query.len() {
                return Ok(true);
            }
            if equivalent(self.query[next], rune) {
                next += 1;
            }
        }
        Ok(next == self.query.len())
    }

    pub fn score(&self, candidate: &str) -> i32 {
        self.score_with(candidate, CancelToken::NONE)
            .expect("未取消")
    }

    pub fn score_with(&self, candidate: &str, cancel: CancelToken) -> Result<i32, Canceled> {
        Ok(if !self.can_match_with(candidate, cancel)? {
            0
        } else {
            self.score_matched_with(candidate, cancel)?
        })
    }

    pub fn score_matched(&self, candidate: &str) -> i32 {
        self.score_matched_with(candidate, CancelToken::NONE)
            .expect("未取消")
    }

    pub fn score_matched_with(
        &self,
        candidate: &str,
        cancel: CancelToken,
    ) -> Result<i32, Canceled> {
        cancel.check()?;
        if self.query.is_empty() {
            return Ok(1);
        }
        if self.ascii_upper.is_none() || !candidate.is_ascii() {
            return Ok(self
                .match_candidate_with(candidate, cancel)?
                .alignment_score);
        }
        let query = self.ascii_upper.as_ref().unwrap().as_bytes();
        let cand = candidate.as_bytes();
        let width = query.len() + 1;
        // 排序阶段只计算分值，避免为所有命中分配高亮链；Unicode 继续走已验证的完整实现。
        let mut best = vec![0i32; width];
        let mut snapshot = vec![0i32; width];
        let mut previous_end = vec![i32::MIN; width];
        let mut current_end = vec![i32::MIN; width];
        let base_score = checked_i32(query.len() as i32 * 1000 - cand.len() as i32 * 10);
        best[0] = base_score;
        let mut acronym_score = base_score - 100;
        let mut acronym_prefix = 0;
        let mut at = 0;
        while at < cand.len() && acronym_prefix < query.len() {
            if query[acronym_prefix] == upper_ascii(cand[at]) {
                acronym_prefix += 1;
            } else {
                acronym_score -= 50;
            }
            at = next_ascii_boundary(cand, at);
        }
        if acronym_prefix != query.len() {
            acronym_score = 0;
        }
        let mut start = 0;
        while start < cand.len() {
            snapshot.copy_from_slice(&best);
            current_end.fill(i32::MIN);
            if ascii_separator(cand[start]) {
                for prefix in 0..query.len() {
                    if query[prefix] != cand[start] {
                        continue;
                    }
                    best[prefix + 1] = best[prefix + 1].max(snapshot[prefix]);
                    current_end[prefix + 1] = current_end[prefix + 1].max(previous_end[prefix]);
                }
                start += 1;
            } else {
                let end = next_ascii_boundary(cand, start);
                for prefix in (0..query.len()).rev() {
                    if snapshot[prefix] == 0 || query[prefix] != upper_ascii(cand[start]) {
                        continue;
                    }
                    let mut score = snapshot[prefix] - 100;
                    if prefix != 0 {
                        score = score.max(previous_end[prefix]);
                    }
                    score -= 2 * start as i32;
                    let mut next = prefix + 1;
                    best[next] = best[next].max(score);
                    if start + 1 == end {
                        current_end[next] = current_end[next].max(score);
                    }
                    let mut skipped = false;
                    let mut position = start + 1;
                    while position < end && next < query.len() {
                        if query[next] != upper_ascii(cand[position]) {
                            skipped = true;
                            position += 1;
                            continue;
                        }
                        score -= 2 * position as i32 + if skipped { 400 } else { 0 };
                        next += 1;
                        best[next] = best[next].max(score);
                        if position + 1 == end {
                            current_end[next] = current_end[next].max(score);
                        }
                        position += 1;
                    }
                }
                start = end;
            }
            std::mem::swap(&mut previous_end, &mut current_end);
        }
        Ok(1.max(best[width - 1].max(acronym_score)))
    }

    pub fn match_candidate(&self, candidate: &str) -> MatchResult {
        self.match_candidate_with(candidate, CancelToken::NONE)
            .expect("未取消")
    }

    pub fn match_candidate_with(
        &self,
        candidate: &str,
        cancel: CancelToken,
    ) -> Result<MatchResult, Canceled> {
        if !self.can_match_with(candidate, cancel)? {
            return Ok(MatchResult::none());
        }
        if self.query.is_empty() {
            return Ok(MatchResult {
                is_match: true,
                alignment_score: 1,
                highlights: Vec::new(),
            });
        }

        let tokens = tokenize(candidate);
        let base_score = checked_i32(self.query.len() as i32 * 1000 - tokens.len() as i32 * 10);
        let mut acronym_score = base_score - 100;
        let mut acronym_prefix = 0;
        let mut acronym_positions: Option<Box<Position>> = None;
        let mut start = 0;
        while start < tokens.len() && acronym_prefix < self.query.len() {
            if equivalent(self.query[acronym_prefix], tokens[start].value) {
                acronym_positions = Some(Box::new(Position {
                    index: start,
                    previous: acronym_positions,
                }));
                acronym_prefix += 1;
            } else {
                acronym_score -= 50;
            }
            start = next_boundary(&tokens, start);
        }
        if acronym_prefix != self.query.len() {
            acronym_score = 0;
        }

        // 全局前缀最优值与“恰好在上一段末尾结束”的状态分别保存，跨段的起始代价不同。
        let width = self.query.len() + 1;
        let mut best = vec![State::default(); width];
        let mut snapshot = vec![State::default(); width];
        let mut previous_end = vec![State::invalid(); width];
        let mut current_end = vec![State::invalid(); width];
        best[0] = State {
            score: base_score,
            positions: None,
        };

        let mut start = 0;
        while start < tokens.len() {
            snapshot.clone_from_slice(&best);
            current_end.fill(State::invalid());
            if is_separator(tokens[start].value) {
                // 标点和空白按字面匹配；它们本身不收取词段起始及位置代价。
                for prefix in 0..self.query.len() {
                    if self.query[prefix] != tokens[start].value {
                        continue;
                    }
                    update(&mut best, prefix + 1, snapshot[prefix].add_position(start));
                    if previous_end[prefix].score != i32::MIN {
                        update(
                            &mut current_end,
                            prefix + 1,
                            previous_end[prefix].add_position(start),
                        );
                    }
                }
                start += 1;
            } else {
                let end = next_boundary(&tokens, start);
                for prefix in (0..self.query.len()).rev() {
                    if snapshot[prefix].score == 0
                        || !equivalent(self.query[prefix], tokens[start].value)
                    {
                        continue;
                    }
                    let mut state = snapshot[prefix].with_score(snapshot[prefix].score - 100);
                    if prefix != 0 && previous_end[prefix].score > state.score {
                        state = previous_end[prefix].clone();
                    }
                    state = state.with_score(state.score - 2 * tokens[start].utf8_offset as i32);
                    state = state.add_position(start);
                    let mut next = prefix + 1;
                    update(&mut best, next, state.clone());
                    if start + 1 == end {
                        update(&mut current_end, next, state.clone());
                    }
                    let mut skipped = false;
                    let mut position = start + 1;
                    while position < end && next < self.query.len() {
                        if !equivalent(self.query[next], tokens[position].value) {
                            skipped = true;
                            position += 1;
                            continue;
                        }
                        // 同一词段发生跳字后，后续匹配字符继续承担跳字代价。
                        state = state.with_score(
                            state.score
                                - 2 * tokens[position].utf8_offset as i32
                                - if skipped { 400 } else { 0 },
                        );
                        state = state.add_position(position);
                        next += 1;
                        update(&mut best, next, state.clone());
                        if position + 1 == end {
                            update(&mut current_end, next, state.clone());
                        }
                        position += 1;
                    }
                }
                start = end;
            }
            std::mem::swap(&mut previous_end, &mut current_end);
        }

        let mut result = best[width - 1].clone();
        if acronym_score >= result.score {
            result = State {
                score: acronym_score,
                positions: acronym_positions,
            };
        }
        if result.score < 1 {
            // 有序筛选已经确认命中；低分对齐仍返回结果，并用完整有序匹配产生高亮。
            let mut next = 0;
            let mut positions = None;
            let mut i = 0;
            while i < tokens.len() && next < self.query.len() {
                if equivalent(self.query[next], tokens[i].value) {
                    positions = Some(Box::new(Position {
                        index: i,
                        previous: positions,
                    }));
                    next += 1;
                }
                i += 1;
            }
            result = State {
                score: 1,
                positions,
            };
        }
        Ok(MatchResult {
            is_match: true,
            alignment_score: result.score,
            highlights: ranges(&tokens, result.positions.as_deref()),
        })
    }
}

fn checked_i32(value: i32) -> i32 {
    value
}

fn find_any(haystack: &[u8], a: u8, b: u8) -> Option<usize> {
    haystack.iter().position(|&ch| ch == a || ch == b)
}

fn upper_ascii(value: u8) -> u8 {
    if value.is_ascii_lowercase() {
        value - 32
    } else {
        value
    }
}

fn ascii_separator(value: u8) -> bool {
    value.is_ascii_whitespace()
        || matches!(value, b'!'..=b'/' | b':'..=b'@' | b'['..=b'`' | b'{'..=b'~')
}

fn next_ascii_boundary(value: &[u8], start: usize) -> usize {
    for i in start + 1..value.len() {
        let current = value[i];
        let previous = value[i - 1];
        if ascii_separator(current) {
            return i;
        }
        if current.is_ascii_uppercase()
            && (!previous.is_ascii_uppercase()
                || (i + 1 < value.len() && value[i + 1].is_ascii_lowercase()))
        {
            return i;
        }
        if (current.is_ascii_digit() && previous.is_ascii_alphabetic())
            || (current.is_ascii_alphabetic() && previous.is_ascii_digit())
        {
            return i;
        }
    }
    value.len()
}

fn equivalent(left: char, right: char) -> bool {
    left == right || upper_invariant(left) == upper_invariant(right)
}

fn upper_invariant(ch: char) -> char {
    if ch.is_ascii() {
        return ch.to_ascii_uppercase();
    }
    let mut upper = ch.to_uppercase();
    let first = upper.next().unwrap_or(ch);
    if upper.next().is_none() {
        first
    } else {
        // .NET 的单个 Rune 无法表示多字符大写映射时保持原字符。
        ch
    }
}

fn update(states: &mut [State], index: usize, value: State) {
    if value.score > states[index].score {
        states[index] = value;
    }
}

fn is_separator(ch: char) -> bool {
    ch.is_whitespace() || matches!(ch as u32, 33..=47 | 58..=64 | 91..=96 | 123..=126)
}

fn is_cjk(ch: char) -> bool {
    matches!(ch as u32, 0x3400..=0x9FFF | 0xF900..=0xFAFF | 0x20000..=0x3134F)
}

fn next_boundary(tokens: &[Token], start: usize) -> usize {
    if is_cjk(tokens[start].value) {
        return start + 1;
    }
    for i in start + 1..tokens.len() {
        let current = tokens[i].value;
        let previous = tokens[i - 1].value;
        if is_separator(current) || is_cjk(current) {
            return i;
        }
        if is_upper(current)
            && (!is_upper(previous) || (i + 1 < tokens.len() && is_lower(tokens[i + 1].value)))
        {
            return i;
        }
        if (is_decimal_digit(current) && is_letter(previous))
            || (is_letter(current) && is_decimal_digit(previous))
        {
            return i;
        }
    }
    tokens.len()
}

fn is_upper(ch: char) -> bool {
    ch.is_uppercase()
}

fn is_lower(ch: char) -> bool {
    ch.is_lowercase()
}

fn is_decimal_digit(ch: char) -> bool {
    ch.is_ascii_digit() || unicode_general_category_nd(ch)
}

fn is_letter(ch: char) -> bool {
    ch.is_alphabetic()
}

fn unicode_general_category_nd(ch: char) -> bool {
    // 685 组样本全是 ASCII；这里仅覆盖常见 Nd 区块，完整 Unicode 分类留待后续补齐。
    matches!(
        ch as u32,
        0x0030..=0x0039
            | 0x0660..=0x0669
            | 0x06F0..=0x06F9
            | 0x07C0..=0x07C9
            | 0x0966..=0x096F
            | 0x09E6..=0x09EF
            | 0x0A66..=0x0A6F
            | 0x0AE6..=0x0AEF
            | 0x0B66..=0x0B6F
            | 0x0BE6..=0x0BEF
            | 0x0C66..=0x0C6F
            | 0x0CE6..=0x0CEF
            | 0x0D66..=0x0D6F
            | 0x0E50..=0x0E59
            | 0x0ED0..=0x0ED9
            | 0x0F20..=0x0F29
            | 0x1040..=0x1049
            | 0x17E0..=0x17E9
            | 0x1810..=0x1819
            | 0xFF10..=0xFF19
    )
}

fn tokenize(text: &str) -> Vec<Token> {
    let mut tokens = Vec::with_capacity(text.len());
    let mut utf16 = 0usize;
    let mut utf8 = 0usize;
    for value in text.chars() {
        tokens.push(Token {
            value,
            utf16_offset: utf16,
            utf8_offset: utf8,
        });
        utf16 += value.len_utf16();
        utf8 += value.len_utf8();
    }
    tokens
}

fn ranges(tokens: &[Token], mut position: Option<&Position>) -> Vec<TextRange> {
    let mut offsets = Vec::new();
    while let Some(current) = position {
        offsets.push(current.index);
        position = current.previous.as_deref();
    }
    offsets.reverse();
    let mut highlights: Vec<TextRange> = Vec::new();
    for index in offsets {
        let token = &tokens[index];
        let length = token.value.len_utf16() as i32;
        let start = token.utf16_offset as i32;
        if let Some(last) = highlights.last_mut() {
            if last.start + last.length == start {
                last.length += length;
                continue;
            }
        }
        highlights.push(TextRange { start, length });
    }
    highlights
}

struct Token {
    value: char,
    utf16_offset: usize,
    utf8_offset: usize,
}

#[derive(Clone)]
struct Position {
    index: usize,
    previous: Option<Box<Position>>,
}

#[derive(Clone, Default)]
struct State {
    score: i32,
    positions: Option<Box<Position>>,
}

impl State {
    fn invalid() -> Self {
        Self {
            score: i32::MIN,
            positions: None,
        }
    }

    fn with_score(&self, score: i32) -> Self {
        Self {
            score,
            positions: self.positions.clone(),
        }
    }

    fn add_position(&self, index: usize) -> Self {
        Self {
            score: self.score,
            positions: Some(Box::new(Position {
                index,
                previous: self.positions.clone(),
            })),
        }
    }
}
