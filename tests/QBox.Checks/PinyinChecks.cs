using QBox.Core.History;
using QBox.Core.Matching;
using QBox.Core.Search;
using QBox.Windows.Applications;
using QBox.Windows.Shortcuts;
using static QBox.Checks.CheckSuite;

namespace QBox.Checks;

internal static class PinyinChecks
{
    public static void Run(CheckSuite checks)
    {
        var fuzzy = new FuzzyMatcher();
        foreach (var (query, length) in new[] { ("db", 2), ("douba", 2), ("dbaogz", 4), ("doubaogz", 4), ("DBGZ", 4) })
            checks.Run($"独立扩展支持首字母和半拼 {query}", () =>
            {
                var match = new PreparedApplicationName("豆包工作").Match(fuzzy.Prepare(query), extended: true);
                True(match.IsPhonetic); True(match.Supplemental);
                Equal(new TextRange(0, length), match.PhoneticMatch!.Highlights.Single());
            });
        checks.Run("首字母扩展不接受音节内部起点", () =>
            Equal(0, new PreparedApplicationName("豆包工作").Match(fuzzy.Prepare("oubao"), extended: true).Score));
        checks.Run("中文和型号数字混合仍可完整拼音匹配", () =>
            True(new PreparedApplicationName("豆包800Q").Match(fuzzy.Prepare("doubao800q"), extended: true).IsPhonetic));
        foreach (var (name, query, start, length) in new[] { ("豆包", "doubao", 0, 2), ("豆包工作", "DOUBAO", 0, 2),
            ("豆包工作", "doubaogongzuo", 0, 4), ("千问办公", "qianwen", 0, 2), ("阿里千问办公", "qianwen", 2, 2),
            ("AI豆包", "doubao", 2, 2), ("😀豆包", "doubao", 2, 2), ("重庆工具", "chongqing", 0, 2),
            ("重庆工具", "zhongqing", 0, 2), ("𠀀", "he", 0, 2), ("吕布", "lvbu", 0, 2) })
            checks.Run($"应用全拼及原名称高亮 {query}/{name}", () =>
            {
                var match = new PreparedApplicationName(name).Match(fuzzy.Prepare(query));
                True(match.IsPhonetic);
                Equal(1, match.Score);
                Equal(new TextRange(start, length), match.PhoneticMatch!.Highlights.Single());
            });
        foreach (var query in new[] { "db", "douba", "oubao", "Any", "800Q", ".code", "nd", "Tra", "chatgpt", "rtt" })
            checks.Run($"完整音节边界不引入额外缩写匹配 {query}", () =>
                Equal(0, new PreparedApplicationName("豆包工作").Match(fuzzy.Prepare(query)).Score));
        checks.Run("原有字面匹配分值和高亮入口保持不变", () =>
        {
            var name = new PreparedApplicationName("中文 Neat Download Manager");
            var expected = fuzzy.Prepare("nd").Score(name.Text);
            var actual = name.Match(fuzzy.Prepare("nd"));
            Equal(expected, actual.Score);
            True(!actual.IsPhonetic);
        });
        checks.Run("拼音匹配响应取消", () =>
            Throws<OperationCanceledException>(() => new PreparedApplicationName("豆包工作").Match(fuzzy.Prepare("doubao"), new(true))));
        checks.Run("读音表未覆盖的字符仍保留字面匹配", () =>
        {
            var name = new PreparedApplicationName("𠮷");
            Equal(0, name.Match(fuzzy.Prepare("ji")).Score);
            True(name.Match(fuzzy.Prepare("𠮷")).Score > 0);
        });

        var catalog = PackagedApplicationCatalog.Create([new("豆包", "Example.Tools_1234567890abc!Bao"),
            new("豆包工作", "Example.Tools_1234567890abc!Work"), new("Doubao Literal", "Example.Tools_1234567890abc!Latin"),
            new("千问办公", "Example.Tools_1234567890abc!Qianwen")]);
        SearchResponse Search(string query, SelectionHistory history, int limit = 9)
        {
            var collector = new SearchCollector(query, limit);
            catalog.SearchInto(collector, history.ForQuery(query));
            return collector.Finish();
        }
        checks.Run("拼音应用保留中文名称且位于既有有效字面应用之后", () =>
        {
            var result = Search("doubao", new());
            Equal(3, result.TotalMatches);
            Equal("Doubao Literal", result.Items[0].Candidate.Name);
            True(result.Items.Skip(1).All(hit => hit.Candidate.Name.StartsWith("豆包", StringComparison.Ordinal)));
            True(result.Items.All(hit => hit.Match.IsMatch));
            Equal(new TextRange(0, 2), result.Items[1].Match.Highlights.Single());
        });
        checks.Run("拼音选择学习仍使用原应用身份", () =>
        {
            var history = new SelectionHistory();
            var selected = Search("doubao", history).Items.Single(hit => hit.Candidate.Name == "豆包工作").Candidate;
            history.RecordSelection(selected.Id, "doubao");
            var result = Search("DOUBAO", history);
            Equal(selected.Id, result.Items[0].Candidate.Id);
            Equal(RankGroup.ExactQueryHistory, result.Items[0].Group);
        });
        checks.Run("拼音扩展展示条数不会截断总命中", () =>
        {
            Equal(3, Search("doubao", new(), 0).TotalMatches);
            Equal(3, Search("doubao", new(), 1).TotalMatches);
            Equal(1, Search("doubao", new(), 1).Items.Count);
        });
        checks.Run("新增缩写不挤掉原来字面首屏，学习后仍可置顶", () =>
        {
            var added = PackagedApplicationCatalog.Create([new("豆包", "Example.Tools_1234567890abc!Bao")]);
            var history = new SelectionHistory();
            SearchResponse Collect(int limit)
            {
                var collector = new SearchCollector("db", limit); var view = history.ForQuery("db");
                added.SearchInto(collector, view);
                for (var i = 0; i < 20; i++) collector.Add(new TestSource(), (ulong)i, $"d{i}b".AsMemory(), SearchItemKind.File, 0, default);
                return collector.Finish();
            }
            Equal(21, Collect(1).TotalMatches);
            True(Collect(9).Items.All(hit => hit.Candidate.Name != "豆包"));
            Equal("豆包", Collect(21).Items[^1].Candidate.Name);
            var selected = Collect(21).Items[^1].Candidate;
            history.RecordSelection(selected.Id, "db");
            Equal(selected.Id, Collect(1).Items.Single().Candidate.Id);
        });
        checks.Run("快捷方式拼音匹配仍归并动作并保留中文高亮", () =>
        {
            var root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "QBox-pinyin-shortcut-check"));
            SearchCandidate[] links = [new("first", "豆包工作", Path.Combine(root, "first.lnk"), SearchItemKind.Shortcut),
                new("alias", "Doubao Work", Path.Combine(root, "alias.lnk"), SearchItemKind.Shortcut)];
            var shortcuts = ShortcutCatalog.Create(links, [root], _ => new(@"C:\Apps\tool.exe", "", "", 1, 0));
            var collector = new SearchCollector("doubaogongzuo");
            shortcuts.SearchInto(collector, new SelectionHistory().ForQuery("doubaogongzuo"));
            var result = collector.Finish();
            Equal(1, result.TotalMatches);
            Equal("豆包工作", result.Items[0].Candidate.Name);
            Equal(new TextRange(0, 4), result.Items[0].Match.Highlights.Single());
            True(shortcuts.MatchedExecutableTargets("doubaogongzuo").Contains(@"C:\Apps\tool.exe"));
        });
    }

    private sealed class TestSource : IUniqueSearchSource
    {
        public SearchCandidate Materialize(ulong key, ReadOnlyMemory<char> name, SearchItemKind kind, ulong priority) => new("test:" + key, name.ToString(), "test", kind);
        public string Identity(ulong key) => "test:" + key;
        public int CompareIdentity(ulong key, IUniqueSearchSource other, ulong otherKey) => key.CompareTo(otherKey);
    }
}
