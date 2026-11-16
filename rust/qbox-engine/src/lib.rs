//! QBox 搜索引擎 Rust 校验端口：FuzzyMatcher、拼音应用名、收集器与排序。

pub mod cancel;
pub mod catalog;
pub mod checks;
pub mod fuzzy;
pub mod history;
pub mod oracle;
pub mod pinyin;
pub mod search;

pub use fuzzy::{FuzzyMatcher, MatchResult, PreparedQuery, TextRange};
pub use pinyin::{ApplicationNameMatch, PreparedApplicationName};

#[cfg(test)]
mod tests {
    use super::checks::{run_launcher_ordering_checks, run_pinyin_checks};
    use super::oracle;

    #[test]
    fn ascii_oracle_includes_holdout() {
        let report = oracle::run_ascii_oracle(&oracle::default_fixture_dir(), true)
            .expect("应能读取 C# ascii-*.json 对照样本");
        assert_eq!(report.total, 685, "对照样本总数应保持 685");
        assert_eq!(report.executed, 685, "含留出样本时应执行全部 685 组");
        assert_eq!(report.failed(), 0, "Rust 匹配器应与 C# 分值完全一致");
    }

    #[test]
    fn pinyin_checks_match_csharp() {
        let report = run_pinyin_checks();
        assert_eq!(
            report.failures.len(),
            0,
            "拼音失败：{:?}",
            report
                .failures
                .iter()
                .map(|failure| format!("{}: {}", failure.name, failure.message))
                .collect::<Vec<_>>()
        );
        assert_eq!(report.total, 36, "PinyinChecks 条数应保持 36");
    }

    #[test]
    fn launcher_ordering_checks_match_csharp() {
        let report = run_launcher_ordering_checks().expect("应能读取 launcher-ordering.json");
        assert_eq!(
            report.failures.len(),
            0,
            "排序/收集失败：{:?}",
            report
                .failures
                .iter()
                .take(10)
                .map(|failure| format!("{}: {}", failure.name, failure.message))
                .collect::<Vec<_>>()
        );
        assert_eq!(report.total, 2058, "LauncherOrderingChecks 条数应保持 2058");
    }
}
