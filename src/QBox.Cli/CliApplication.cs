using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using QBox.Core.History;
using QBox.Core.Matching;
using QBox.Core.Search;
using QBox.Windows.Ntfs;
using System.ComponentModel;
using QBox.Windows.Indexing;
using QBox.Windows.Shortcuts;
using QBox.Windows.Applications;
using QBox.Windows;

namespace QBox.Cli;

internal static class CliApplication
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Converters = { new JsonStringEnumConverter(allowIntegerValues: false) }
    };

    public static async Task<int> RunAsync(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;
        if (args.Length == 0 || args[0] is "--help" or "-h" or "help")
        {
            Help();
            return 0;
        }
        using var cancellation = new CancellationTokenSource();
        ConsoleCancelEventHandler interrupt = (_, e) => { e.Cancel = true; cancellation.Cancel(); };
        Console.CancelKeyPress += interrupt;
        try
        {
            if (args[0] == "match")
            {
                if (args.Length != 3) throw new ArgumentException("用法：match <查询> <候选名称>");
                var match = new FuzzyMatcher().Match(args[1], args[2], cancellation.Token);
                WriteJson(match);
                return match.IsMatch ? 0 : 1;
            }
            if (args[0] == "volume-probe")
            {
                if (!OperatingSystem.IsWindows()) throw new NotSupportedException("NTFS 卷读取仅支持 Windows");
                var probeOptions = Options(args[1..]);
                using var volume = new NativeNtfsVolume(Required(probeOptions, "drive"));
                var root = new NtfsFileResolver(volume).Resolve(new(5), cancellation.Token)
                    ?? throw new InvalidDataException("无法读取根目录记录");
                WriteJson(new { volume.Geometry, Journal = volume.ReadJournalBounds(), Root = root.Base.File });
                return 0;
            }
            if (args[0] == "index-info")
            {
                var infoOptions = Options(args[1..]);
                WriteIndexInfo(VolumeIndexStore.Load(Required(infoOptions, "index"), cancellation.Token));
                return 0;
            }
            if (args[0] is "index-build" or "index-sync")
            {
                if (!OperatingSystem.IsWindows()) throw new NotSupportedException("NTFS 卷读取仅支持 Windows");
                var indexOptions = Options(args[1..]);
                var indexPath = Required(indexOptions, "index");
                using var writer = VolumeIndexStore.AcquireWriter(indexPath);
                var saved = args[0] == "index-sync" ? VolumeIndexStore.Load(indexPath, cancellation.Token) : null;
                using var volume = new NativeNtfsVolume(Required(indexOptions, "drive"));
                var builder = new NtfsIndexBuilder(volume);
                var progress = new ConsoleProgress();
                var built = saved == null ? builder.Build(progress, cancellation.Token) : builder.Synchronize(saved, progress, cancellation.Token);
                VolumeIndexStore.Save(indexPath, built, cancellation.Token);
                WriteIndexInfo(built);
                return 0;
            }
            if (args[0] is not ("search" or "open" or "recent"))
                throw new ArgumentException($"未知命令：{args[0]}");
            var options = Options(args[1..]);
            var historyPath = options.GetValueOrDefault("history") ?? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "QBox", "history.json");
            var history = new SelectionHistory(new FileHistoryPersistence(historyPath));
            if (args[0] == "recent")
            {
                WriteJson(history.Snapshot.RecentQueries);
                return 0;
            }
            var query = Required(options, "query");
            var sources = new[] { "index", "index-directory", "catalog" }.Count(options.ContainsKey);
            if (sources != 1) throw new ArgumentException("请指定 --index、--index-directory 或 --catalog 中的一项");
            IEnumerable<SearchCandidate> candidates;
            VolumeIndex[]? indexes = null;
            LiveVolumeIndex[]? live = null;
            if (options.ContainsKey("catalog")) candidates = ReadCatalog(Required(options, "catalog")).Items;
            else
            {
                var paths = options.ContainsKey("index") ? new[] { Required(options, "index") } :
                    Directory.GetFiles(Required(options, "index-directory"), "*.qbi").Order(StringComparer.Ordinal).ToArray();
                if (paths.Length == 0) throw new InvalidDataException("索引目录中没有 .qbi 文件");
                live = paths.Select(path => LiveIndexFile.Load(path, cancellation.Token).View).ToArray();
                indexes = live.Select(view => view.Base).ToArray();
                if (indexes.Select(index => index.Geometry.Serial).Distinct().Count() != indexes.Length)
                    throw new InvalidDataException("同一个卷存在多份索引，请移走重复快照再查询");
                candidates = [];
            }
            if (args[0] == "search")
            {
                var limit = options.TryGetValue("limit", out var value) ? int.Parse(value!, CultureInfo.InvariantCulture) : 50;
                SearchResponse result;
                if (indexes == null) result = new SearchEngine(history).Search(query, candidates, limit, cancellation.Token);
                else
                {
                    var collector = new SearchCollector(query, limit, cancellation.Token);
                    var view = history.ForQuery(query);
                    var applications = PackagedApplicationCatalog.Load(cancellation.Token);
                    if (applications.Warning != null) Console.Error.WriteLine(applications.Warning);
                    applications.SearchInto(collector, view, cancellation.Token);
                    var shortcuts = ShortcutCatalog.Load(live!, cancellation.Token);
                    shortcuts.SearchInto(collector, view, cancellation.Token);
                    var replaced = shortcuts.ReplacedIds(query);
                    var representedExecutables = shortcuts.MatchedExecutableTargets(query);
                    foreach (var index in live!) index.SearchInto(collector, view, cancellation.Token, replaced, representedExecutables);
                    result = collector.Finish();
                }
                if (options.ContainsKey("json")) WriteJson(result);
                else
                {
                    Console.WriteLine($"命中 {result.TotalMatches} 个目标，显示 {result.Items.Count} 个");
                    foreach (var hit in result.Items)
                    {
                        Console.WriteLine($"{hit.Candidate.Id}\t{Highlighted(hit.Candidate.Name, hit.Match.Highlights)}");
                        Console.WriteLine($"  {hit.Candidate.Target}");
                    }
                }
                return 0;
            }
            var id = Required(options, "id");
            var candidate = candidates.FirstOrDefault(item => item.Id == id);
            if (candidate == null && indexes != null)
            {
                // 搜索别名后返回的是合并动作的主快捷方式 ID，主名称未必匹配该查询。
                var shortcuts = ShortcutCatalog.Load(live!, cancellation.Token);
                var applications = PackagedApplicationCatalog.Load(cancellation.Token);
                // 仅在临时历史中把指定 ID 提到首位，避免打开时物化全盘结果或提前污染用户历史。
                var lookup = new SelectionHistory(); lookup.RecordSelection(id, query);
                var collector = new SearchCollector(query, 1, cancellation.Token);
                var view = lookup.ForQuery(query);
                applications.SearchInto(collector, view, cancellation.Token);
                shortcuts.SearchInto(collector, view, cancellation.Token);
                foreach (var index in live!) index.SearchInto(collector, view, cancellation.Token, shortcuts.ReplacedIds(query), shortcuts.MatchedExecutableTargets(query));
                candidate = collector.Finish().Items.FirstOrDefault(hit => hit.Candidate.Id == id)?.Candidate;
            }
            if (candidate == null) throw new ArgumentException($"找不到目标 ID：{id}");
            var activated = await new SearchActions(history).ActivateAsync(candidate, query,
                options.ContainsKey("catalog") ? Launch : LaunchIndexed, cancellation.Token);
            if (!activated.Opened)
            {
                Console.Error.WriteLine($"打开失败：{activated.Error}");
                return 1;
            }
            Console.WriteLine($"已打开：{candidate.Name}");
            if (activated.HistoryError != null) Console.Error.WriteLine($"历史未保存：{activated.HistoryError}");
            return 0;
        }
        catch (OperationCanceledException)
        {
            Console.Error.WriteLine("查询已取消");
            return 130;
        }
        catch (Exception error) when (error is ArgumentException or IOException or UnauthorizedAccessException
                                     or InvalidDataException or JsonException or FormatException or OverflowException or Win32Exception or NotSupportedException)
        {
            Console.Error.WriteLine(error.Message);
            return 2;
        }
        finally { Console.CancelKeyPress -= interrupt; }
    }

    private static Task<bool> Launch(SearchCandidate candidate, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (candidate.AppUserModelId != null)
        {
            if (!OperatingSystem.IsWindows()) throw new NotSupportedException("应用入口仅支持 Windows");
            return Task.FromResult(ShellTargetLauncher.OpenIndexed(candidate, cancellationToken));
        }
        if (string.IsNullOrWhiteSpace(candidate.Target)) throw new ArgumentException("目标没有启动路径");
        using var process = Process.Start(new ProcessStartInfo
        {
            FileName = candidate.Target,
            Arguments = candidate.Arguments ?? "",
            UseShellExecute = true
        });
        // ShellExecute 打开目录或交给已有进程时可能返回 null，未抛错即表示请求已提交。
        return Task.FromResult(true);
    }

    private static Task<bool> LaunchIndexed(SearchCandidate candidate, CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsWindows()) throw new NotSupportedException("索引目标打开仅支持 Windows");
        return Task.FromResult(ShellTargetLauncher.OpenIndexed(candidate, cancellationToken));
    }

    private static Catalog ReadCatalog(string path)
    {
        var catalog = JsonSerializer.Deserialize<Catalog>(File.ReadAllBytes(path), Json)
            ?? throw new InvalidDataException("候选文件为空");
        if (catalog.Version != 1 || catalog.Items == null) throw new InvalidDataException("候选文件格式版本无效");
        var identities = new Dictionary<string, SearchCandidate>(StringComparer.Ordinal);
        foreach (var item in catalog.Items)
        {
            if (item == null || string.IsNullOrWhiteSpace(item.Id) || string.IsNullOrEmpty(item.Name) || string.IsNullOrWhiteSpace(item.Target))
                throw new InvalidDataException("候选必须具有 ID、名称和目标路径");
            if (identities.TryGetValue(item.Id, out var old) &&
                (!StringComparer.OrdinalIgnoreCase.Equals(old.Target, item.Target) || old.Arguments != item.Arguments))
                throw new InvalidDataException($"同一 ID 对应了不同的启动动作：{item.Id}");
            identities[item.Id] = item;
        }
        return catalog;
    }

    private static Dictionary<string, string?> Options(string[] args)
    {
        var values = new Dictionary<string, string?>(StringComparer.Ordinal);
        for (var i = 0; i < args.Length; i++)
        {
            if (!args[i].StartsWith("--", StringComparison.Ordinal)) throw new ArgumentException($"无效参数：{args[i]}");
            var key = args[i][2..];
            if (key is not ("catalog" or "history" or "query" or "limit" or "id" or "json" or "drive" or "index" or "index-directory"))
                throw new ArgumentException($"未知选项：--{key}");
            if (values.ContainsKey(key)) throw new ArgumentException($"重复选项：--{key}");
            if (key == "json") values.Add(key, null);
            else
            {
                if (++i == args.Length) throw new ArgumentException($"--{key} 缺少值");
                values.Add(key, args[i]);
            }
        }
        return values;
    }

    private static string Required(Dictionary<string, string?> options, string key) =>
        options.TryGetValue(key, out var value) && value != null ? value : throw new ArgumentException($"缺少 --{key}");

    private static string Highlighted(string text, IReadOnlyList<TextRange> ranges)
    {
        var output = new StringBuilder();
        var start = 0;
        foreach (var range in ranges)
        {
            output.Append(text.AsSpan(start, range.Start - start));
            output.Append('[').Append(text.AsSpan(range.Start, range.Length)).Append(']');
            start = range.Start + range.Length;
        }
        return output.Append(text.AsSpan(start)).ToString();
    }

    private static void WriteJson<T>(T value) => Console.WriteLine(JsonSerializer.Serialize(value, Json));

    private static void WriteIndexInfo(VolumeIndex index) => WriteJson(new
    {
        index.Geometry, index.Checkpoint, index.CreatedAt, index.FileCount, index.NameCount,
        Notice = "这是该 NTFS 卷在保存日志位置的快照；离线查询前请同步变更"
    });

    private sealed class ConsoleProgress : IProgress<IndexProgress>
    {
        public void Report(IndexProgress value) => Console.Error.WriteLine($"{value.Stage}：{value.Processed}，文件记录 {value.Files}");
    }

    private static void Help() => Console.WriteLine("""
        QBox 搜索核心
        match <查询> <候选名称>
        volume-probe --drive <C:>
        index-build --drive <C:> --index <索引.qbi>
        index-sync --drive <C:> --index <索引.qbi>
        index-info --index <索引.qbi>
        search --index <索引.qbi> --query <查询> [--history <历史.json>] [--limit 50] [--json]
        search --index-directory <索引目录> --query <查询> [--history <历史.json>] [--limit 50] [--json]
        search --catalog <候选.json> --query <查询> [--history <历史.json>] [--limit 50] [--json]
        open --catalog <候选.json> --query <查询> --id <目标ID> [--history <历史.json>]
        recent [--history <历史.json>]

        卷读取命令需要管理员权限，仅以只读方式访问 NTFS；不创建或修改 USN 日志。
        index-sync 断档时要求重建，失败或取消保留原索引；search --index 查询已保存的快照。
        open 也可以使用 --index 替代 --catalog。候选 JSON 格式见 samples/catalog.example.json。
        查询只读历史；open 请求提交成功后记录使用。Ctrl+C 取消查询。
        """);

    private sealed record Catalog(int Version, SearchCandidate[] Items);
}
