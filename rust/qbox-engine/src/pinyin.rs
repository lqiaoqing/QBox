//! 忠实移植 `PreparedApplicationName`：字面分优先，完整音节拼音次之，扩展缩写最后。

use std::collections::{HashMap, HashSet};
use std::fs;
use std::path::PathBuf;
use std::sync::OnceLock;

use crate::cancel::{CancelToken, Canceled};
use crate::fuzzy::{MatchResult, PreparedQuery, TextRange};

#[derive(Clone, Debug, PartialEq, Eq)]
pub struct ApplicationNameMatch {
    pub score: i32,
    pub phonetic_match: Option<MatchResult>,
    pub supplemental: bool,
}

impl ApplicationNameMatch {
    pub fn none() -> Self {
        Self {
            score: 0,
            phonetic_match: None,
            supplemental: false,
        }
    }

    pub fn is_phonetic(&self) -> bool {
        self.phonetic_match.is_some()
    }
}

#[derive(Clone)]
struct Token {
    start: i32,
    length: i32,
    readings: Vec<String>,
    is_han: bool,
}

#[derive(Clone)]
pub struct PreparedApplicationName {
    text: String,
    tokens: Vec<Token>,
    phonetic_letters: u64,
}

fn readings_table() -> &'static HashMap<u32, Vec<String>> {
    static TABLE: OnceLock<HashMap<u32, Vec<String>>> = OnceLock::new();
    TABLE.get_or_init(load_readings)
}

fn default_pinyin_path() -> PathBuf {
    PathBuf::from(env!("CARGO_MANIFEST_DIR"))
        .join("..")
        .join("..")
        .join("src")
        .join("QBox.Core")
        .join("Matching")
        .join("Data")
        .join("pinyin.tsv")
}

fn load_readings() -> HashMap<u32, Vec<String>> {
    let path = default_pinyin_path();
    let text = fs::read_to_string(&path)
        .unwrap_or_else(|error| panic!("缺少拼音读音表 {}: {error}", path.display()));
    let mut result = HashMap::new();
    for line in text.lines() {
        if line.is_empty() || line.as_bytes()[0] == b'#' {
            continue;
        }
        let mut fields = line.split('\t');
        let (Some(code), Some(readings), None) = (fields.next(), fields.next(), fields.next())
        else {
            panic!("拼音读音表格式无效");
        };
        let rune = u32::from_str_radix(code, 16).unwrap_or_else(|_| panic!("拼音读音表格式无效"));
        if char::from_u32(rune).is_none() {
            panic!("拼音读音表格式无效");
        }
        if result
            .insert(rune, readings.split(',').map(str::to_string).collect())
            .is_some()
        {
            panic!("拼音读音表格式无效");
        }
    }
    result
}

fn lower_invariant(ch: char) -> String {
    if ch.is_ascii() {
        ch.to_ascii_lowercase().to_string()
    } else {
        let lower: String = ch.to_lowercase().collect();
        if lower.chars().count() == 1 {
            lower
        } else {
            ch.to_string()
        }
    }
}

impl PreparedApplicationName {
    pub fn new(text: impl Into<String>) -> Self {
        let text = text.into();
        let has_non_ascii = text.chars().any(|ch| ch as u32 > 127);
        if !has_non_ascii {
            return Self {
                text,
                tokens: Vec::new(),
                phonetic_letters: 0,
            };
        }
        let table = readings_table();
        let mut tokens = Vec::new();
        let mut offset = 0i32;
        let mut has_han = false;
        for ch in text.chars() {
            let length = ch.len_utf16() as i32;
            let han = table.get(&(ch as u32)).cloned();
            let is_han = han.is_some();
            has_han |= is_han;
            tokens.push(Token {
                start: offset,
                length,
                readings: han.unwrap_or_else(|| vec![lower_invariant(ch)]),
                is_han,
            });
            offset += length;
        }
        if !has_han {
            tokens.clear();
        }
        let mut letters = 0u64;
        for token in &tokens {
            for reading in &token.readings {
                for letter in reading.chars() {
                    if ('a'..='z').contains(&letter) {
                        letters |= 1u64 << (letter as u8 - b'a');
                    } else if ('0'..='9').contains(&letter) {
                        letters |= 1u64 << (26 + letter as u8 - b'0');
                    }
                }
            }
        }
        Self {
            text,
            tokens,
            phonetic_letters: letters,
        }
    }

