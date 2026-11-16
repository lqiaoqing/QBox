//! 读取 C# 685 组匹配对照样本，不改写、不丢弃任何未完成或失败用例。

use std::fs;
use std::path::{Path, PathBuf};

use serde::Deserialize;

use crate::fuzzy::FuzzyMatcher;

#[derive(Debug, Deserialize)]
struct OracleFile {
    cases: Vec<OracleCase>,
}

#[derive(Debug, Deserialize)]
pub struct OracleCase {
    pub query: String,
    pub candidate: String,
    pub completed: bool,
    pub score: i32,
    #[serde(default)]
    pub partition: Option<String>,
}

#[derive(Debug)]
pub struct CaseFailure {
    pub query: String,
    pub candidate: String,
    pub partition: Option<String>,
    pub expected: i32,
    pub match_score: i32,
    pub match_is_match: bool,
    pub score_only: i32,
    pub reason: String,
}

#[derive(Debug)]
pub struct OracleReport {
    pub total: usize,
    pub executed: usize,
    pub passed: usize,
    pub failures: Vec<CaseFailure>,
}

impl OracleReport {
    pub fn failed(&self) -> usize {
        self.failures.len()
    }
}

pub fn default_fixture_dir() -> PathBuf {
    PathBuf::from(env!("CARGO_MANIFEST_DIR"))
        .join("..")
        .join("..")
        .join("tests")
        .join("QBox.Checks")
        .join("Fixtures")
}

pub fn load_ascii_cases(fixture_dir: &Path) -> Result<Vec<OracleCase>, String> {
    let mut paths: Vec<PathBuf> = fs::read_dir(fixture_dir)
        .map_err(|error| format!("无法读取对照目录 {}: {error}", fixture_dir.display()))?
        .filter_map(|entry| entry.ok().map(|item| item.path()))
        .filter(|path| {
            path.file_name()
                .and_then(|name| name.to_str())
                .map(|name| name.starts_with("ascii-") && name.ends_with(".json"))
                .unwrap_or(false)
        })
        .collect();
    paths.sort();
    if paths.is_empty() {
        return Err(format!(
            "对照目录 {} 中没有 ascii-*.json",
            fixture_dir.display()
        ));
    }
    let mut cases = Vec::new();
    for path in paths {
        let text = fs::read_to_string(&path)
            .map_err(|error| format!("无法读取 {}: {error}", path.display()))?;
        let file: OracleFile = serde_json::from_str(&text)
            .map_err(|error| format!("无法解析 {}: {error}", path.display()))?;
        cases.extend(file.cases);
    }
    Ok(cases)
}

/// 与 `tests/QBox.Checks/Program.cs` 在 `--include-holdout` 时一致：全部 685 组都执行。
pub fn run_ascii_oracle(fixture_dir: &Path, include_holdout: bool) -> Result<OracleReport, String> {
    let cases = load_ascii_cases(fixture_dir)?;
    let matcher = FuzzyMatcher;
    let mut failures = Vec::new();
    let mut executed = 0usize;
    for sample in &cases {
        if !include_holdout && sample.partition.as_deref() == Some("holdout") {
            continue;
        }
        executed += 1;
        if !sample.completed {
            failures.push(CaseFailure {
                query: sample.query.clone(),
                candidate: sample.candidate.clone(),
                partition: sample.partition.clone(),
                expected: sample.score,
                match_score: 0,
                match_is_match: false,
                score_only: 0,
                reason: "Oracle 样本未完成".to_string(),
            });
            continue;
        }
        let actual = matcher.match_text(&sample.query, &sample.candidate);
        let score_only = matcher.prepare(&sample.query).score(&sample.candidate);
        let expected_is_match = sample.score > 0;
        let mut reasons = Vec::new();
        if actual.alignment_score != sample.score || actual.is_match != expected_is_match {
            reasons.push(format!(
                "match expected={}, actual={}, is_match={}",
                sample.score, actual.alignment_score, actual.is_match
            ));
        }
        if score_only != sample.score {
            reasons.push(format!(
                "score-only expected={}, actual={score_only}",
                sample.score
            ));
        }
        if !reasons.is_empty() {
            failures.push(CaseFailure {
                query: sample.query.clone(),
                candidate: sample.candidate.clone(),
                partition: sample.partition.clone(),
                expected: sample.score,
                match_score: actual.alignment_score,
                match_is_match: actual.is_match,
                score_only,
                reason: reasons.join("; "),
            });
        }
    }
    Ok(OracleReport {
        total: cases.len(),
        executed,
        passed: executed - failures.len(),
        failures,
    })
}
