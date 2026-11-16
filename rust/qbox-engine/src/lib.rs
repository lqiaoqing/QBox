//! QBox 搜索引擎 Rust 校验端口。当前仅包含与 C# `FuzzyMatcher` 对齐的匹配器。

pub mod fuzzy;
pub mod oracle;

pub use fuzzy::{FuzzyMatcher, MatchResult, PreparedQuery, TextRange};

#[cfg(test)]
mod tests {
    use super::oracle;

    #[test]
    fn ascii_oracle_includes_holdout() {
        let report = oracle::run_ascii_oracle(&oracle::default_fixture_dir(), true)
            .expect("应能读取 C# ascii-*.json 对照样本");
        assert_eq!(report.total, 685, "对照样本总数应保持 685");
        assert_eq!(report.executed, 685, "含留出样本时应执行全部 685 组");
        assert_eq!(report.failed(), 0, "Rust 匹配器应与 C# 分值完全一致");
    }
}
