using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using QBox.Core.History;
using QBox.Core.Search;
using QBox.Windows.Indexing;
using QBox.Windows.Shortcuts;
using QBox.Windows.Applications;

if (args.Length == 3 && args[0] == "--pack")
{
    IndexSnapshotProbe.Pack(args[1], args[2]);
    return;
}
if (args.Length == 3 && args[0] == "--pack-live")
{
    LiveSnapshotPacker.Run(args[1], args[2]);
    return;
}
if (args.Length == 4 && args[0] == "--compare-indexes")
{
    IndexSnapshotProbe.Compare(args[1], args[2], args[3]);
    return;
}

if (args.Length >= 2 && args[0] == "--indexing")
{
    IndexingBenchmark.Run(args[1], args.Contains("--remap"), args.Contains("--repeated-names"));
    return;
}
if (args.Length is < 2 or > 3) throw new ArgumentException("用法：QBox.MemoryProbe <索引目录> <报告路径> [--live]");
var includeDesktop = args.Length == 3 && args[2] == "--desktop";
var useLive = includeDesktop || args.Length == 3 && args[2] == "--live";
var json = new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true };
var repetitions = int.TryParse(Environment.GetEnvironmentVariable("QBOX_PROBE_QUERY_PASSES"), out var requested) ? requested : 3;
if (repetitions is < 3 or > 30) throw new ArgumentException("查询探针重复次数必须为 3～30");
var stages = new List<object>();
var process = Process.GetCurrentProcess();
void Sample(string stage)
{
    // GC 仅用于对照探针的存活对象计量，产品不依靠强制回收或裁剪工作集压低数字。
    process.Refresh();
    var beforeCollection = process.PrivateMemorySize64;
    GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
    process.Refresh();
    stages.Add(new { stage, beforeCollectionPrivateBytes = beforeCollection, privateBytes = process.PrivateMemorySize64,
        workingSetBytes = process.WorkingSet64, managedBytes = GC.GetTotalMemory(false),
        peakWorkingSetBytes = process.PeakWorkingSet64 });
    Console.WriteLine($"{stage}: private={process.PrivateMemorySize64 / 1048576.0:F1} MiB, heap={GC.GetTotalMemory(false) / 1048576.0:F1} MiB");
}

Sample("empty");
var paths = Directory.GetFiles(args[0], "*.qbi").Order(StringComparer.Ordinal).ToArray();
if (paths.Length == 0) throw new InvalidDataException("没有基础快照");
var clock = Stopwatch.StartNew();
var live = useLive ? paths.Select(path => LiveIndexFile.Load(path).View).ToArray() : null;
var indexes = live?.Select(view => view.Base).ToArray() ?? paths.Select(path => VolumeIndexStore.Load(path)).ToArray();
var loadMs = clock.Elapsed.TotalMilliseconds;
Sample("loaded");
clock.Restart();
foreach (var index in indexes) index.PrepareSearch();
var prepareMs = clock.Elapsed.TotalMilliseconds;
Sample("prepared");
clock.Restart();
var shortcuts = includeDesktop ? ShortcutCatalog.Load(live!) : ShortcutCatalog.Empty;
var applications = includeDesktop ? PackagedApplicationCatalog.Load() : PackagedApplicationCatalog.Empty;
var catalogMs = clock.Elapsed.TotalMilliseconds;
if (includeDesktop) Sample("catalogs");
var history = new SelectionHistory();
var cases = new List<object>();
foreach (var query in new[] { "Any", "800Q", ".code", "nd", "Tra", "chatgpt", "rtt", "doubao", "qianwen",
    "豆包", "千问", "资料", "工作", "中", "任意中文不存在探针", "资", "😀", "𠀀", "文a", "ſ", "ı", "", "@", "!", "a@", "[", "#" })
{
    var timings = new List<double>();
    string? digest = null;
    var total = 0;
    for (var pass = 0; pass < repetitions; pass++)
    {
        clock.Restart();
        var collector = new SearchCollector(query, 9);
        applications.SearchInto(collector, history.ForQuery(query));
        shortcuts.SearchInto(collector, history.ForQuery(query));
        if (live == null)
            foreach (var index in indexes) index.SearchInto(collector, history.ForQuery(query));
        else
            foreach (var view in live) view.SearchInto(collector, history.ForQuery(query),
                replacedShortcutIds: shortcuts.ReplacedIds(query), representedExecutables: shortcuts.MatchedExecutableTargets(query));
        var result = collector.Finish();
        timings.Add(clock.Elapsed.TotalMilliseconds);
        // 前九项全部 SearchHit 字段和总数参与摘要；报告不保存机器上的真实文件路径。
        var current = Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(result, json)));
        if (digest != null && digest != current) throw new InvalidDataException("重复查询结果不一致");
        digest = current; total = result.TotalMatches;
    }
    cases.Add(new { query, totalMatches = total, responseSha256 = digest, timesMs = timings });
    Console.WriteLine($"query {query}: total={total}, warm={timings.Skip(1).Order().ElementAt((repetitions - 1) / 2):F2} ms");
}
Sample("queried");
var report = new { sourceFiles = paths.Select(path =>
    {
        using var file = File.OpenRead(path);
        return new { name = Path.GetFileName(path), bytes = file.Length,
            sha256 = Convert.ToHexStringLower(SHA256.HashData(file)) };
    }),
    useLive, includeDesktop, repetitions, catalogMs, shortcutActions = shortcuts.ActionCount,
    applications = applications.Count, applicationCatalogWarning = applications.Warning != null,
    changedRecords = live?.Sum(view => view.ChangedRecords) ?? 0,
    fileCount = indexes.Sum(index => (long)index.FileCount), nameCount = indexes.Sum(index => (long)index.NameCount),
    loadMs, prepareMs, stages, cases };
File.WriteAllText(args[1], JsonSerializer.Serialize(report, json));
GC.KeepAlive(indexes);
