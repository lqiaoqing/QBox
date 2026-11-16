//! 移植打包应用与快捷方式目录的检索提交逻辑，供拼音收集器用例对照。

use std::collections::{HashMap, HashSet};
use std::sync::Arc;

use serde::Deserialize;
use sha2::{Digest, Sha256};

use crate::cancel::{CancelToken, Canceled};
use crate::fuzzy::FuzzyMatcher;
use crate::history::HistoryLookup;
use crate::pinyin::{ApplicationNameMatch, PreparedApplicationName};
use crate::qbi::{ordinal_cmp, ordinal_ignore_case_cmp, starts_with_ordinal_ignore_case};
use crate::search::{CatalogSource, SearchCandidate, SearchCollector, SearchItemKind, SliceSource};

#[derive(Clone, Debug)]
pub struct RegisteredApplication {
    pub name: String,
    pub app_user_model_id: String,
}

#[derive(Clone, Debug, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct CandidateSnapshot {
    pub id: String,
    pub name: String,
    pub target: String,
    pub kind: String,
    pub arguments: Option<String>,
    pub normal_priority: u64,
    pub is_application_entry: bool,
    pub app_user_model_id: Option<String>,
    pub is_direct_executable: bool,
}

impl CandidateSnapshot {
    fn into_candidate(self) -> SearchCandidate {
        let kind = match self.kind.as_str() {
            "Directory" => SearchItemKind::Directory,
            "Application" => SearchItemKind::Application,
            "Shortcut" => SearchItemKind::Shortcut,
            _ => SearchItemKind::File,
        };
        SearchCandidate {
            id: self.id,
            name: self.name,
            target: self.target,
            kind,
            arguments: self.arguments,
            normal_priority: self.normal_priority,
            is_application_entry: self.is_application_entry,
            app_user_model_id: self.app_user_model_id,
            is_direct_executable: self.is_direct_executable,
        }
    }
}

#[derive(Clone, Debug, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct ShortcutSnapshot {
    pub canonical: CandidateSnapshot,
    pub aliases: Vec<CandidateSnapshot>,
    pub action_target: String,
}

#[derive(Clone, Debug, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct CatalogSnapshot {
    pub applications: Vec<CandidateSnapshot>,
    pub shortcuts: Vec<ShortcutSnapshot>,
}

#[derive(Clone, Debug, PartialEq, Eq, Hash)]
pub struct ShortcutAction {
    pub target: String,
    pub arguments: String,
    pub working_directory: String,
    pub show_command: i32,
    pub flags: u32,
}

