using System.Text;
using QBox.Core.History;
using QBox.Core.Matching;
using QBox.Core.Search;
using static QBox.Checks.CheckSuite;

namespace QBox.Checks;

internal static class CoreChecks
{
    private static readonly DateTimeOffset Epoch = DateTimeOffset.FromUnixTimeMilliseconds(1767225600000);
    private static SearchCandidate Item(string id, string name) => new(id, name, "test-target", SearchItemKind.File);

    public static async Task Run(CheckSuite checks)
    {
        var matcher = new FuzzyMatcher();
        checks.Run("历史改名迁移合并同查询时间且不改最近查询", () =>
        {
            var history = new SelectionHistory();
            history.RecordSelection("old", "name", Epoch);
            history.RecordSelection("new", "name", Epoch.AddSeconds(1));
            var recent = history.Snapshot.RecentQueries;
            Equal(1, history.ReassignTargets(new Dictionary<string, string> { ["old"] = "new" }));
            Equal(1, history.Snapshot.Targets.Length);
            Equal<DateTimeOffset?>(Epoch.AddSeconds(1), history.ForQuery("name").Get("new").ExactQuery);
            True(recent.SequenceEqual(history.Snapshot.RecentQueries));
        });
        checks.Run("新输入取消旧请求并拒绝旧结果", () =>
        {
            using var gate = new LatestQueryGate();
            using var first = gate.Begin();
            using var second = gate.Begin();
            True(first.Token.IsCancellationRequested);
            True(!gate.IsCurrent(first));
            True(gate.IsCurrent(second));
            first.Dispose();
            True(gate.IsCurrent(second));
        });
        checks.Run("已完成请求仍可校验版本，下一输入使其失效", () =>
        {
            using var gate = new LatestQueryGate();
            var completed = gate.Begin();
            completed.Dispose();
            True(gate.IsCurrent(completed));
            using var next = gate.Begin();
            True(!gate.IsCurrent(completed));
            True(gate.IsCurrent(next));
        });
        checks.Run("窗口退出取消正在运行的查询", () =>
        {
            var gate = new LatestQueryGate();
            using var request = gate.Begin();
            gate.Dispose();
            True(request.Token.IsCancellationRequested);
            True(!gate.IsCurrent(request));
            Throws<ObjectDisposedException>(() => gate.Begin());
        });
        checks.Run("空查询和空候选", () =>
        {
            True(matcher.Match("", "").IsMatch);
            True(matcher.Match("", "目录").IsMatch);
            True(!matcher.Match("x", "").IsMatch);
        });
        checks.Run("点号参与匹配", () =>
        {
            True(matcher.Match(".CODE", ".codex").IsMatch);
            True(!matcher.Match(".code", "codex").IsMatch);
        });
        checks.Run("长名称高亮不截断", () =>
        {
            var result = matcher.Match("ab", new string('x', 300) + "ab");
            True(result.IsMatch);
            Equal(300, result.Highlights[0].Start);
            Equal(2, result.Highlights[0].Length);
        });
        checks.Run("查询取消", () =>
            Throws<OperationCanceledException>(() => matcher.Match("x", "xyz", new(true))));
        checks.Run("快速打分取消", () =>
            Throws<OperationCanceledException>(() => matcher.Prepare("x").Score("xyz", new(true))));

        var random = new Random(0x51424f58);
        var alphabet = new[] { "a", "B", "c", "0", " ", ".", "_", "目", "录", "资", "料", "😀", "𠮷" };
        for (var test = 0; test < 300; test++)
        {
            var letters = Enumerable.Range(0, random.Next(1, 35)).Select(_ => alphabet[random.Next(alphabet.Length)]).ToArray();
            var text = string.Concat(letters);
            var positions = Enumerable.Range(0, letters.Length).OrderBy(_ => random.Next()).Take(random.Next(1, Math.Min(5, letters.Length) + 1)).Order().ToArray();
            var query = string.Concat(positions.Select(i => letters[i])).ToLowerInvariant();
            checks.Run($"Unicode 有序匹配及高亮 {test}", () =>
            {
                var result = matcher.Match(query, text);
                True(result.IsMatch);
                Equal(result.AlignmentScore, matcher.Prepare(query).Score(text));
                True(result.AlignmentScore <= matcher.Prepare(query).ScoreUpperBound(text));
                var highlighted = string.Concat(result.Highlights.Select(r => text.Substring(r.Start, r.Length)));
                Equal(query.ToUpperInvariant(), highlighted.ToUpperInvariant());
                var last = 0;
                foreach (var range in result.Highlights)
                {
                    True(range.Start >= last && range.Length > 0 && range.Start + range.Length <= text.Length);
                    last = range.Start + range.Length;
                }
            });
        }

        checks.Run("前缀方向及精确历史优先", () =>
        {
            var history = new SelectionHistory();
            history.RecordSelection("app", "ND", Epoch);
            history.RecordSelection("app", "NDM", Epoch.AddSeconds(10));
            var exact = history.ForQuery("nd").Get("app");
            Equal<DateTimeOffset?>(Epoch, exact.ExactQuery);
            Equal<DateTimeOffset?>(null, exact.PrefixQuery);
            Equal<DateTimeOffset?>(Epoch.AddSeconds(10), history.ForQuery("n").Get("app").PrefixQuery);
            Equal<DateTimeOffset?>(null, history.ForQuery("ndmx").Get("app").PrefixQuery);
            Equal<DateTimeOffset?>(null, history.ForQuery("").Get("app").PrefixQuery);
        });
        checks.Run("每目标八条且重复查询去重", () =>
        {
            var history = new SelectionHistory();
            for (var i = 0; i < 10; i++) history.RecordSelection("id", $"q{i}", Epoch.AddSeconds(i));
            Equal(8, history.Snapshot.Targets[0].Queries.Length);
            Equal<DateTimeOffset?>(null, history.ForQuery("q0").Get("id").ExactQuery);
            history.RecordSelection("id", "Q4", Epoch.AddSeconds(20));
            Equal(8, history.Snapshot.Targets[0].Queries.Length);
            Equal("q4", history.Snapshot.Targets[0].Queries[0].Query);
        });
        checks.Run("全局最近查询五百条", () =>
        {
            var history = new SelectionHistory();
            for (var i = 0; i < 505; i++) history.RecordSelection("id", $"q{i}", Epoch.AddSeconds(i));
            Equal(500, history.Snapshot.RecentQueries.Length);
            Equal("q504", history.Snapshot.RecentQueries[0].Query);
            True(history.Snapshot.RecentQueries.All(q => q.Query != "q0"));
        });
        checks.Run("空查询仅记录目标使用", () =>
        {
            var history = new SelectionHistory();
            history.RecordSelection("id", " ", Epoch);
            Equal(0, history.Snapshot.RecentQueries.Length);
            Equal(0, history.Snapshot.Targets[0].Queries.Length);
            Equal<DateTimeOffset?>(Epoch, history.ForQuery("").Get("id").LastUsed);
        });
        checks.Run("旧时间不覆盖较新目标记录", () =>
        {
            var history = new SelectionHistory();
            history.RecordSelection("id", "test", Epoch.AddDays(1));
            history.RecordSelection("id", "test", Epoch);
            Equal<DateTimeOffset?>(Epoch.AddDays(1), history.ForQuery("test").Get("id").ExactQuery);
            Equal<DateTimeOffset?>(Epoch.AddDays(1), history.ForQuery("test").Get("id").LastUsed);
        });
        checks.Run("查询快照不随下一次选择改变", () =>
        {
            var history = new SelectionHistory();
            var view = history.ForQuery("nd");
            history.RecordSelection("id", "nd", Epoch);
            Equal<DateTimeOffset?>(null, view.Get("id").ExactQuery);
        });
        checks.Run("历史分组先于匹配分数和访问新旧", () =>
        {
            var history = new SelectionHistory();
            history.RecordSelection("exact", "ab", Epoch);
            history.RecordSelection("prefix", "abcd", Epoch.AddDays(1));
            var engine = new SearchEngine(history);
            var response = engine.Search("ab", [Item("ordinary", "ab"), Item("prefix", "a b"), Item("exact", "aXXXXb")]);
            True(response.Items.Select(i => i.Candidate.Id).SequenceEqual(["exact", "prefix", "ordinary"]));
        });
        checks.Run("不命中项不能靠历史进入结果", () =>
        {
            var history = new SelectionHistory();
            history.RecordSelection("wrong", "ab", Epoch);
            var engine = new SearchEngine(history);
            Equal(0, engine.Search("ab", [Item("wrong", "xyz")]).TotalMatches);
        });
        checks.Run("同名不同目标均保留，别名匹配后去重", () =>
        {
            var engine = new SearchEngine(new());
            var aliases = new[] { Item("one", "Other"), Item("one", "Tool"), Item("two", "Tool") };
            var response = engine.Search("tool", aliases, 1);
            Equal(2, response.TotalMatches);
            Equal(1, response.Items.Count);
            Equal(2, engine.Search("tool", aliases, 0).TotalMatches);
            // 原始比较指令在键和时间相同时不交换条目，保留各来源提供的顺序。
            Equal("one,two", string.Join(',', engine.Search("tool", aliases, 10).Items.Select(x => x.Candidate.Id)));
            Equal("two,one", string.Join(',', engine.Search("tool", aliases.Reverse(), 10).Items.Select(x => x.Candidate.Id)));
        });
        checks.Run("历史桶不参与别名择优，仍展示匹配最好的别名", () =>
        {
            var history = new SelectionHistory();
            history.RecordSelection("one", "tool", Epoch);
            var result = new SearchEngine(history).Search("tool", [Item("one", "tXoXoXl"), Item("one", "Tool")]);
            Equal("Tool", result.Items[0].Candidate.Name);
            Equal(RankGroup.ExactQueryHistory, result.Items[0].Group);
        });

        var scratch = Path.Combine(Path.GetTempPath(), "QBox-Checks-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(scratch);
        try
        {
            checks.Run("历史原子保存与重启读取", () =>
            {
                var path = Path.Combine(scratch, "roundtrip", "history.json");
                var history = new SelectionHistory(new FileHistoryPersistence(path));
                True(!Directory.Exists(Path.GetDirectoryName(path)));
                history.RecordSelection("id", "目录 ND", Epoch);
                var loaded = new SelectionHistory(new FileHistoryPersistence(path));
                Equal<DateTimeOffset?>(Epoch, loaded.ForQuery("目录 nd").Get("id").ExactQuery);
                True(!Directory.EnumerateFiles(Path.GetDirectoryName(path)!, "*.tmp-*").Any());
            });
            checks.Run("损坏历史保持原文件", () =>
            {
                var path = Path.Combine(scratch, "corrupt.json");
                File.WriteAllText(path, "{broken");
                Throws<InvalidDataException>(() => new SelectionHistory(new FileHistoryPersistence(path)));
                Equal("{broken", File.ReadAllText(path));
            });
            checks.Run("未知格式版本拒绝覆盖", () =>
            {
                var path = Path.Combine(scratch, "future.json");
                const string json = "{\"version\":99,\"targets\":[],\"recentQueries\":[]}";
                File.WriteAllText(path, json);
                Throws<InvalidDataException>(() => new SelectionHistory(new FileHistoryPersistence(path)));
                Equal(json, File.ReadAllText(path));
            });
            checks.Run("并发版本冲突重新合并", () =>
            {
                var path = Path.Combine(scratch, "shared.json");
                var first = new SelectionHistory(new FileHistoryPersistence(path));
                var second = new SelectionHistory(new FileHistoryPersistence(path));
                first.RecordSelection("first", "one", Epoch);
                second.RecordSelection("second", "two", Epoch.AddSeconds(1));
                var loaded = new SelectionHistory(new FileHistoryPersistence(path));
                Equal(2, loaded.Snapshot.Targets.Length);
                Equal<DateTimeOffset?>(Epoch, loaded.ForQuery("one").Get("first").ExactQuery);
            });
            await checks.RunAsync("选择、重启与再次查询完整链路", async () =>
            {
                var path = Path.Combine(scratch, "learning.json");
                var history = new SelectionHistory(new FileHistoryPersistence(path));
                var selected = Item("chosen", "AlphaBeta");
                var catalog = new[] { Item("short", "ab"), selected };
                Equal("short", new SearchEngine(history).Search("ab", catalog).Items[0].Candidate.Id);
                await new SearchActions(history).ActivateAsync(selected, "ab", (_, _) => Task.FromResult(true));
                var reloaded = new SelectionHistory(new FileHistoryPersistence(path));
                var response = new SearchEngine(reloaded).Search("AB", catalog);
                Equal("chosen", response.Items[0].Candidate.Id);
                Equal(RankGroup.ExactQueryHistory, response.Items[0].Group);
                Equal(RankGroup.PrefixQueryHistory, new SearchEngine(reloaded).Search("a", catalog).Items[0].Group);
            });
        }
        finally
        {
            // 仅删除本次验证创建、经完整路径校验的临时目录。
            var resolved = Path.GetFullPath(scratch);
            var tempRoot = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (!resolved.StartsWith(tempRoot, StringComparison.OrdinalIgnoreCase) ||
                !Path.GetFileName(resolved).StartsWith("QBox-Checks-", StringComparison.Ordinal))
                throw new InvalidOperationException("临时验证目录不在预期范围");
            Directory.Delete(resolved, recursive: true);
        }

        await checks.RunAsync("启动失败不学习，成功后才学习", async () =>
        {
            var history = new SelectionHistory();
            var actions = new SearchActions(history);
            var candidate = Item("id", "Tool");
            var failed = await actions.ActivateAsync(candidate, "tool", (_, _) => Task.FromResult(false));
            True(!failed.Opened);
            Equal(0, history.Snapshot.Targets.Length);
            var succeeded = await actions.ActivateAsync(candidate, "tool", (_, _) => Task.FromResult(true));
            True(succeeded.Opened);
            True(history.ForQuery("tool").Get("id").ExactQuery != null);
        });
        await checks.RunAsync("学习保存失败不误报打开失败", async () =>
        {
            var history = new SelectionHistory(new FailingPersistence());
            var result = await new SearchActions(history).ActivateAsync(Item("id", "Tool"), "tool", (_, _) => Task.FromResult(true));
            True(result.Opened && result.HistoryError != null);
            Equal(0, history.Snapshot.Targets.Length);
        });
    }

    private sealed class FailingPersistence : IHistoryPersistence
    {
        public HistorySnapshot Read() => HistorySnapshot.Empty;
        public void Write(HistorySnapshot snapshot) => throw new IOException("模拟磁盘写入失败");
    }
}
