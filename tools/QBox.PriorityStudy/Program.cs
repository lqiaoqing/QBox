using System.Security.Cryptography;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using QBox.Core.History;
using QBox.Core.Search;
using QBox.Windows.Applications;
using QBox.Windows.Indexing;
using QBox.Windows.Shortcuts;

if (args.Length != 3) throw new ArgumentException("用法：<索引目录> <默认表 JSON> <输出 JSON>");
if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("本机应用入口与路径对照需要 Windows");
var json = new JsonSerializerOptions(JsonSerializerDefaults.Web)
{
    WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    Converters = { new JsonStringEnumConverter() }
};
var defaults = JsonSerializer.Deserialize<DefaultTables>(File.ReadAllText(args[1]), json)
    ?? throw new InvalidDataException("默认表为空");
var rules = new ProposedRules(defaults.Copies[0]);
rules.CheckBoundaries();
var paths = Directory.GetFiles(args[0], "*.qbi").Order(StringComparer.Ordinal).ToArray();
if (paths.Length == 0) throw new InvalidDataException("没有索引快照");
var fingerprints = paths.Select(path => new { Name = Path.GetFileName(path), Sha256 = Hash(path) }).ToArray();
var indexes = paths.Select(path => VolumeIndexStore.Load(path)).ToArray();
foreach (var index in indexes) index.PrepareSearch();
var shortcuts = ShortcutCatalog.Load(indexes);
var applications = PackagedApplicationCatalog.Load();
if (applications.Warning != null) throw new IOException(applications.Warning);
var queries = new[] { "Any", "800Q", ".code", "nd", "Tra", "chatgpt", "rtt", "doubao", "qianwen" };
var cases = new List<object>();
foreach (var query in queries)
{
    Console.WriteLine($"完整收集：{query}");
    cases.Add(Study(query));
    // 每次只保留报告摘要，避免上一查询的完整路径与下一查询同时驻留。
    GC.Collect();
}
var report = new
{
    Version = "0.1.3 offline priority proposal", CreatedUtc = DateTimeOffset.UtcNow,
    History = "fresh in-memory history; no user history read or written",
    Scope = "complete current matches; stable partition only; no candidates filtered or index changes",
    Assumptions = new[] {
        "A: longest boundary-matching configured path wins; also demote node_modules ancestors; protect app/history entries",
        "B: A plus all five name patterns on the target basename using recovered file/folder flags",
        "These are QBox proposals, not proven Listary runtime semantics; system/hidden attribute filtering is excluded"
    },
    ListaryBinarySha256 = defaults.BinarySha256, Indexes = fingerprints,
    Applications = applications.Count, ShortcutActions = shortcuts.ActionCount, ExpandedPaths = rules.ExpandedPaths,
    BoundaryChecks = 10, Cases = cases
};
Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(args[2]))!);
File.WriteAllText(args[2], JsonSerializer.Serialize(report, json) + Environment.NewLine);
Console.WriteLine($"已写入 {args[2]}");

