//! 把 C# `PinyinChecks` 与 `LauncherOrderingChecks` 逐条搬到 Rust，不削弱、不跳过。

use std::sync::Arc;

use serde::Deserialize;

use crate::cancel::CancelToken;
use crate::catalog::{
    PackagedApplicationCatalog, RegisteredApplication, ShortcutAction, ShortcutCatalog,
};
use crate::fuzzy::{FuzzyMatcher, TextRange};
use crate::history::{Instant, SelectionHistory};
use crate::oracle::default_fixture_dir;
use crate::pinyin::PreparedApplicationName;
use crate::search::{
    compare_native, SearchCandidate, SearchCollector, SearchEngine, SearchItemKind, SearchResponse,
    SliceSource, TestFileSource,
};

#[derive(Debug)]
pub struct CheckFailure {
    pub name: String,
    pub message: String,
}

#[derive(Debug)]
pub struct CheckReport {
    pub total: usize,
    pub failures: Vec<CheckFailure>,
}

impl CheckReport {
    pub fn passed(&self) -> usize {
        self.total - self.failures.len()
    }
}

struct Suite {
    failures: Vec<CheckFailure>,
    total: usize,
}

impl Suite {
    fn new() -> Self {
        Self {
            failures: Vec::new(),
            total: 0,
        }
    }

    fn run(&mut self, name: impl Into<String>, action: impl FnOnce() -> Result<(), String>) {
        self.total += 1;
        if let Err(message) = action() {
            self.failures.push(CheckFailure {
                name: name.into(),
                message,
            });
        }
    }
}

fn equal<T: PartialEq + std::fmt::Debug>(expected: T, actual: T) -> Result<(), String> {
    if expected == actual {
        Ok(())
    } else {
        Err(format!("expected={expected:?}, actual={actual:?}"))
    }
}

fn is_true(value: bool, message: &str) -> Result<(), String> {
    if value {
        Ok(())
    } else {
        Err(message.to_string())
    }
}

fn single_range(ranges: &[TextRange]) -> Result<TextRange, String> {
    if ranges.len() == 1 {
        Ok(ranges[0])
    } else {
        Err(format!("expected a single highlight, actual={ranges:?}"))
    }
}

