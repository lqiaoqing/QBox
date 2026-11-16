//! QBox 搜索引擎 Rust 校验端口：FuzzyMatcher、拼音应用名、收集器与排序、只读 .qbi 遍历。

pub mod cancel;
pub mod catalog;
pub mod checks;
pub mod fuzzy;
pub mod history;
pub mod live;
pub mod oracle;
pub mod pinyin;
pub mod pipeline;
pub mod qbi;
pub mod search;

pub use fuzzy::{FuzzyMatcher, MatchResult, PreparedQuery, TextRange};
pub use pinyin::{ApplicationNameMatch, PreparedApplicationName};

#[cfg(test)]
mod tests {
    use std::sync::Arc;

    use super::cancel::CancelToken;
    use super::checks::{run_launcher_ordering_checks, run_pinyin_checks};
    use super::history::{parse_history_blob, Instant};
    use super::oracle;
    use super::qbi::{
        build_volume_image, MaterializedFile, MaterializedName, OrdinalIgnoreCaseSet, VolumeGeometry,
        VolumeIndex,
    };

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

    #[test]
    fn history_blob_parses_csharp_layout() {
        let id = "ntfs:20c4668ac4666250:0001000000066f98:0001000000066f97:abcd";
        let mut bytes = Vec::new();
        bytes.extend_from_slice(b"QBXHST01");
        bytes.extend_from_slice(&1u32.to_le_bytes());
        bytes.extend_from_slice(&1u32.to_le_bytes());
        let units: Vec<u16> = id.encode_utf16().collect();
        bytes.extend_from_slice(&(units.len() as u32).to_le_bytes());
        for unit in &units {
            bytes.extend_from_slice(&unit.to_le_bytes());
        }
        bytes.push(1 | 2 | 4);
        bytes.extend_from_slice(&11i64.to_le_bytes());
        bytes.extend_from_slice(&22i64.to_le_bytes());
        bytes.extend_from_slice(&33i64.to_le_bytes());
        let history = parse_history_blob(&bytes).expect("历史 blob 应能解析");
        let signals = history.get(id);
        assert_eq!(signals.exact_query, Some(Instant(11)));
        assert_eq!(signals.prefix_query, Some(Instant(22)));
        assert_eq!(signals.last_used, Some(Instant(33)));
    }

    #[test]
    fn ordinal_ignore_case_set_matches_paths() {
        let set = OrdinalIgnoreCaseSet::from_values([r"C:\Apps\Tool.exe"]);
        assert!(set.contains(r"c:\apps\tool.EXE"));
        assert!(!set.contains(r"C:\Apps\Other.exe"));
    }

    #[test]
    fn memory_volume_image_roundtrips() {
        let geometry = VolumeGeometry {
            root: r"C:\".to_string(),
            serial: 1,
        };
        let file = |file: u64, directory: bool, parent: u64, name: &str| MaterializedFile {
            file,
            is_directory: directory,
            last_usn: None,
            link_count: 1,
            names: vec![MaterializedName {
                parent,
                name: name.to_string(),
                namespace: if file == 5 { 3 } else { 1 },
                attributes: 0,
            }],
        };
        let files = vec![
            file(5, true, 5, "."),
            file(100, true, 5, "Dir"),
            file(200, false, 100, "file.txt"),
        ];
        let image = build_volume_image(&geometry, 7, 8, 9, &files).expect("镜像应能生成");
        let mut volume = VolumeIndex::from_owned_image("memory.qbi", image).expect("镜像应能解析");
        volume.prepare_search(CancelToken::NONE).expect("PrepareSearch");
        let volume = Arc::new(volume);
        assert_eq!(volume.node_count, 3);
        assert_eq!(volume.name_count, 3);
        assert!(volume.find_file(100).is_some_and(|value| value.is_directory));
        assert_eq!(volume.path_for(2), r"C:\Dir\file.txt");
    }
}
