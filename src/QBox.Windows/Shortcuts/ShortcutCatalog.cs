using System.Runtime.InteropServices;
using System.ComponentModel;
using QBox.Core.History;
using QBox.Core.Search;
using QBox.Core.Matching;
using QBox.Windows.Indexing;

namespace QBox.Windows.Shortcuts;

public sealed class ShortcutCatalog
{
    private sealed record Entry(SearchCandidate Canonical, SearchCandidate[] Aliases, string ExecutableTarget);
    private readonly Entry[] _entries;
    private readonly HashSet<string> _replaced;
    public static ShortcutCatalog Empty { get; } = new([], []);
    public int ActionCount => _entries.Length;
    public int ShortcutCount => _replaced.Count;
    private ShortcutCatalog(Entry[] entries, HashSet<string> replaced) { _entries = entries; _replaced = replaced; }

    public static ShortcutCatalog Load(VolumeIndex[] indexes, CancellationToken cancellationToken = default)
    {
        if (!OperatingSystem.IsWindows()) return Empty;
        var completed = new TaskCompletionSource<ShortcutCatalog>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                var roots = new[] { Environment.SpecialFolder.StartMenu, Environment.SpecialFolder.CommonStartMenu,
                    Environment.SpecialFolder.DesktopDirectory, Environment.SpecialFolder.CommonDesktopDirectory }
                    .Select(Environment.GetFolderPath).Where(path => path.Length != 0).ToArray();
                completed.SetResult(Create(indexes.SelectMany(index => index.Candidates(".lnk", cancellationToken)), roots,
                    candidate => OperatingSystem.IsWindows() ? ShellLinkReader.Read(candidate) : null, cancellationToken));
            }
            catch (Exception error) { completed.SetException(error); }
        }) { IsBackground = true, Name = "QBox shortcut metadata" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return completed.Task.GetAwaiter().GetResult();
    }

    public static ShortcutCatalog Create(IEnumerable<SearchCandidate> shortcuts, IReadOnlyList<string> roots,
        Func<SearchCandidate, ShortcutAction?> read, CancellationToken cancellationToken = default)
    {
        var groups = new Dictionary<ShortcutAction, List<SearchCandidate>>();
        var folders = roots.Select(path => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path)) + Path.DirectorySeparatorChar).ToArray();
        foreach (var candidate in shortcuts)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (candidate.Kind != SearchItemKind.Shortcut || !folders.Any(root => candidate.Target.StartsWith(root, StringComparison.OrdinalIgnoreCase))) continue;
            ShortcutAction? action;
            try { action = read(candidate); }
            catch (Exception error) when (error is COMException or IOException or UnauthorizedAccessException or Win32Exception or ArgumentException) { continue; }
            if (action == null || string.IsNullOrWhiteSpace(action.Target)) continue;
            // 字段按原值比较，含不同参数、工作目录、窗口状态或任何链接标志时都保留为独立动作。
            if (!groups.TryGetValue(action, out var aliases)) groups[action] = aliases = [];
            aliases.Add(candidate with { Name = candidate.Name.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase) ? candidate.Name[..^4] : candidate.Name });
        }
        var entries = groups.Select(group =>
        {
            // 固定按来源顺序选主项，新增别名不会因本次查询的匹配分数改变学习身份。
            var ordered = group.Value.OrderBy(candidate => Array.FindIndex(folders, root => candidate.Target.StartsWith(root, StringComparison.OrdinalIgnoreCase)))
                .ThenBy(candidate => candidate.Target, StringComparer.Ordinal).ToArray();
            return new Entry(ordered[0] with { Kind = SearchItemKind.Application, IsApplicationEntry = true }, ordered, group.Key.Target);
        }).OrderBy(entry => entry.Canonical.Id, StringComparer.Ordinal).ToArray();
        return new(entries, entries.SelectMany(entry => entry.Aliases).Select(candidate => candidate.Id).ToHashSet(StringComparer.Ordinal));
    }

    public void SearchInto(SearchCollector collector, HistoryView history, CancellationToken cancellationToken = default)
    {
        if (IsLiteralLinkQuery(collector.Query.Text)) return;
        var source = new CatalogSource(_entries);
        var matches = new List<(int Index, string Name, int Score, HistorySignals History)>();
        for (var index = 0; index < _entries.Length; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var entry = _entries[index];
            SearchCandidate? best = null;
            var score = 0;
            var signals = default(HistorySignals);
            foreach (var alias in entry.Aliases)
            {
                var usage = history.Get(alias.Id);
                signals = new(Latest(signals.ExactQuery, usage.ExactQuery), Latest(signals.PrefixQuery, usage.PrefixQuery), Latest(signals.LastUsed, usage.LastUsed));
                var value = collector.Query.Score(alias.Name, cancellationToken);
                if (value > score || value != 0 && value == score && StringComparer.OrdinalIgnoreCase.Compare(alias.Name, best?.Name) < 0)
                { best = alias; score = value; }
            }
            if (best != null) matches.Add((index, best.Name, score, signals));
        }
        // Launcher 同时间条目保持来源顺序；QBox 的快捷方式来源先按最佳别名分数排列。
        foreach (var match in matches.OrderByDescending(match => match.Score).ThenBy(match => match.Index))
            collector.AddMatched(source, (ulong)match.Index, match.Name.AsMemory(), SearchItemKind.Application, 0, match.History, match.Score > 1);
    }
    public ISet<string>? ReplacedIds(string query)
    {
        if (IsLiteralLinkQuery(query)) return null;
        var prepared = new FuzzyMatcher().Prepare(query);
        // 仅用已命中的动作替换原始入口；只匹配 .lnk 扩展名片段的查询仍能找到原文件。
        return _entries.Where(entry => entry.Aliases.Any(alias => prepared.CanMatch(alias.Name)))
            .SelectMany(entry => entry.Aliases).Select(alias => alias.Id).ToHashSet(StringComparer.Ordinal);
    }
    public IReadOnlySet<string> MatchedExecutableTargets(string query)
    {
        if (IsLiteralLinkQuery(query)) return new HashSet<string>();
        var prepared = new FuzzyMatcher().Prepare(query);
        // 仅当快捷方式本身提供了强匹配入口时，才将同目标的直接 EXE 留在普通文件区。
        return _entries.Where(entry => entry.ExecutableTarget.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) &&
                entry.Aliases.Any(alias => prepared.Score(alias.Name) > 1))
            .Select(entry => entry.ExecutableTarget).ToHashSet(StringComparer.OrdinalIgnoreCase);
    }
    private static bool IsLiteralLinkQuery(string query) => query.Contains(".lnk", StringComparison.OrdinalIgnoreCase);
    private static DateTimeOffset? Latest(DateTimeOffset? left, DateTimeOffset? right) => left == null || right > left ? right : left;

    private sealed class CatalogSource(Entry[] entries) : IUniqueSearchSource
    {
        public SearchCandidate Materialize(ulong key, ReadOnlyMemory<char> name, SearchItemKind kind, ulong priority) =>
            entries[checked((int)key)].Canonical with { Name = name.ToString() };
        public string Identity(ulong key) => entries[checked((int)key)].Canonical.Id;
        public int CompareIdentity(ulong key, IUniqueSearchSource other, ulong otherKey) => StringComparer.Ordinal.Compare(Identity(key), other.Identity(otherKey));
    }
}