pub fn is_packaged_id(id: &str) -> bool {
    if id.is_empty() || id.len() > 512 {
        return false;
    }
    let bang = id.find('!');
    if bang.is_none() || bang != id.rfind('!') || bang == Some(0) || id.ends_with('!') {
        return false;
    }
    id.chars()
        .all(|letter| letter.is_ascii_alphanumeric() || matches!(letter, '.' | '-' | '_' | '!'))
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

pub fn packaged_identity(id: &str) -> String {
    assert!(is_packaged_id(id), "打包应用身份无效");
    format!("app:{}", hex_lower(&Sha256::digest(id.as_bytes())))
}

pub fn packaged_parsing_name(id: &str) -> String {
    assert!(is_packaged_id(id), "打包应用身份无效");
    format!("shell:AppsFolder\\{id}")
}

pub struct PackagedApplicationCatalog {
    entries: Vec<SearchCandidate>,
    names: HashMap<String, PreparedApplicationName>,
}

impl PackagedApplicationCatalog {
    pub fn create(applications: Vec<RegisteredApplication>) -> Self {
        let mut entries: Vec<SearchCandidate> = applications
            .into_iter()
            .filter(|application| {
                !application.name.trim().is_empty()
                    && is_packaged_id(&application.app_user_model_id)
            })
            .map(|application| {
                let mut candidate = SearchCandidate::application(
                    packaged_identity(&application.app_user_model_id),
                    application.name,
                    packaged_parsing_name(&application.app_user_model_id),
                );
                candidate.app_user_model_id = Some(application.app_user_model_id);
                candidate
            })
            .collect();
        let mut seen = HashSet::new();
        entries.retain(|candidate| seen.insert((candidate.id.clone(), candidate.name.clone())));
        entries.sort_by(|left, right| {
            ordinal_cmp(&left.id, &right.id).then_with(|| ordinal_cmp(&left.name, &right.name))
        });
        let names = entries
            .iter()
            .map(|entry| entry.name.clone())
            .collect::<HashSet<_>>()
            .into_iter()
            .map(|name| (name.clone(), PreparedApplicationName::new(name)))
            .collect();
        Self { entries, names }
    }

    // 产品侧沿用 C# 的 PackagedApplicationCatalog 加载，再把最终条目快照交给 Rust 搜索。
    pub fn from_snapshot(applications: Vec<CandidateSnapshot>) -> Self {
        let entries: Vec<SearchCandidate> = applications
            .into_iter()
            .map(CandidateSnapshot::into_candidate)
            .collect();
        let names = entries
            .iter()
            .map(|entry| entry.name.clone())
            .collect::<HashSet<_>>()
            .into_iter()
            .map(|name| (name.clone(), PreparedApplicationName::new(name)))
            .collect();
        Self { entries, names }
    }

    pub fn search_into(
        &self,
        collector: &mut SearchCollector<'_>,
        history: &dyn HistoryLookup,
        cancel: CancelToken,
    ) -> Result<(), Canceled> {
        // 同一 AUMID 可以有本地化名称别名；先择最佳匹配名称，再提交唯一动作。
        let mut matches = Vec::new();
        for candidate in &self.entries {
            let value = self.names[&candidate.name].match_query(collector.query(), cancel, true)?;
            if value.score > 0 {
                matches.push((candidate.clone(), value));
            }
        }
        let mut best: HashMap<String, (SearchCandidate, ApplicationNameMatch)> = HashMap::new();
        for (candidate, value) in matches {
            best.entry(candidate.id.clone())
                .and_modify(|old| {
                    if should_replace_packaged(&old.1, &value) {
                        *old = (candidate.clone(), value.clone());
                    }
                })
                .or_insert((candidate, value));
        }
        let mut matches: Vec<(SearchCandidate, ApplicationNameMatch)> =
            best.into_values().collect();
        matches.sort_by(|left, right| {
            right
                .1
                .score
                .cmp(&left.1.score)
                .then(left.1.is_phonetic().cmp(&right.1.is_phonetic()))
                .then(left.0.id.cmp(&right.0.id))
        });
        let source: Arc<dyn crate::search::UniqueSearchSource> = Arc::new(SliceSource {
            items: matches
                .iter()
                .map(|(candidate, _)| candidate.clone())
                .collect(),
        });
        for (index, (candidate, value)) in matches.iter().enumerate() {
            collector.add_matched(
                &source,
                index as u64,
                &candidate.name,
                SearchItemKind::Application,
                0,
                history.lookup(&candidate.id),
                value.score > 1 || value.is_phonetic(),
                false,
                value.phonetic_match.clone(),
                value.supplemental,
            )?;
        }
        Ok(())
    }
}

fn should_replace_packaged(old: &ApplicationNameMatch, new: &ApplicationNameMatch) -> bool {
    new.score > old.score
        || (new.score == old.score && new.score > 0 && new.supplemental < old.supplemental)
        || (new.score == old.score
            && new.score > 0
            && new.supplemental == old.supplemental
            && !new.is_phonetic()
            && old.is_phonetic())
}

pub struct ShortcutEntry {
    pub canonical: SearchCandidate,
    pub aliases: Vec<SearchCandidate>,
    pub action_target: String,
}

pub struct ShortcutCatalog {
    entries: Vec<ShortcutEntry>,
    names: HashMap<String, PreparedApplicationName>,
}

impl ShortcutCatalog {
    pub fn create(
        shortcuts: Vec<SearchCandidate>,
        roots: &[&str],
        read: impl Fn(&SearchCandidate) -> Option<ShortcutAction>,
    ) -> Self {
        let folders: Vec<String> = roots
            .iter()
            .map(|root| {
                let trimmed = root.trim_end_matches(['\\', '/']);
                format!("{trimmed}\\")
            })
            .collect();
        let mut groups: HashMap<ShortcutAction, Vec<SearchCandidate>> = HashMap::new();
        for candidate in shortcuts {
            if candidate.kind != SearchItemKind::Shortcut {
                continue;
            }
            if !folders
                .iter()
                .any(|root| starts_with_ordinal_ignore_case(&candidate.target, root))
            {
                continue;
            }
            let Some(action) = read(&candidate) else {
                continue;
            };
            if action.target.trim().is_empty() {
                continue;
            }
            let mut alias = candidate;
            if alias.name.to_ascii_lowercase().ends_with(".lnk") {
                alias.name.truncate(alias.name.len() - 4);
            }
            groups.entry(action).or_default().push(alias);
        }
        let mut entries: Vec<ShortcutEntry> = groups
            .into_iter()
            .map(|(action, mut aliases)| {
                aliases.sort_by(|left, right| {
                    folder_index(&folders, &left.target)
                        .cmp(&folder_index(&folders, &right.target))
                        .then_with(|| ordinal_cmp(&left.target, &right.target))
                });
                let mut canonical = aliases[0].clone();
                canonical.kind = SearchItemKind::Application;
                canonical.is_application_entry = true;
                ShortcutEntry {
                    canonical,
                    aliases,
                    action_target: action.target,
                }
            })
            .collect();
        entries.sort_by(|left, right| ordinal_cmp(&left.canonical.id, &right.canonical.id));
        let names = entries
            .iter()
            .flat_map(|entry| entry.aliases.iter().map(|alias| alias.name.clone()))
            .collect::<HashSet<_>>()
            .into_iter()
            .map(|name| (name.clone(), PreparedApplicationName::new(name)))
            .collect();
        Self { entries, names }
    }

    // C# 已完成快捷方式分组与主项选择；Rust 只按快照复现检索、替换与目标集合。
    pub fn from_snapshot(shortcuts: Vec<ShortcutSnapshot>) -> Self {
        let entries: Vec<ShortcutEntry> = shortcuts
            .into_iter()
            .map(|snapshot| ShortcutEntry {
                canonical: snapshot.canonical.into_candidate(),
                aliases: snapshot
                    .aliases
                    .into_iter()
                    .map(CandidateSnapshot::into_candidate)
                    .collect(),
                action_target: snapshot.action_target,
            })
            .collect();
        let names = entries
            .iter()
            .flat_map(|entry| entry.aliases.iter().map(|alias| alias.name.clone()))
            .collect::<HashSet<_>>()
            .into_iter()
            .map(|name| (name.clone(), PreparedApplicationName::new(name)))
            .collect();
        Self { entries, names }
    }

    pub fn search_into(
        &self,
        collector: &mut SearchCollector<'_>,
        history: &dyn HistoryLookup,
        cancel: CancelToken,
    ) -> Result<(), Canceled> {
        if is_literal_link_query(collector.query().text()) {
            return Ok(());
        }
        let mut matches = Vec::new();
        for (index, entry) in self.entries.iter().enumerate() {
            cancel.check()?;
            let mut best: Option<(SearchCandidate, ApplicationNameMatch)> = None;
            let mut signals = crate::history::HistorySignals::default();
            for alias in &entry.aliases {
                let usage = history.lookup(&alias.id);
                signals.exact_query = latest(signals.exact_query, usage.exact_query);
                signals.prefix_query = latest(signals.prefix_query, usage.prefix_query);
                signals.last_used = latest(signals.last_used, usage.last_used);
                let value = self.names[&alias.name].match_query(collector.query(), cancel, true)?;
                if let Some((best_alias, best_match)) = &best {
                    if !better_alias(&value, best_match, &alias.name, &best_alias.name) {
                        continue;
                    }
                } else if value.score <= 0 {
                    continue;
                }
                if value.score > 0 {
                    best = Some((alias.clone(), value));
                }
            }
            if let Some((best, value)) = best {
                matches.push((index, best.name, value, signals));
            }
        }
        matches.sort_by(|left, right| {
            right
                .2
                .score
                .cmp(&left.2.score)
                .then(left.2.is_phonetic().cmp(&right.2.is_phonetic()))
                .then(left.0.cmp(&right.0))
        });
        let source: Arc<dyn crate::search::UniqueSearchSource> = Arc::new(CatalogSource {
            items: self
                .entries
                .iter()
                .map(|entry| entry.canonical.clone())
                .collect(),
        });
        for (index, name, value, signals) in matches {
            collector.add_matched(
                &source,
                index as u64,
                &name,
                SearchItemKind::Application,
                0,
                signals,
                value.score > 1 || value.is_phonetic(),
                false,
                value.phonetic_match,
                value.supplemental,
            )?;
        }
        Ok(())
    }

    // 仅用已命中的动作替换原始入口；只匹配 .lnk 扩展名片段的查询仍能找到原文件。
    pub fn replaced_ids(&self, query: &str) -> Option<HashSet<String>> {
        if is_literal_link_query(query) {
            return None;
        }
        let prepared = FuzzyMatcher.prepare(query);
        let mut ids = HashSet::new();
        for entry in &self.entries {
            let matched = entry.aliases.iter().any(|alias| {
                self.names[&alias.name]
                    .match_query(&prepared, CancelToken::NONE, true)
                    .map(|value| value.score > 0)
                    .unwrap_or(false)
            });
            if matched {
                ids.extend(entry.aliases.iter().map(|alias| alias.id.clone()));
            }
        }
        Some(ids)
    }

    pub fn matched_executable_targets(&self, query: &str) -> HashSet<String> {
        if is_literal_link_query(query) {
            return HashSet::new();
        }
        let prepared = FuzzyMatcher.prepare(query);
        let mut targets = HashSet::new();
        for entry in &self.entries {
            if !entry.action_target.to_ascii_lowercase().ends_with(".exe") {
                continue;
            }
            let strong = entry.aliases.iter().any(|alias| {
                self.names[&alias.name]
                    .match_query(&prepared, CancelToken::NONE, true)
                    .map(|value| value.score > 1 || value.is_phonetic())
                    .unwrap_or(false)
            });
            if strong {
                targets.insert(entry.action_target.clone());
            }
        }
        targets
    }
}

fn better_alias(
    value: &ApplicationNameMatch,
    best: &ApplicationNameMatch,
    alias_name: &str,
    best_name: &str,
) -> bool {
    if value.score > best.score {
        return true;
    }
    if value.score <= 0 || value.score != best.score {
        return false;
    }
    if value.supplemental != best.supplemental {
        return value.supplemental < best.supplemental;
    }
    if value.is_phonetic() != best.is_phonetic() {
        return !value.is_phonetic() && best.is_phonetic();
    }
    ordinal_ignore_case_cmp(alias_name, best_name) == std::cmp::Ordering::Less
}

fn folder_index(folders: &[String], target: &str) -> i32 {
    folders
        .iter()
        .position(|root| starts_with_ordinal_ignore_case(target, root))
        .map(|index| index as i32)
        .unwrap_or(-1)
}

fn is_literal_link_query(query: &str) -> bool {
    query.to_ascii_lowercase().contains(".lnk")
}

fn latest<T: Copy + Ord>(left: Option<T>, right: Option<T>) -> Option<T> {
    match (left, right) {
        (None, right) => right,
        (left, None) => left,
        (Some(left), Some(right)) => Some(if right > left { right } else { left }),
    }
}
