using QBox.Core.History;
using QBox.Core.Search;
using QBox.Windows.Applications;
using static QBox.Checks.CheckSuite;

namespace QBox.Checks;

internal static class ApplicationChecks
{
    public static async Task Run(CheckSuite checks)
    {
        const string appId = "Example.Tools_1234567890abc!Main";
        var catalog = PackagedApplicationCatalog.Create([new("Atlas Notes", appId), new("Notes Atlas", appId),
            new("Another Atlas", "Example.Tools_1234567890abc!Other"), new("Unregistered", "Win32-Desktop-Id")]);
        checks.Run("注册应用以 AUMID 保持身份，名称变化不改变 ID", () =>
        {
            var renamed = PackagedApplicationCatalog.Create([new("Renamed Tool", appId)]);
            Equal(PackagedApplicationCatalog.Identity(appId), renamed.Find(PackagedApplicationCatalog.Identity(appId))!.Id);
            Equal(3, catalog.Count);
            var result = Search(catalog, "atlas");
            Equal(2, result.TotalMatches);
            var hit = result.Items.Single(item => item.Candidate.AppUserModelId == appId);
            Equal(PackagedApplicationCatalog.ParsingName(appId), hit.Candidate.Target);
            Equal(SearchItemKind.Application, hit.Candidate.Kind);
        });
        checks.Run("注册应用的别名与大小写均可匹配", () =>
        {
            var result = Search(catalog, "NOTES AT");
            Equal(1, result.TotalMatches);
            Equal("Notes Atlas", result.Items[0].Candidate.Name);
        });
        foreach (var invalid in new[] { "", "NoPackage", "Family!", "!App", "Family!App!Extra", "Family!App\\child", "Family!App\n", "Family!\ud800" })
            checks.Run("拒绝无效应用身份 " + invalid.Length + "/" + Convert.ToHexString(System.Text.Encoding.Unicode.GetBytes(invalid)), () =>
                True(!PackagedApplicationCatalog.IsPackagedId(invalid)));
        checks.Run("注册入口与 ID、参数和目标必须一致", () =>
        {
            var candidate = catalog.Find(PackagedApplicationCatalog.Identity(appId))!;
            PackagedApplicationActivation.Validate(candidate);
            Throws<IOException>(() => PackagedApplicationActivation.Validate(candidate with { Id = "another" }));
            Throws<IOException>(() => PackagedApplicationActivation.Validate(candidate with { Target = @"C:\other.exe" }));
            Throws<IOException>(() => PackagedApplicationActivation.Validate(candidate with { Arguments = "extra" }));
        });
        await checks.RunAsync("注册应用打开失败不学习，提交成功才记录稳定身份", async () =>
        {
            var history = new SelectionHistory();
            var candidate = catalog.Find(PackagedApplicationCatalog.Identity(appId))!;
            var actions = new SearchActions(history);
            var failed = await actions.ActivateAsync(candidate, "atlas", (_, _) => throw new IOException("注册入口已卸载"));
            True(!failed.Opened); Equal(0, history.Snapshot.Targets.Length);
            var success = await actions.ActivateAsync(candidate, "atlas", (_, _) => Task.FromResult(true));
            True(success.Opened);
            Equal(candidate.Id, Search(catalog, "ATLAS", history).Items[0].Candidate.Id);
        });
    }
    private static SearchResponse Search(PackagedApplicationCatalog catalog, string query, SelectionHistory? history = null)
    {
        var collector = new SearchCollector(query);
        catalog.SearchInto(collector, (history ?? new()).ForQuery(query));
        return collector.Finish();
    }
}
