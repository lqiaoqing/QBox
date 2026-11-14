using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Runtime.Versioning;
using QBox.Core.History;
using QBox.Core.Search;

namespace QBox.Windows.Applications;

public sealed record RegisteredApplication(string Name, string AppUserModelId);

public sealed class PackagedApplicationCatalog
{
    private readonly SearchCandidate[] _entries;
    public static PackagedApplicationCatalog Empty { get; } = new([]);
    public int Count => _entries.Length;
    public string? Warning { get; private init; }
    private PackagedApplicationCatalog(SearchCandidate[] entries) => _entries = entries;

    public static bool IsPackagedId(string? id) => !string.IsNullOrWhiteSpace(id) && id.Length <= 512 &&
        id.IndexOf('!') > 0 && id.LastIndexOf('!') == id.IndexOf('!') && !id.EndsWith('!') &&
        id.All(letter => char.IsAsciiLetterOrDigit(letter) || letter is '.' or '-' or '_' or '!');

    public static string Identity(string id)
    {
        if (!IsPackagedId(id)) throw new ArgumentException("打包应用身份无效", nameof(id));
        return "app:" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(id)));
    }
    public static string ParsingName(string id)
    {
        if (!IsPackagedId(id)) throw new ArgumentException("打包应用身份无效", nameof(id));
        return @"shell:AppsFolder\" + id;
    }

    public static PackagedApplicationCatalog Create(IEnumerable<RegisteredApplication> applications) => new(applications
        .Where(application => !string.IsNullOrWhiteSpace(application.Name) && IsPackagedId(application.AppUserModelId))
        .Select(application => new SearchCandidate(Identity(application.AppUserModelId), application.Name,
            ParsingName(application.AppUserModelId), SearchItemKind.Application, IsApplicationEntry: true, AppUserModelId: application.AppUserModelId))
        .DistinctBy(candidate => (candidate.Id, candidate.Name)).OrderBy(candidate => candidate.Id, StringComparer.Ordinal)
        .ThenBy(candidate => candidate.Name, StringComparer.Ordinal).ToArray());

    public static PackagedApplicationCatalog Load(CancellationToken cancellationToken = default)
    {
        if (!OperatingSystem.IsWindows()) return Empty;
        try
        {
            return ShellApartment.Run(() =>
            {
                if (!OperatingSystem.IsWindows()) return Empty;
                cancellationToken.ThrowIfCancellationRequested();
                object? shell = null, folder = null, items = null;
                try
                {
                    // 使用系统应用注册的显示名与 AUMID，不依赖 WindowsApps 中可能变化的安装路径。
                    shell = Activator.CreateInstance(Type.GetTypeFromCLSID(new("13709620-C279-11CE-A49E-444553540000"), true)!)!;
                    folder = ((dynamic)shell).NameSpace("shell:AppsFolder");
                    if (folder == null) throw new IOException("系统应用目录不可用");
                    items = ((dynamic)folder).Items();
                    var applications = new List<RegisteredApplication>();
                    var skipped = 0;
                    var count = (int)((dynamic)items).Count;
                    for (var index = 0; index < count; index++)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        object? item = null;
                        try
                        {
                            item = ((dynamic)items).Item(index);
                            var name = (string?)((dynamic)item).Name;
                            var id = (string?)((dynamic)item).Path;
                            if (!string.IsNullOrWhiteSpace(name) && IsPackagedId(id)) applications.Add(new(name, id!));
                        }
                        catch (COMException) { skipped++; }
                        finally { Release(item); }
                    }
                    var result = Create(applications);
                    return skipped == 0 ? result : new(result._entries) { Warning = $"有 {skipped} 个系统应用入口读取失败，可重新载入重试。" };
                }
                finally { Release(items); Release(folder); Release(shell); }
            });
        }
        catch (Exception error) when (error is COMException or IOException or UnauthorizedAccessException)
        {
            return new([]) { Warning = "系统应用列表读取失败：" + error.Message };
        }
    }

    [SupportedOSPlatform("windows")]
    private static void Release(object? value)
    {
        if (value != null && Marshal.IsComObject(value)) Marshal.FinalReleaseComObject(value);
    }

    public SearchCandidate? Find(string id) => _entries.FirstOrDefault(candidate => candidate.Id == id);

    public void SearchInto(SearchCollector collector, HistoryView history, CancellationToken cancellationToken = default)
    {
        // 同一 AUMID 可以有本地化名称别名；先择最佳匹配名称，再提交唯一动作。
        var matches = _entries.Select(candidate => (Candidate: candidate, Score: collector.Query.Score(candidate.Name, cancellationToken)))
            .Where(hit => hit.Score > 0).GroupBy(hit => hit.Candidate.Id, StringComparer.Ordinal)
            .Select(group => group.OrderByDescending(hit => hit.Score).First())
            .OrderByDescending(hit => hit.Score).ThenBy(hit => hit.Candidate.Id, StringComparer.Ordinal).ToArray();
        var source = new Source(matches.Select(hit => hit.Candidate).ToArray());
        for (var index = 0; index < matches.Length; index++)
        {
            var match = matches[index];
            collector.AddMatched(source, (ulong)index, match.Candidate.Name.AsMemory(), SearchItemKind.Application, 0,
                history.Get(match.Candidate.Id), match.Score > 1);
        }
    }
    private sealed class Source(SearchCandidate[] entries) : IUniqueSearchSource
    {
        public SearchCandidate Materialize(ulong key, ReadOnlyMemory<char> name, SearchItemKind kind, ulong priority) => entries[(int)key];
        public string Identity(ulong key) => entries[(int)key].Id;
        public int CompareIdentity(ulong key, IUniqueSearchSource other, ulong otherKey) => StringComparer.Ordinal.Compare(Identity(key), other.Identity(otherKey));
    }
}
