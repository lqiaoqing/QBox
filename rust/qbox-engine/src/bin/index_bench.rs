//! 只读 .qbi 引擎路径计时：与 tools/QBox.EngineBench 同一协议，不含 UI。

use std::fs;
use std::path::{Path, PathBuf};
use std::sync::Arc;
use std::time::Instant;

use qbox_engine::cancel::CancelToken;
use qbox_engine::qbi::VolumeIndex;
use qbox_engine::search::SearchCollector;
use serde_json::{json, Value};

fn main() {
    let mut index_dir = PathBuf::from("work/rust-validation/indexes");
    let mut limit: i32 = 200;
    let mut warmup = 1usize;
    let mut runs = 5usize;
    let mut output_path = PathBuf::from("work/rust-validation/rust-bench.json");
    let args: Vec<String> = std::env::args().skip(1).collect();
    let mut i = 0;
    while i < args.len() {
        match args[i].as_str() {
            "--index-dir" => {
                i += 1;
                index_dir = PathBuf::from(&args[i]);
            }
            "--limit" => {
                i += 1;
                limit = args[i].parse().expect("limit");
            }
            "--warmup" => {
                i += 1;
                warmup = args[i].parse().expect("warmup");
            }
            "--runs" => {
                i += 1;
                runs = args[i].parse().expect("runs");
            }
            "--output" => {
                i += 1;
                output_path = PathBuf::from(&args[i]);
            }
            other => panic!("未知参数：{other}"),
        }
        i += 1;
    }

    let index_dir = fs::canonicalize(&index_dir).unwrap_or_else(|_| index_dir);
    let mut paths = collect_qbi(&index_dir);
    if paths.is_empty() {
        panic!("索引目录中没有 .qbi");
    }
    paths.sort_by(|left, right| {
        let left = left.to_string_lossy().to_ascii_lowercase();
        let right = right.to_string_lossy().to_ascii_lowercase();
        left.cmp(&right)
    });

    let mut volumes = Vec::new();
    let mut volume_meta = Vec::new();
    for path in &paths {
        let load = Instant::now();
        let mut index = VolumeIndex::load(path)
            .unwrap_or_else(|error| panic!("无法加载 {}: {error}", path.display()));
        let load_ms = load.elapsed().as_secs_f64() * 1000.0;
        let prepare = Instant::now();
        index
            .prepare_search(CancelToken::NONE)
            .expect("PrepareSearch");
        let prepare_ms = prepare.elapsed().as_secs_f64() * 1000.0;
        eprintln!(
            "loaded {} root={} serial={:016x} files={} names={} load_ms={:.3} prepare_ms={:.3}",
            path.display(),
            index.geometry.root,
            index.geometry.serial,
            index.node_count,
            index.name_count,
            load_ms,
            prepare_ms
        );
        volume_meta.push(json!({
            "path": path.display().to_string(),
            "root": index.geometry.root,
            "serial": format!("{:016x}", index.geometry.serial),
            "files": index.node_count,
            "names": index.name_count,
            "load_ms": load_ms.round() as i64,
            "prepare_ms": prepare_ms.round() as i64,
        }));
        volumes.push(Arc::new(index));
    }

    let queries = ["1", "豆包", "CODE", "python"];
    let mut results = Vec::new();
    for query in queries {
        for _ in 0..warmup {
            search_once(&volumes, query, limit);
        }
        let mut times = Vec::new();
        let mut last = None;
        for _ in 0..runs {
            let clock = Instant::now();
            let response = search_once(&volumes, query, limit);
            times.push(clock.elapsed().as_secs_f64() * 1000.0);
            last = Some(response);
        }
        let last = last.expect("至少一次计时运行");
        let items: Vec<Value> = last
            .items
            .iter()
            .map(|hit| {
                json!({
                    "id": hit.candidate.id,
                    "name": hit.candidate.name,
                    "target": hit.candidate.target,
                    "kind": kind_name(hit.candidate.kind),
                    "score": hit.match_result.alignment_score,
                    "group": group_name(hit.group),
                    "application": hit.candidate.is_application_entry,
                    "direct": hit.candidate.is_direct_executable,
                })
            })
            .collect();
        let min_ms = times.iter().copied().fold(f64::INFINITY, f64::min);
        let median_ms = median(&times);
        eprintln!(
            "query={:?} total={} shown={} min_ms={:.3} median_ms={:.3} times=[{}]",
            query,
            last.total_matches,
            items.len(),
            min_ms,
            median_ms,
            times
                .iter()
                .map(|value| format!("{value:.3}"))
                .collect::<Vec<_>>()
                .join(", ")
        );
        results.push(json!({
            "query": query,
            "total": last.total_matches,
            "shown": items.len(),
            "limit": limit,
            "times_ms": times,
            "min_ms": min_ms,
            "median_ms": median_ms,
            "items": items,
        }));
    }

    let payload = json!({
        "engine": "rust",
        "indexDir": index_dir.display().to_string(),
        "volumes": volume_meta,
        "timing": "SearchInto all volumes + Finish; excludes process start, mmap/load, SHA-256, PrepareSearch, and warmup runs. Each timed run includes presence select, CanMatch/ScoreMatched, collector insert, and Finish materialization. std::time::Instant, same process.",
        "warmup": warmup,
        "runs": runs,
        "limit": limit,
        "results": results,
    });
    let output_path = fs::canonicalize(output_path.parent().unwrap_or_else(|| Path::new(".")))
        .unwrap_or_else(|_| {
            output_path
                .parent()
                .unwrap_or_else(|| Path::new("."))
                .to_path_buf()
        })
        .join(output_path.file_name().expect("output name"));
    fs::write(
        &output_path,
        serde_json::to_vec_pretty(&payload).expect("json"),
    )
    .expect("write");
    eprintln!("wrote {}", output_path.display());
}