object Study(string query)
{
    var history = new SelectionHistory();
    var before = Collect(query, history, int.MaxValue);
    if (before.Items.Count != before.TotalMatches) throw new InvalidDataException("完整候选未收齐");
    var bounded = Collect(query, history, 9);
    if (JsonSerializer.Serialize(bounded.Items, json) != JsonSerializer.Serialize(before.Items.Take(9), json) ||
        bounded.TotalMatches != before.TotalMatches) throw new InvalidDataException("完整收集与已发布的首屏排序不一致");
    var decisions = before.Items.Select(hit => rules.Classify(hit)).ToArray();
    var original = Enumerable.Range(0, before.Items.Count).ToArray();
    var a = original.OrderBy(i => decisions[i].PathReason == null ? 0 : 1).ToArray();
    var b = original.OrderBy(i => decisions[i].PathReason == null && decisions[i].NameReason == null ? 0 : 1).ToArray();
    // 稳定分区必须完整保留原身份；在此直接核对原序号排列，不能只比较总数。
    foreach (var variant in new[] { a, b })
        if (!variant.Order().SequenceEqual(original)) throw new InvalidDataException("模拟改变了召回集合");
    var targetIndices = original.Where(i => IsTarget(query, before.Items[i].Candidate)).ToArray();
    if (targetIndices.Length != (query == "doubao" ? 2 : 1))
        throw new InvalidDataException("用户目标缺失或不唯一，不能把空对照当作通过");
    var aRanks = Invert(a);
    var bRanks = Invert(b);
    var targets = targetIndices.Select(i => new
    {
        before.Items[i].Candidate.Name, before.Items[i].Candidate.Target,
        Before = i + 1, PathOnly = aRanks[i], AllNamePatterns = bRanks[i], Decision = decisions[i],
        ProtectedEntriesAhead = original.Take(i).Count(j => decisions[j].ProtectedEntry),
        PathDemotedEntriesAhead = original.Take(i).Count(j => decisions[j].PathReason != null)
    }).ToArray();
    var learned = targetIndices.Select(i =>
    {
        // 仅在独立内存历史中模拟一次成功选择，既不启动目标，也不读写用户历史文件。
        var memory = new SelectionHistory();
        memory.RecordSelection(before.Items[i].Candidate.Id, query, DateTimeOffset.Parse("2026-11-14T00:00:00Z"));
        var first = Collect(query, memory, 9).Items[0];
        if (first.Candidate.Id != before.Items[i].Candidate.Id || !rules.Classify(first).ProtectedEntry)
            throw new InvalidDataException("模拟降级损害了已学习的目标");
        return new { first.Candidate.Name, first.Candidate.Target, RankAfterOneMemorySelection = 1 };
    }).ToArray();
    var result = new
    {
        Query = query, before.TotalMatches, BoundedFirstNineEqual = true, RecallSetUnchanged = true,
        PathDemotions = decisions.Count(d => d.PathReason != null),
        AdditionalNameDemotions = decisions.Count(d => d.PathReason == null && d.NameReason != null),
        PathOnlyFirstNineUnchanged = a.Take(9).SequenceEqual(original.Take(9)),
        AllNamePatternsFirstNineUnchanged = b.Take(9).SequenceEqual(original.Take(9)),
        Before = Rows(original), PathOnly = Rows(a), AllNamePatterns = Rows(b), Targets = targets, MemoryLearningChecks = learned
    };
    Console.WriteLine($"{query}: {before.TotalMatches} 命中；目标 {string.Join(", ", targets.Select(t => $"{t.Name} {t.Before}->{t.PathOnly}/{t.AllNamePatterns}"))}");
    return result;

    object[] Rows(int[] order) => order.Take(9).Select((i, rank) => (object)new
    {
        Rank = rank + 1, OriginalRank = i + 1, before.Items[i].Candidate.Name,
        before.Items[i].Candidate.Target, before.Items[i].Candidate.Kind, before.Items[i].Group,
        Score = before.Items[i].Match.AlignmentScore, Decision = decisions[i]
    }).ToArray();
}

SearchResponse Collect(string query, SelectionHistory history, int limit)
{
    var collector = new SearchCollector(query, limit);
    var view = history.ForQuery(query);
    applications.SearchInto(collector, view);
    shortcuts.SearchInto(collector, view);
    var replaced = shortcuts.ReplacedIds(query);
    var represented = shortcuts.MatchedExecutableTargets(query);
    foreach (var index in indexes) index.SearchInto(collector, view, default, replaced, represented);
    return collector.Finish();
}

static int[] Invert(int[] order)
{
    var ranks = new int[order.Length];
    for (var i = 0; i < order.Length; i++) ranks[order[i]] = i + 1;
    return ranks;
}

static bool IsTarget(string query, SearchCandidate item) => query.ToLowerInvariant() switch
{
    "any" => item.Target.Equals(@"D:\Work\烧录固件\production_tools_pc-ANYconsole_V2.1.00\ANYconsole", StringComparison.OrdinalIgnoreCase),
    "800q" => item.Target.Equals(@"D:\Work\资料\EG800Q", StringComparison.OrdinalIgnoreCase),
    ".code" => item.Target.Equals(@"C:\Users\zsj\.codex", StringComparison.OrdinalIgnoreCase),
    "nd" => item.IsApplicationEntry && item.Name.Equals("Neat Download Manager", StringComparison.OrdinalIgnoreCase),
    "tra" => item.IsApplicationEntry && item.Name.Equals("Trae CN", StringComparison.OrdinalIgnoreCase),
    "chatgpt" => item.Name.Equals("ChatGPT", StringComparison.OrdinalIgnoreCase) && item.AppUserModelId != null,
    "rtt" => item.Target.Equals(@"C:\Users\zsj\.eide\tools\jlink\JLinkRTTViewer.exe", StringComparison.OrdinalIgnoreCase),
    "doubao" => item.IsApplicationEntry && item.Name is "豆包" or "豆包工作",
    "qianwen" => item.IsApplicationEntry && item.Name == "千问办公",
    _ => false
};

