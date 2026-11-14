using QBox.Core.History;
using QBox.Core.Search;
using QBox.Windows.Shortcuts;
using static QBox.Checks.CheckSuite;

namespace QBox.Checks;

internal static class ShortcutChecks
{
    public static void Run(CheckSuite checks)
    {
        var root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "QBox-shortcut-catalog-check"));
        SearchCandidate Link(string id, string name, string? directory = null) => new(id, name,
            Path.Combine(directory ?? root, name + ".lnk"), SearchItemKind.Shortcut);
        var action = new ShortcutAction(@"C:\Apps\tool.exe", "--profile one", @"C:\Work", 1, 0x80);
        var canonical = Link("a", "Alpha Tool"); var alias = Link("b", "Beta Tool");
        var catalog = ShortcutCatalog.Create([canonical, alias], [root], _ => action);
        SearchResponse Search(string query, SelectionHistory? history = null)
        {
            var collector = new SearchCollector(query);
            catalog.SearchInto(collector, (history ?? new SelectionHistory()).ForQuery(query));
            return collector.Finish();
        }
        checks.Run("相同启动动作只出现一次，别名仍参与匹配", () =>
        {
            Equal(1, catalog.ActionCount); Equal(2, catalog.ShortcutCount);
            var beta = Search("beta");
            Equal(1, beta.TotalMatches); Equal("Beta Tool", beta.Items[0].Candidate.Name);
            Equal("a", beta.Items[0].Candidate.Id); Equal(canonical.Target, beta.Items[0].Candidate.Target);
            Equal("a", Search("alpha").Items[0].Candidate.Id);
            Equal(1, Search("tool").TotalMatches);
        });
        checks.Run("合并动作继承任一快捷方式的历史关联", () =>
        {
            var history = new SelectionHistory();
            history.RecordSelection("b", "beta", DateTimeOffset.FromUnixTimeSeconds(10));
            var hit = Search("beta", history).Items[0];
            Equal(RankGroup.ExactQueryHistory, hit.Group);
            Equal<DateTimeOffset?>(DateTimeOffset.FromUnixTimeSeconds(10), hit.History.ExactQuery);
        });
        foreach (var (label, different) in new[] { ("参数", action with { Arguments = "--profile two" }),
            ("工作目录", action with { WorkingDirectory = @"C:\Other" }), ("窗口状态", action with { ShowCommand = 3 }),
            ("管理员标志", action with { Flags = 0x2080 }), ("目标大小写", action with { Target = @"C:\Apps\Tool.exe" }) })
            checks.Run($"不同{label}不误合并", () =>
            {
                var result = ShortcutCatalog.Create([canonical, alias], [root], candidate => candidate.Id == "a" ? action : different);
                Equal(2, result.ActionCount);
            });
        checks.Run("未知链接和开始菜单外的副本保留原始文件入口", () =>
        {
            var otherRoot = root + "-backup";
            var unknown = Link("unknown", "Unknown"); var outside = Link("outside", "Outside", otherRoot);
            var result = ShortcutCatalog.Create([canonical, unknown, outside], [root], item => item.Id == "unknown" ? null : action);
            Equal(1, result.ActionCount);
            True(result.ReplacedIds("tool")!.Contains("a"));
            True(!result.ReplacedIds("tool")!.Contains("unknown"));
            True(!result.ReplacedIds("tool")!.Contains("outside"));
        });
        checks.Run("显式搜索 .lnk 时恢复全部原始快捷方式路径", () =>
        {
            Equal(0, Search(".LNK").TotalMatches);
            True(catalog.ReplacedIds(".lnk") == null);
        });
        checks.Run("元数据读取失败不会隐藏该快捷方式", () =>
        {
            var result = ShortcutCatalog.Create([canonical], [root], _ => throw new IOException("不可读"));
            Equal(0, result.ShortcutCount);
        });
        checks.Run("读取扩展名入口后移除 .lnk，避免扩展名字符制造应用命中", () =>
        {
            var result = ShortcutCatalog.Create([Link("link", "Alpha.lnk")], [root], _ => action);
            var collector = new SearchCollector("pl");
            result.SearchInto(collector, new SelectionHistory().ForQuery("pl"));
            Equal(0, collector.Finish().TotalMatches);
            Equal(0, result.ReplacedIds("pl")!.Count);
        });
        checks.Run("弱匹配快捷方式保留命中但不享有应用优先级", () =>
        {
            var result = ShortcutCatalog.Create([Link("weak", "Remote Desktop Connection")], [root], _ => action);
            var collector = new SearchCollector("rtt");
            result.SearchInto(collector, new SelectionHistory().ForQuery("rtt"));
            var hit = collector.Finish().Items.Single();
            Equal(1, hit.Match.AlignmentScore);
            Equal(RankGroup.Ordinary, hit.Group);
            True(!hit.Candidate.IsApplicationEntry);
        });
        checks.Run("只有匹配的快捷方式才抑制同目标 EXE 的重复提权", () =>
        {
            True(catalog.MatchedExecutableTargets("tool").Contains(action.Target));
            True(!catalog.MatchedExecutableTargets("unmatched").Contains(action.Target));
            Equal(0, catalog.MatchedExecutableTargets(".lnk").Count);
        });
    }
}
