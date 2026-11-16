using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using QBox.Core.History;
using QBox.Core.Search;
using QBox.Windows.Applications;
using QBox.Windows.Indexing;
using QBox.Windows.Search;
using QBox.Windows.Shortcuts;

var indexDir = "work/rust-validation/indexes";
var limit = 200;
var warmup = 1;
var runs = 5;
var queries = new[] { "1", "豆包", "CODE", "python" };
var outputPath = "work/rust-validation/csharp-bench.json";
var liveMode = false;
var catalogs = false;
string? catalogOut = null;
string? historyPath = null;
string? historySetOut = null;
for (var i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--index-dir": indexDir = args[++i]; break;
        case "--limit": limit = int.Parse(args[++i], CultureInfo.InvariantCulture); break;
        case "--warmup": warmup = int.Parse(args[++i], CultureInfo.InvariantCulture); break;
        case "--runs": runs = int.Parse(args[++i], CultureInfo.InvariantCulture); break;
        case "--output": outputPath = args[++i]; break;
        case "--live": liveMode = true; break;
        case "--catalogs": catalogs = true; break;
        case "--catalog-out": catalogOut = args[++i]; break;
        case "--history": historyPath = args[++i]; break;
        case "--history-set-out": historySetOut = args[++i]; break;
        default: throw new ArgumentException("未知参数：" + args[i]);
    }
}

indexDir = Path.GetFullPath(indexDir);
var paths = Directory.GetFiles(indexDir, "*.qbi").Order(StringComparer.Ordinal).ToArray();
if (paths.Length == 0) throw new InvalidDataException("索引目录中没有 .qbi");

VolumeIndex[] volumes;
LiveVolumeIndex[]? lives = null;
if (liveMode)
{
    var loaded = new List<LiveIndexFile>();
    foreach (var path in paths) loaded.Add(LiveIndexFile.Load(path));
    lives = loaded.Select(source => source.View).ToArray();
    volumes = lives.Select(view => view.Base).ToArray();
}
else
{
    volumes = paths.Select(path => VolumeIndexStore.Load(path)).ToArray();
    foreach (var index in volumes) index.PrepareSearch();
}
if (volumes.Select(index => index.Geometry.Serial).Distinct().Count() != volumes.Length)
    throw new InvalidDataException("同一个磁盘存在重复索引，请选择正确的索引目录");

var applications = catalogs ? PackagedApplicationCatalog.Load() : PackagedApplicationCatalog.Empty;
if (applications.Warning != null) Console.Error.WriteLine(applications.Warning);
ShortcutCatalog shortcuts = !catalogs ? ShortcutCatalog.Empty :
    liveMode ? ShortcutCatalog.Load(lives!) : ShortcutCatalog.Load(volumes);
catalogOut ??= catalogs ? "work/rust-validation/catalog-snapshot.json" : null;
if (catalogOut != null)
{
    catalogOut = Path.GetFullPath(catalogOut);
    var snapshot = EngineCatalogSnapshot.Create(applications, shortcuts);
    File.WriteAllBytes(catalogOut, snapshot.Serialize());
    Console.Error.WriteLine($"wrote catalog snapshot {catalogOut} apps={snapshot.Applications.Length} shortcuts={snapshot.Shortcuts.Length}");
}

SelectionHistory history;
if (historyPath != null)
{
    try { history = new SelectionHistory(new FileHistoryPersistence(Path.GetFullPath(historyPath))); }
    catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or System.Text.Json.JsonException)
    {
        Console.Error.WriteLine("历史无法读取，使用空历史：" + error.Message);
        history = new SelectionHistory();
    }
}
else history = new SelectionHistory();
if (historySetOut != null)
{
    historySetOut = Path.GetFullPath(historySetOut);
    File.WriteAllBytes(historySetOut, EngineHistoryBlob.SerializeSet(history, queries));
    Console.Error.WriteLine("wrote history snapshot set " + historySetOut);
}
var fullPipeline = liveMode || catalogs;

SearchResponse SearchOnce(string query)
{
    var collector = new SearchCollector(query, limit);
    var view = history.ForQuery(query);
    ISet<string>? replaced = null;
    IReadOnlySet<string>? represented = null;
    if (fullPipeline)
    {
        applications.SearchInto(collector, view);
        shortcuts.SearchInto(collector, view);
        replaced = shortcuts.ReplacedIds(query);
        represented = shortcuts.MatchedExecutableTargets(query);
    }
    if (liveMode)
        foreach (var live in lives!) live.SearchInto(collector, view, default, replaced, represented);
    else
        foreach (var volume in volumes) volume.SearchInto(collector, view, default, replaced, represented);
    return collector.Finish();
}

var results = new List<object>();
foreach (var query in queries)
{
    for (var i = 0; i < warmup; i++)
        SearchOnce(query);
    var times = new List<double>();
    SearchResponse? last = null;
    for (var i = 0; i < runs; i++)
    {
        var clock = Stopwatch.StartNew();
        last = SearchOnce(query);
        clock.Stop();
        times.Add(clock.Elapsed.TotalMilliseconds);
    }
    var items = last!.Items.Select(hit => new
    {
        id = hit.Candidate.Id,
        name = hit.Candidate.Name,
        target = hit.Candidate.Target,
        kind = hit.Candidate.Kind.ToString(),
        score = hit.Match.AlignmentScore,
        group = hit.Group.ToString(),
        application = hit.Candidate.IsApplicationEntry,
        direct = hit.Candidate.IsDirectExecutable,
    }).ToArray();
    results.Add(new
    {
        query,
        total = last.TotalMatches,
        shown = items.Length,
        limit,
        times_ms = times,
        min_ms = times.Min(),
        median_ms = Median(times),
        items,
    });
    Console.Error.WriteLine(
        $"query={JsonSerializer.Serialize(query)} total={last.TotalMatches} shown={items.Length} min_ms={times.Min():0.###} median_ms={Median(times):0.###} times=[{string.Join(", ", times.Select(value => value.ToString("0.###", CultureInfo.InvariantCulture)))}]");
}

var payload = new
{
    engine = "csharp",
    mode = liveMode ? "live" : "static",
    catalogs,
    history = historyPath,
    indexDir,
    volumes = volumes.Select(volume => new
    {
        root = volume.Geometry.Root,
        serial = volume.Geometry.Serial.ToString("x16", CultureInfo.InvariantCulture),
        files = volume.FileCount,
        names = volume.NameCount,
        changes = lives?.First(source => ReferenceEquals(source.Base, volume)).ChangedRecords,
    }),
    timing = "SearchInto apps/shortcuts/all volumes + Finish; excludes process start, mmap/load, SHA-256, PrepareSearch, and warmup runs. Each timed run includes presence select, CanMatch/ScoreMatched, collector insert, and Finish materialization. Stopwatch, same process.",
    warmup,
    runs,
    limit,
    results,
};
var json = JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true });
outputPath = Path.GetFullPath(outputPath);
File.WriteAllText(outputPath, json);
Console.Error.WriteLine("wrote " + outputPath);
return;

static double Median(List<double> values)
{
    var ordered = values.OrderBy(value => value).ToArray();
    var mid = ordered.Length / 2;
    return ordered.Length % 2 == 0 ? (ordered[mid - 1] + ordered[mid]) / 2 : ordered[mid];
}