    pub fn text(&self) -> &str {
        &self.text
    }

    pub fn has_phonetic(&self) -> bool {
        !self.tokens.is_empty()
    }

    pub fn phonetic_letters(&self) -> u64 {
        self.phonetic_letters
    }

    pub fn can_contain_phonetic(text: &str) -> bool {
        let table = readings_table();
        text.chars()
            .any(|ch| ch as u32 > 127 && table.contains_key(&(ch as u32)))
    }

    pub fn match_query(
        &self,
        query: &PreparedQuery<'_>,
        cancel: CancelToken,
        extended: bool,
    ) -> Result<ApplicationNameMatch, Canceled> {
        let literal = query.score_with(&self.text, cancel)?;
        if literal > 0 {
            return Ok(ApplicationNameMatch {
                score: literal,
                phonetic_match: None,
                supplemental: false,
            });
        }
        if self.tokens.is_empty()
            || query.text().is_empty()
            || query.text().len() > 128
            || !query.text().chars().all(|ch| ch.is_ascii_alphanumeric())
        {
            return Ok(ApplicationNameMatch::none());
        }
        let complete = self.match_phonetic(query.text(), false, cancel)?;
        if complete.score != 0 || !extended {
            Ok(complete)
        } else {
            self.match_phonetic(query.text(), true, cancel)
        }
    }

    pub fn match_phonetic(
        &self,
        query: &str,
        extended: bool,
        cancel: CancelToken,
    ) -> Result<ApplicationNameMatch, Canceled> {
        if self.tokens.is_empty()
            || query.is_empty()
            || query.len() > 128
            || !query.chars().all(|ch| ch.is_ascii_alphanumeric())
        {
            return Ok(ApplicationNameMatch::none());
        }
        let input = query.to_ascii_lowercase();
        let mut rejected = HashSet::new();
        for start in 0..self.tokens.len() {
            if let Some(end) = consume(
                &self.tokens,
                input.as_bytes(),
                start,
                0,
                false,
                extended,
                &mut rejected,
                cancel,
            )? {
                let first = &self.tokens[start];
                let last = &self.tokens[end - 1];
                // 补充候选使用最低应用分值，有效字面应用匹配仍保持在前；普通排序器不变。
                return Ok(ApplicationNameMatch {
                    score: 1,
                    phonetic_match: Some(MatchResult {
                        is_match: true,
                        alignment_score: 1,
                        highlights: vec![TextRange {
                            start: first.start,
                            length: last.start + last.length - first.start,
                        }],
                    }),
                    supplemental: extended,
                });
            }
        }
        Ok(ApplicationNameMatch::none())
    }
}
fn consume(
    tokens: &[Token],
    input: &[u8],
    at: usize,
    next: usize,
    han: bool,
    extended: bool,
    rejected: &mut HashSet<(usize, usize, bool)>,
    cancel: CancelToken,
) -> Result<Option<usize>, Canceled> {
    cancel.check()?;
    if next == input.len() {
        return Ok(if han { Some(at) } else { None });
    }
    if at == tokens.len() || rejected.contains(&(at, next, han)) {
        return Ok(None);
    }
    let token = &tokens[at];
    for reading in &token.readings {
        let reading_bytes = reading.as_bytes();
        if input[next..].starts_with(reading_bytes) {
            if let Some(end) = consume(
                tokens,
                input,
                at + 1,
                next + reading_bytes.len(),
                han || token.is_han,
                extended,
                rejected,
                cancel,
            )? {
                return Ok(Some(end));
            }
        }
        if !extended || !token.is_han {
            continue;
        }
        let remaining = &input[next..];
        // 音节尾部可省略，首字母也可以与完整音节混用；起点仍落在原名称字符边界。
        if remaining.len() < reading_bytes.len() && reading_bytes.starts_with(remaining) {
            return Ok(Some(at + 1));
        }
        if reading_bytes[0] == input[next] {
            if let Some(end) = consume(
                tokens,
                input,
                at + 1,
                next + 1,
                true,
                extended,
                rejected,
                cancel,
            )? {
                return Ok(Some(end));
            }
        }
    }
    rejected.insert((at, next, han));
    Ok(None)
}