pub fn run_pinyin_checks() -> CheckReport {
    let mut checks = Suite::new();
    let fuzzy = FuzzyMatcher;
    for (query, length) in [
        ("db", 2),
        ("douba", 2),
        ("dbaogz", 4),
        ("doubaogz", 4),
        ("DBGZ", 4),
    ] {
        checks.run(
            format!("独立扩展支持首字母和半拼 {query}"),
            || {
                let match_value = PreparedApplicationName::new("豆包工作")
                    .match_query(&fuzzy.prepare(query), CancelToken::NONE, true)
                    .map_err(|error| error.to_string())?;
                is_true(match_value.is_phonetic(), "IsPhonetic")?;
                is_true(match_value.supplemental, "Supplemental")?;
                equal(
                    TextRange { start: 0, length },
                    single_range(&match_value.phonetic_match.as_ref().unwrap().highlights)?,
                )
            },
        );
    }
    checks.run("首字母扩展不接受音节内部起点", || {
        equal(
            0,
            PreparedApplicationName::new("豆包工作")
                .match_query(&fuzzy.prepare("oubao"), CancelToken::NONE, true)
                .map_err(|error| error.to_string())?
                .score,
        )
    });
    checks.run(
        "中文和型号数字混合仍可完整拼音匹配",
        || {
            is_true(
                PreparedApplicationName::new("豆包800Q")
                    .match_query(&fuzzy.prepare("doubao800q"), CancelToken::NONE, true)
                    .map_err(|error| error.to_string())?
                    .is_phonetic(),
                "IsPhonetic",
            )
        },
    );
    for (name, query, start, length) in [
        ("豆包", "doubao", 0, 2),
        ("豆包工作", "DOUBAO", 0, 2),
        ("豆包工作", "doubaogongzuo", 0, 4),
        ("千问办公", "qianwen", 0, 2),
        ("阿里千问办公", "qianwen", 2, 2),
        ("AI豆包", "doubao", 2, 2),
        ("😀豆包", "doubao", 2, 2),
        ("重庆工具", "chongqing", 0, 2),
        ("重庆工具", "zhongqing", 0, 2),
        ("𠀀", "he", 0, 2),
        ("吕布", "lvbu", 0, 2),
    ] {
        checks.run(
            format!("应用全拼及原名称高亮 {query}/{name}"),
            || {
                let match_value = PreparedApplicationName::new(name)
                    .match_query(&fuzzy.prepare(query), CancelToken::NONE, false)
                    .map_err(|error| error.to_string())?;
                is_true(match_value.is_phonetic(), "IsPhonetic")?;
                equal(1, match_value.score)?;
                equal(
                    TextRange { start, length },
                    single_range(&match_value.phonetic_match.as_ref().unwrap().highlights)?,
                )
            },
        );
    }
    for query in [
        "db", "douba", "oubao", "Any", "800Q", ".code", "nd", "Tra", "chatgpt", "rtt",
    ] {
        checks.run(
            format!("完整音节边界不引入额外缩写匹配 {query}"),
            || {
                equal(
                    0,
                    PreparedApplicationName::new("豆包工作")
                        .match_query(&fuzzy.prepare(query), CancelToken::NONE, false)
                        .map_err(|error| error.to_string())?
                        .score,
                )
            },
        );
    }
    checks.run(
        "原有字面匹配分值和高亮入口保持不变",
        || {
            let name = PreparedApplicationName::new("中文 Neat Download Manager");
            let expected = fuzzy.prepare("nd").score(name.text());
            let actual = name
                .match_query(&fuzzy.prepare("nd"), CancelToken::NONE, false)
                .map_err(|error| error.to_string())?;
            equal(expected, actual.score)?;
            is_true(!actual.is_phonetic(), "should stay literal")
        },
    );
    checks.run("拼音匹配响应取消", || {
        let result = PreparedApplicationName::new("豆包工作").match_query(
            &fuzzy.prepare("doubao"),
            CancelToken::CANCELED,
            false,
        );
        match result {
            Err(_) => Ok(()),
            Ok(_) => Err("没有抛出 OperationCanceledException".to_string()),
        }
    });
    checks.run("读音表未覆盖的字符仍保留字面匹配", || {
        let name = PreparedApplicationName::new("𠮷");
        equal(
            0,
            name.match_query(&fuzzy.prepare("ji"), CancelToken::NONE, false)
                .map_err(|error| error.to_string())?
                .score,
        )?;
        is_true(
            name.match_query(&fuzzy.prepare("𠮷"), CancelToken::NONE, false)
                .map_err(|error| error.to_string())?
                .score
                > 0,
            "literal match should remain",
        )
    });

    let catalog = PackagedApplicationCatalog::create(vec![
        registered("豆包", "Example.Tools_1234567890abc!Bao"),
        registered("豆包工作", "Example.Tools_1234567890abc!Work"),
        registered("Doubao Literal", "Example.Tools_1234567890abc!Latin"),
        registered("千问办公", "Example.Tools_1234567890abc!Qianwen"),
    ]);
    let search =
        |query: &str, history: &SelectionHistory, limit: i32| -> Result<SearchResponse, String> {
            let mut collector = SearchCollector::new(query, limit);
            catalog
                .search_into(&mut collector, &history.for_query(query), CancelToken::NONE)
                .map_err(|error| error.to_string())?;
            collector.finish().map_err(|error| error.to_string())
        };

    checks.run(
        "拼音应用保留中文名称且位于既有有效字面应用之后",
        || {
            let result = search("doubao", &SelectionHistory::new(), 9)?;
            equal(3, result.total_matches)?;
            equal(
                "Doubao Literal".to_string(),
                result.items[0].candidate.name.clone(),
            )?;
            is_true(
                result.items[1..]
                    .iter()
                    .all(|hit| hit.candidate.name.starts_with("豆包")),
                "remaining names should start with 豆包",
            )?;
            is_true(
                result.items.iter().all(|hit| hit.match_result.is_match),
                "all should match",
            )?;
            equal(
                TextRange {
                    start: 0,
                    length: 2,
                },
                single_range(&result.items[1].match_result.highlights)?,
            )
        },
    );
    checks.run("拼音选择学习仍使用原应用身份", || {
        let mut history = SelectionHistory::new();
        let selected = search("doubao", &history, 9)?
            .items
            .into_iter()
            .find(|hit| hit.candidate.name == "豆包工作")
            .ok_or_else(|| "missing 豆包工作".to_string())?
            .candidate;
        history.record_selection(&selected.id, "doubao", None);
        let result = search("DOUBAO", &history, 9)?;
        equal(selected.id.clone(), result.items[0].candidate.id.clone())?;
        equal(
            crate::search::RankGroup::ExactQueryHistory,
            result.items[0].group,
        )
    });
    checks.run("拼音扩展展示条数不会截断总命中", || {
        equal(
            3,
            search("doubao", &SelectionHistory::new(), 0)?.total_matches,
        )?;
        equal(
            3,
            search("doubao", &SelectionHistory::new(), 1)?.total_matches,
        )?;
        equal(
            1,
            search("doubao", &SelectionHistory::new(), 1)?.items.len() as i32,
        )
    });
    checks.run(
        "新增缩写不挤掉原来字面首屏，学习后仍可置顶",
        || {
            let added = PackagedApplicationCatalog::create(vec![registered(
                "豆包",
                "Example.Tools_1234567890abc!Bao",
            )]);
            let mut history = SelectionHistory::new();
            let collect = |limit: i32,
                           history: &SelectionHistory|
             -> Result<SearchResponse, String> {
                let mut collector = SearchCollector::new("db", limit);
                let view = history.for_query("db");
                added
                    .search_into(&mut collector, &view, CancelToken::NONE)
                    .map_err(|error| error.to_string())?;
                let source: Arc<dyn crate::search::UniqueSearchSource> = Arc::new(TestFileSource);
                for i in 0..20 {
                    collector
                        .add(
                            source.clone(),
                            i,
                            &format!("d{i}b"),
                            SearchItemKind::File,
                            0,
                            Default::default(),
                            false,
                            false,
                        )
                        .map_err(|error| error.to_string())?;
                }
                collector.finish().map_err(|error| error.to_string())
            };
            equal(21, collect(1, &history)?.total_matches)?;
            is_true(
                collect(9, &history)?
                    .items
                    .iter()
                    .all(|hit| hit.candidate.name != "豆包"),
                "supplemental must not enter first page",
            )?;
            equal(
                "豆包".to_string(),
                collect(21, &history)?
                    .items
                    .last()
                    .unwrap()
                    .candidate
                    .name
                    .clone(),
            )?;
            let selected = collect(21, &history)?
                .items
                .last()
                .unwrap()
                .candidate
                .clone();
            history.record_selection(&selected.id, "db", None);
            let topped = collect(1, &history)?;
            equal(1, topped.items.len() as i32)?;
            equal(selected.id, topped.items[0].candidate.id.clone())
        },
    );
    checks.run(
        "快捷方式拼音匹配仍归并动作并保留中文高亮",
        || {
            let root = std::env::temp_dir().join("QBox-pinyin-shortcut-check");
            let root = std::fs::canonicalize(&root).unwrap_or(root);
            let root_text = root.to_string_lossy().to_string();
            let links = vec![
                SearchCandidate::new(
                    "first",
                    "豆包工作",
                    format!("{}\\first.lnk", root_text.trim_end_matches(['\\', '/'])),
                    SearchItemKind::Shortcut,
                ),
                SearchCandidate::new(
                    "alias",
                    "Doubao Work",
                    format!("{}\\alias.lnk", root_text.trim_end_matches(['\\', '/'])),
                    SearchItemKind::Shortcut,
                ),
            ];
            let shortcuts = ShortcutCatalog::create(links, &[&root_text], |_| {
                Some(ShortcutAction {
                    target: r"C:\Apps\tool.exe".to_string(),
                    arguments: String::new(),
                    working_directory: String::new(),
                    show_command: 1,
                    flags: 0,
                })
            });
            let mut collector = SearchCollector::new("doubaogongzuo", 50);
            shortcuts
                .search_into(
                    &mut collector,
                    &SelectionHistory::new().for_query("doubaogongzuo"),
                    CancelToken::NONE,
                )
                .map_err(|error| error.to_string())?;
            let result = collector.finish().map_err(|error| error.to_string())?;
            equal(1, result.total_matches)?;
            equal(
                "豆包工作".to_string(),
                result.items[0].candidate.name.clone(),
            )?;
            equal(
                TextRange {
                    start: 0,
                    length: 4,
                },
                single_range(&result.items[0].match_result.highlights)?,
            )?;
            is_true(
                shortcuts
                    .matched_executable_targets("doubaogongzuo")
                    .contains(r"C:\Apps\tool.exe"),
                "matched executable target",
            )
        },
    );

    CheckReport {
        total: checks.total,
        failures: checks.failures,
    }
}