fn search_once(
    volumes: &[Arc<VolumeIndex>],
    query: &str,
    limit: i32,
) -> qbox_engine::search::SearchResponse {
    let mut collector = SearchCollector::new(query, limit);
    for volume in volumes {
        volume
            .search_into(&mut collector, CancelToken::NONE)
            .expect("SearchInto");
    }
    collector.finish().expect("Finish")
}

fn collect_qbi(dir: &Path) -> Vec<PathBuf> {
    let mut paths = Vec::new();
    let entries =
        fs::read_dir(dir).unwrap_or_else(|error| panic!("无法读取 {}: {error}", dir.display()));
    for entry in entries {
        let entry = entry.expect("dirent");
        let path = entry.path();
        if path
            .extension()
            .and_then(|value| value.to_str())
            .map(|value| value.eq_ignore_ascii_case("qbi"))
            == Some(true)
        {
            paths.push(path);
        }
    }
    paths
}

fn median(values: &[f64]) -> f64 {
    let mut ordered = values.to_vec();
    ordered.sort_by(|left, right| left.partial_cmp(right).unwrap());
    let mid = ordered.len() / 2;
    if ordered.len() % 2 == 0 {
        (ordered[mid - 1] + ordered[mid]) / 2.0
    } else {
        ordered[mid]
    }
}

fn kind_name(kind: qbox_engine::search::SearchItemKind) -> &'static str {
    use qbox_engine::search::SearchItemKind::*;
    match kind {
        File => "File",
        Directory => "Directory",
        Application => "Application",
        Shortcut => "Shortcut",
    }
}

fn group_name(group: qbox_engine::search::RankGroup) -> &'static str {
    use qbox_engine::search::RankGroup::*;
    match group {
        ExactQueryHistory => "ExactQueryHistory",
        PrefixQueryHistory => "PrefixQueryHistory",
        Ordinary => "Ordinary",
        ApplicationEntry => "ApplicationEntry",
        ExecutableEntry => "ExecutableEntry",
    }
}
