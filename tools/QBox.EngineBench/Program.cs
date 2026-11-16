using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using QBox.Core.History;
using QBox.Core.Search;
using QBox.Windows.Indexing;

var indexDir = "work/rust-validation/indexes";
var limit = 200;
var warmup = 1;
var runs = 5;
var queries = new[] { "1", "豆包", "CODE", "python" };
var outputPath = "work/rust-validation/csharp-bench.json";
for (var i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--index-dir": indexDir = args[++i]; break;
        case "--limit": limit = int.Parse(args[++i], CultureInfo.InvariantCulture); break;
        case "--warmup": warmup = int.Parse(args[++i], CultureInfo.InvariantCulture); break;
        case "--runs": runs = int.Parse(args[++i], CultureInfo.InvariantCulture); break;
        case "--output": outputPath = args[++i]; break;
        default: throw new ArgumentException("未知参数：" + args[i]);
    }
}

indexDir = Path.GetFullPath(indexDir);
var paths = Directory.GetFiles(indexDir, "*.qbi").Order(StringComparer.OrdinalIgnoreCase).ToArray();
if (paths.Length == 0) throw new InvalidDataException("索引目录中没有 .qbi");

var volumes = new List<(string Path, VolumeIndex Index, long LoadMs, long PrepareMs)>();
foreach (var path in paths)
{
    var load = Stopwatch.StartNew();
    var index = VolumeIndexStore.Load(path);
    load.Stop();
    var prepare = Stopwatch.StartNew();
    index.PrepareSearch();
    prepare.Stop();
    volumes.Add((path, index, load.ElapsedMilliseconds, prepare.ElapsedMilliseconds));
    Console.Error.WriteLine(
        $"loaded {path} root={index.Geometry.Root} serial={index.Geometry.Serial:x16} files={index.FileCount} names={index.NameCount} load_ms={load.ElapsedMilliseconds} prepare_ms={prepare.ElapsedMilliseconds}");
}

SearchResponse SearchOnce(string query)
{
    var collector = new SearchCollector(query, limit);
    var history = new SelectionHistory().ForQuery(query);
    foreach (var volume in volumes)
        volume.Index.SearchInto(collector, history);
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
    indexDir,
    volumes = volumes.Select(volume => new
    {
        path = volume.Path,
        root = volume.Index.Geometry.Root,
        serial = volume.Index.Geometry.Serial.ToString("x16", CultureInfo.InvariantCulture),
        files = volume.Index.FileCount,
        names = volume.Index.NameCount,
        load_ms = volume.LoadMs,
        prepare_ms = volume.PrepareMs,
    }),
    timing = "SearchInto all volumes + Finish; excludes process start, mmap/load, SHA-256, PrepareSearch, and warmup runs. Each timed run includes presence select, CanMatch/ScoreMatched, collector insert, and Finish materialization. Stopwatch, same process.",
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
