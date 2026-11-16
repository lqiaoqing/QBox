//! 与 MainWindow / CliApplication 相同的来源顺序：应用、快捷方式、各 live 卷。

use std::sync::Arc;

use crate::cancel::{CancelToken, Canceled};
use crate::catalog::{CatalogSnapshot, PackagedApplicationCatalog, ShortcutCatalog};
use crate::history::QueryHistory;
use crate::live::LiveSet;
use crate::qbi::OrdinalIgnoreCaseSet;
use crate::search::{SearchCollector, SearchResponse};

pub struct SearchPipeline {
    pub applications: Arc<PackagedApplicationCatalog>,
    pub shortcuts: Arc<ShortcutCatalog>,
    pub live: Arc<LiveSet>,
}

impl SearchPipeline {
    pub fn with_empty_catalogs(live: Arc<LiveSet>) -> Self {
        Self {
            applications: Arc::new(PackagedApplicationCatalog::create(Vec::new())),
            shortcuts: Arc::new(ShortcutCatalog::create(Vec::new(), &[], |_| None)),
            live,
        }
    }

    // C# 负责 COM / ShellLink 加载，刷新时把最终条目快照交进来。
    pub fn from_catalog_json(live: Arc<LiveSet>, bytes: &[u8]) -> Result<Self, String> {
        let snapshot: CatalogSnapshot = serde_json::from_slice(bytes)
            .map_err(|error| format!("目录快照无法解析：{error}"))?;
        Ok(Self {
            applications: Arc::new(PackagedApplicationCatalog::from_snapshot(
                snapshot.applications,
            )),
            shortcuts: Arc::new(ShortcutCatalog::from_snapshot(snapshot.shortcuts)),
            live,
        })
    }

    pub fn search(
        &self,
        query: &str,
        limit: i32,
        history: &QueryHistory,
        cancel: CancelToken,
    ) -> Result<SearchResponse, Canceled> {
        let mut collector = SearchCollector::new_with(query, limit, cancel);
        self.applications.search_into(&mut collector, history, cancel)?;
        self.shortcuts.search_into(&mut collector, history, cancel)?;
        let replaced = self.shortcuts.replaced_ids(query);
        let targets = self.shortcuts.matched_executable_targets(query);
        // C# 始终传入代表目标集合；空集合也会让 EXE 候选走一次路径判定，这里保持同一口径。
        let represented =
            OrdinalIgnoreCaseSet::from_values(targets.iter().map(String::as_str));
        self.live.search_into(
            &mut collector,
            history,
            cancel,
            replaced.as_ref(),
            Some(&represented),
        )?;
        collector.finish()
    }
}
