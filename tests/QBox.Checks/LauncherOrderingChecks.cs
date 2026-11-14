using System.Globalization;
using System.Text.Json;
using QBox.Core.History;
using QBox.Core.Search;
using static QBox.Checks.CheckSuite;

namespace QBox.Checks;

internal static class LauncherOrderingChecks
{
    public static void Run(CheckSuite checks)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Fixtures", "launcher-ordering.json");
        var fixture = JsonSerializer.Deserialize<Fixture>(File.ReadAllText(path), new JsonSerializerOptions
            { PropertyNameCaseInsensitive = true })!;
        foreach (var (row, index) in fixture.Cases.Select((row, index) => (row, index)))
            checks.Run($"Launcher 原始排序指令 {index}", () =>
            {
                True(row.Completed);
                var left = row.Left; var right = row.Right;
                var compare = LauncherOrdering.CompareNative(row.Mode,
                    left.NormalTag, ulong.Parse(left.NormalKey, CultureInfo.InvariantCulture), left.HasScore, left.Score, left.Kind, left.LastUsed,
                    right.NormalTag, ulong.Parse(right.NormalKey, CultureInfo.InvariantCulture), right.HasScore, right.Score, right.Kind, right.LastUsed);
                Equal(row.First, compare > 0 ? 2 : 1);
            });

        checks.Run("强目录、混合前十、弱目录、混合尾部按 Launcher 桶次序合并", () =>
        {
            // aXb 对 ab 仅有有序弱匹配；用它验证弱目录不会压过前十个普通文件。
            var candidates = Enumerable.Range(0, 12).Select(i => new SearchCandidate($"file{i:D2}", "ab.txt", "unused", SearchItemKind.File))
                .Append(new("weak", "aXb", "unused", SearchItemKind.Directory))
                .Append(new("strong", "ab-dir", "unused", SearchItemKind.Directory)).ToArray();
            var result = new SearchEngine(new()).Search("ab", candidates);
            Equal("strong", result.Items[0].Candidate.Id);
            Equal("file09", result.Items[10].Candidate.Id);
            Equal("weak", result.Items[11].Candidate.Id);
            Equal("file10", result.Items[12].Candidate.Id);
        });
        checks.Run("相同查询历史、前缀历史、无历史应用的桶次序", () =>
        {
            var history = new SelectionHistory();
            var time = DateTimeOffset.FromUnixTimeSeconds(10);
            history.RecordSelection("exact", "ab", time);
            history.RecordSelection("prefix", "abcd", time.AddSeconds(1));
            SearchCandidate[] candidates = [new("exact", "aXb", "unused", SearchItemKind.File),
                new("prefix", "aXb", "unused", SearchItemKind.File),
                new("app", "ab", "unused", SearchItemKind.Application, IsApplicationEntry: true),
                new("dir", "ab", "unused", SearchItemKind.Directory)];
            Equal("exact,prefix,app,dir", string.Join(',', new SearchEngine(history).Search("ab", candidates).Items.Select(i => i.Candidate.Id)));
        });
        var mixed = Enumerable.Range(0, 30).Select(i => new SearchCandidate($"app{i}", "ab tool", "unused", SearchItemKind.Application,
                IsApplicationEntry: true)).Concat(Enumerable.Range(0, 90).Select(i => new SearchCandidate($"item{i}",
                i % 3 == 0 ? "ab-dir" : "aXb", "unused", i % 2 == 0 ? SearchItemKind.Directory : SearchItemKind.File))).ToArray();
        var learned = new SelectionHistory();
        learned.RecordSelection("item89", "ab", DateTimeOffset.FromUnixTimeSeconds(1));
        learned.RecordSelection("item88", "abcd", DateTimeOffset.FromUnixTimeSeconds(2));
        foreach (var limit in new[] { 0, 1, 9, 17, 50, 100 })
            checks.Run($"较早分桶填满后仍保留精确历史、完整计数及扩展结果 / {limit}", () =>
            {
                var expected = new SearchEngine(learned).Search("ab", mixed, limit);
                var collector = new SearchCollector("ab", limit);
                var source = new Source(mixed);
                for (var i = 0; i < mixed.Length; i++)
                    collector.Add(source, (ulong)i, mixed[i].Name.AsMemory(), mixed[i].Kind, 0,
                        learned.ForQuery("ab").Get(mixed[i].Id), mixed[i].IsApplicationEntry);
                var actual = collector.Finish();
                Equal(expected.TotalMatches, actual.TotalMatches);
                True(expected.Items.Select(hit => hit.Candidate).SequenceEqual(actual.Items.Select(hit => hit.Candidate)));
            });
        checks.Run("跨应用来源的晚到强匹配可以替换已满的早到结果", () =>
        {
            var entries = Enumerable.Range(0, 20).Select(index => new SearchCandidate($"early{index}", "Alpha Beta long tool " + index,
                "unused", SearchItemKind.Application, IsApplicationEntry: true))
                .Append(new("late", "ab", "unused", SearchItemKind.Application, IsApplicationEntry: true)).ToArray();
            var collector = new SearchCollector("ab", 1);
            var source = new Source(entries);
            for (var index = 0; index < entries.Length; index++)
                collector.Add(source, (ulong)index, entries[index].Name.AsMemory(), SearchItemKind.Application, 0, default, true);
            var result = collector.Finish();
            Equal(21, result.TotalMatches);
            Equal("late", result.Items[0].Candidate.Id);
            Equal("late", new SearchEngine(new()).Search("ab", entries, 1).Items[0].Candidate.Id);
        });
        checks.Run("已定义的应用入口优先于未注册 EXE，学习后可反转", () =>
        {
            SearchCandidate[] entries = [new("direct", "nd", "unused", SearchItemKind.Application,
                IsApplicationEntry: true, IsDirectExecutable: true), new("known", "Neat Download Manager", "unused",
                SearchItemKind.Application, IsApplicationEntry: true)];
            var history = new SelectionHistory();
            var collector = new SearchCollector("nd", 1);
            var source = new Source(entries);
            for (var index = 0; index < entries.Length; index++)
                collector.Add(source, (ulong)index, entries[index].Name.AsMemory(), entries[index].Kind, 0, default,
                    true, entries[index].IsDirectExecutable);
            Equal("known", collector.Finish().Items[0].Candidate.Id);
            Equal("known", new SearchEngine(history).Search("nd", entries).Items[0].Candidate.Id);
            history.RecordSelection("direct", "nd", DateTimeOffset.UtcNow);
            Equal("direct", new SearchEngine(history).Search("nd", entries).Items[0].Candidate.Id);
        });
    }
    private sealed record Fixture(Row[] Cases);
    private sealed record Row(string Mode, Value Left, Value Right, bool Completed, int First);
    private sealed record Value(bool NormalTag, string NormalKey, bool HasScore, int Score, byte Kind, long? LastUsed);
    private sealed class Source(SearchCandidate[] items) : IUniqueSearchSource
    {
        public SearchCandidate Materialize(ulong key, ReadOnlyMemory<char> name, SearchItemKind kind, ulong priority) => items[(int)key];
        public string Identity(ulong key) => items[(int)key].Id;
        public int CompareIdentity(ulong key, IUniqueSearchSource other, ulong otherKey) => StringComparer.Ordinal.Compare(Identity(key), other.Identity(otherKey));
    }
}