fn registered(name: &str, aumid: &str) -> RegisteredApplication {
    RegisteredApplication {
        name: name.to_string(),
        app_user_model_id: aumid.to_string(),
    }
}

#[derive(Deserialize)]
struct OrderingFixture {
    cases: Vec<OrderingCase>,
}

#[derive(Deserialize)]
struct OrderingCase {
    mode: String,
    left: OrderingValue,
    right: OrderingValue,
    completed: bool,
    first: i32,
}

#[derive(Deserialize)]
#[serde(rename_all = "camelCase")]
struct OrderingValue {
    normal_tag: bool,
    normal_key: String,
    has_score: bool,
    score: i32,
    kind: u8,
    last_used: Option<i64>,
}

pub fn run_launcher_ordering_checks() -> Result<CheckReport, String> {
    let mut checks = Suite::new();
    let path = default_fixture_dir().join("launcher-ordering.json");
    let fixture: OrderingFixture = serde_json::from_str(
        &std::fs::read_to_string(&path)
            .map_err(|error| format!("无法读取 {}: {error}", path.display()))?,
    )
    .map_err(|error| format!("无法解析 {}: {error}", path.display()))?;
    for (index, row) in fixture.cases.iter().enumerate() {
        checks.run(format!("Launcher 原始排序指令 {index}"), || {
            is_true(row.completed, "Oracle 样本未完成")?;
            let left = &row.left;
            let right = &row.right;
            let compare = compare_native(
                &row.mode,
                left.normal_tag,
                left.normal_key
                    .parse::<u64>()
                    .map_err(|error| error.to_string())?,
                left.has_score,
                left.score,
                left.kind,
                left.last_used,
                right.normal_tag,
                right
                    .normal_key
                    .parse::<u64>()
                    .map_err(|error| error.to_string())?,
                right.has_score,
                right.score,
                right.kind,
                right.last_used,
            );
            equal(row.first, if compare > 0 { 2 } else { 1 })
        });
    }

    checks.run(
        "强目录、混合前十、弱目录、混合尾部按 Launcher 桶次序合并",
        || {
            let mut candidates: Vec<SearchCandidate> = (0..12)
                .map(|i| {
                    SearchCandidate::new(
                        format!("file{i:02}"),
                        "ab.txt",
                        "unused",
                        SearchItemKind::File,
                    )
                })
                .collect();
            candidates.push(SearchCandidate::new(
                "weak",
                "aXb",
                "unused",
                SearchItemKind::Directory,
            ));
            candidates.push(SearchCandidate::new(
                "strong",
                "ab-dir",
                "unused",
                SearchItemKind::Directory,
            ));
            let result = SearchEngine::new(SelectionHistory::new())
                .search("ab", &candidates, 50, CancelToken::NONE)
                .map_err(|error| error.to_string())?;
            equal("strong".to_string(), result.items[0].candidate.id.clone())?;
            equal("file09".to_string(), result.items[10].candidate.id.clone())?;
            equal("weak".to_string(), result.items[11].candidate.id.clone())?;
            equal("file10".to_string(), result.items[12].candidate.id.clone())
        },
    );
    checks.run(
        "相同查询历史、前缀历史、无历史应用的桶次序",
        || {
            let mut history = SelectionHistory::new();
            let time = Instant::from_unix_seconds(10);
            history.record_selection("exact", "ab", Some(time));
            history.record_selection("prefix", "abcd", Some(Instant(time.0 + 1)));
            let candidates = vec![
                SearchCandidate::new("exact", "aXb", "unused", SearchItemKind::File),
                SearchCandidate::new("prefix", "aXb", "unused", SearchItemKind::File),
                {
                    let mut app =
                        SearchCandidate::new("app", "ab", "unused", SearchItemKind::Application);
                    app.is_application_entry = true;
                    app
                },
                SearchCandidate::new("dir", "ab", "unused", SearchItemKind::Directory),
            ];
            let ids = SearchEngine::new(history)
                .search("ab", &candidates, 50, CancelToken::NONE)
                .map_err(|error| error.to_string())?
                .items
                .into_iter()
                .map(|hit| hit.candidate.id)
                .collect::<Vec<_>>()
                .join(",");
            equal("exact,prefix,app,dir".to_string(), ids)
        },
    );

    let mixed: Vec<SearchCandidate> = (0..30)
        .map(|i| {
            let mut app = SearchCandidate::new(
                format!("app{i}"),
                "ab tool",
                "unused",
                SearchItemKind::Application,
            );
            app.is_application_entry = true;
            app
        })
        .chain((0..90).map(|i| {
            SearchCandidate::new(
                format!("item{i}"),
                if i % 3 == 0 { "ab-dir" } else { "aXb" },
                "unused",
                if i % 2 == 0 {
                    SearchItemKind::Directory
                } else {
                    SearchItemKind::File
                },
            )
        }))
        .collect();
    let mut learned = SelectionHistory::new();
    learned.record_selection("item89", "ab", Some(Instant::from_unix_seconds(1)));
    learned.record_selection("item88", "abcd", Some(Instant::from_unix_seconds(2)));
    for limit in [0, 1, 9, 17, 50, 100] {
        checks.run(
            format!("较早分桶填满后仍保留精确历史、完整计数及扩展结果 / {limit}"),
            || {
                let expected = SearchEngine::new(learned.clone())
                    .search("ab", &mixed, limit, CancelToken::NONE)
                    .map_err(|error| error.to_string())?;
                let mut collector = SearchCollector::new("ab", limit);
                let source = Arc::new(SliceSource {
                    items: mixed.clone(),
                });
                for (i, item) in mixed.iter().enumerate() {
                    collector
                        .add(
                            source.clone(),
                            i as u64,
                            &item.name,
                            item.kind,
                            0,
                            learned.for_query("ab").get(&item.id),
                            item.is_application_entry,
                            false,
                        )
                        .map_err(|error| error.to_string())?;
                }
                let actual = collector.finish().map_err(|error| error.to_string())?;
                equal(expected.total_matches, actual.total_matches)?;
                let expected_ids: Vec<_> =
                    expected.items.iter().map(|hit| &hit.candidate.id).collect();
                let actual_ids: Vec<_> = actual.items.iter().map(|hit| &hit.candidate.id).collect();
                // C# 比较整个 SearchCandidate；校验身份、名称、类型与入口标记。
                is_true(
                    expected
                        .items
                        .iter()
                        .zip(actual.items.iter())
                        .all(|(left, right)| {
                            left.candidate.id == right.candidate.id
                                && left.candidate.name == right.candidate.name
                                && left.candidate.kind == right.candidate.kind
                                && left.candidate.is_application_entry
                                    == right.candidate.is_application_entry
                        }),
                    &format!("candidates differ: expected={expected_ids:?} actual={actual_ids:?}"),
                )
            },
        );
    }
    checks.run(
        "跨应用来源的晚到强匹配可以替换已满的早到结果",
        || {
            let mut entries: Vec<SearchCandidate> = (0..20)
                .map(|index| {
                    let mut app = SearchCandidate::new(
                        format!("early{index}"),
                        format!("Alpha Beta long tool {index}"),
                        "unused",
                        SearchItemKind::Application,
                    );
                    app.is_application_entry = true;
                    app
                })
                .collect();
            let mut late =
                SearchCandidate::new("late", "ab", "unused", SearchItemKind::Application);
            late.is_application_entry = true;
            entries.push(late);
            let mut collector = SearchCollector::new("ab", 1);
            let source = Arc::new(SliceSource {
                items: entries.clone(),
            });
            for (index, entry) in entries.iter().enumerate() {
                collector
                    .add(
                        source.clone(),
                        index as u64,
                        &entry.name,
                        SearchItemKind::Application,
                        0,
                        Default::default(),
                        true,
                        false,
                    )
                    .map_err(|error| error.to_string())?;
            }
            let result = collector.finish().map_err(|error| error.to_string())?;
            equal(21, result.total_matches)?;
            equal("late".to_string(), result.items[0].candidate.id.clone())?;
            equal(
                "late".to_string(),
                SearchEngine::new(SelectionHistory::new())
                    .search("ab", &entries, 1, CancelToken::NONE)
                    .map_err(|error| error.to_string())?
                    .items[0]
                    .candidate
                    .id
                    .clone(),
            )
        },
    );
    checks.run(
        "已定义的应用入口优先于未注册 EXE，学习后可反转",
        || {
            let mut direct =
                SearchCandidate::new("direct", "nd", "unused", SearchItemKind::Application);
            direct.is_application_entry = true;
            direct.is_direct_executable = true;
            let mut known = SearchCandidate::new(
                "known",
                "Neat Download Manager",
                "unused",
                SearchItemKind::Application,
            );
            known.is_application_entry = true;
            let entries = vec![direct, known];
            let mut history = SelectionHistory::new();
            let mut collector = SearchCollector::new("nd", 1);
            let source = Arc::new(SliceSource {
                items: entries.clone(),
            });
            for (index, entry) in entries.iter().enumerate() {
                collector
                    .add(
                        source.clone(),
                        index as u64,
                        &entry.name,
                        entry.kind,
                        0,
                        Default::default(),
                        true,
                        entry.is_direct_executable,
                    )
                    .map_err(|error| error.to_string())?;
            }
            equal(
                "known".to_string(),
                collector.finish().map_err(|error| error.to_string())?.items[0]
                    .candidate
                    .id
                    .clone(),
            )?;
            equal(
                "known".to_string(),
                SearchEngine::new(history.clone())
                    .search("nd", &entries, 50, CancelToken::NONE)
                    .map_err(|error| error.to_string())?
                    .items[0]
                    .candidate
                    .id
                    .clone(),
            )?;
            history.record_selection("direct", "nd", Some(Instant::now()));
            equal(
                "direct".to_string(),
                SearchEngine::new(history)
                    .search("nd", &entries, 50, CancelToken::NONE)
                    .map_err(|error| error.to_string())?
                    .items[0]
                    .candidate
                    .id
                    .clone(),
            )
        },
    );

    Ok(CheckReport {
        total: checks.total,
        failures: checks.failures,
    })
}