static string Hash(string path)
{
    using var stream = File.OpenRead(path);
    return Convert.ToHexStringLower(SHA256.HashData(stream));
}

sealed record DefaultTables(string BinarySha256, TableCopy[] Copies);
sealed record TableCopy(PathRule[] Paths, NameRule[] Patterns);
sealed record PathRule(int Priority, string Path);
sealed record NameRule(string Pattern, bool ApplyToFolders, bool ApplyToFiles);
sealed record Decision(string? PathReason, string? NameReason, bool ProtectedEntry);

sealed class ProposedRules
{
    public PathRule[] ExpandedPaths { get; }
    private readonly (NameRule Rule, Regex Matcher)[] _patterns;

    public ProposedRules(TableCopy table)
    {
        ExpandedPaths = table.Paths.Select(rule => rule with { Path = Environment.ExpandEnvironmentVariables(rule.Path).TrimEnd('\\') })
            .OrderByDescending(rule => rule.Path.Length).ToArray();
        if (ExpandedPaths.Any(rule => rule.Path.Contains('%'))) throw new InvalidDataException("环境变量未展开");
        _patterns = table.Patterns.Select(rule => (rule, new Regex(rule.Pattern, RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1)))).ToArray();
    }

    public Decision Classify(SearchHit hit)
    {
        var item = hit.Candidate;
        // 这是待确认的保护边界：当前已认可的应用入口、便携 EXE、查询历史不参与普通项降级。
        if (item.IsApplicationEntry || hit.History.ExactQuery != null || hit.History.PrefixQuery != null)
            return new(null, null, true);
        return Plain(item.Target, item.Kind == SearchItemKind.Directory);
    }

    private Decision Plain(string path, bool directory)
    {
        var pathRule = ExpandedPaths.FirstOrDefault(rule => Under(path, rule.Path));
        var pathReason = pathRule?.Priority == 2 ? "path: " + pathRule.Path : null;
        // 后代继承 node_modules 是本次模拟假设，尚未从原程序执行路径证实。
        if (path.Split('\\').Contains("node_modules", StringComparer.OrdinalIgnoreCase)) pathReason ??= "ancestor: node_modules";
        var basename = Path.GetFileName(path.TrimEnd('\\'));
        var pattern = _patterns.FirstOrDefault(pair => (directory ? pair.Rule.ApplyToFolders : pair.Rule.ApplyToFiles) && pair.Matcher.IsMatch(basename));
        return new(pathReason, pattern.Rule == null ? null : "name: " + pattern.Rule.Pattern, false);
    }

    private static bool Under(string path, string root) => path.Equals(root, StringComparison.OrdinalIgnoreCase) ||
        path.StartsWith(root + "\\", StringComparison.OrdinalIgnoreCase);

    public void CheckBoundaries()
    {
        var appData = Environment.ExpandEnvironmentVariables(@"%USERPROFILE%\AppData");
        var start = Environment.ExpandEnvironmentVariables(@"%ProgramData%\Microsoft\Windows\Start Menu");
        var checks = new[] {
            Plain(appData + @"\cache\any", true).PathReason != null,
            Plain(appData + @"2\any", true).PathReason == null,
            Plain(start + @"\Programs\example.lnk", false).PathReason == null,
            Plain(start.ToUpperInvariant() + @"\example.lnk", false).PathReason == null,
            Plain(@"D:\normal\ANYconsole", true).NameReason == null,
            Plain(@"D:\normal\README", false).NameReason == "name: ^[^.]+$",
            Plain(@"D:\normal\node_modules", true).NameReason == "name: ^node_modules$",
            Plain(@"D:\normal\node_modules.txt", false).NameReason == null,
            Plain(@"D:\normal\.codex", true).NameReason == @"name: ^\.",
            Plain(@"D:\normal\node_modules\any.txt", false).PathReason == "ancestor: node_modules"
        };
        if (checks.Any(result => !result)) throw new InvalidDataException("模拟分类边界检查失败");
    }
}
