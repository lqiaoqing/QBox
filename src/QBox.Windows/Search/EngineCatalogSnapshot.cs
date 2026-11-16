using System.Text.Json;
using QBox.Core.Search;
using QBox.Windows.Applications;
using QBox.Windows.Shortcuts;

namespace QBox.Windows.Search;

// 产品侧仍由 C# 负责 COM 应用目录与 ShellLink 读取；这里把最终条目交给 Rust 搜索引擎。
public sealed record EngineCandidateSnapshot(string Id, string Name, string Target, string Kind,
    string? Arguments, ulong NormalPriority, bool IsApplicationEntry, string? AppUserModelId,
    bool IsDirectExecutable);

public sealed record EngineShortcutSnapshot(EngineCandidateSnapshot Canonical,
    EngineCandidateSnapshot[] Aliases, string ActionTarget);

public sealed record EngineCatalogSnapshot(EngineCandidateSnapshot[] Applications,
    EngineShortcutSnapshot[] Shortcuts)
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static EngineCatalogSnapshot Create(PackagedApplicationCatalog applications, ShortcutCatalog shortcuts)
    {
        var apps = applications.ExportEntries().Select(FromCandidate).ToArray();
        var links = shortcuts.ExportEntries()
            .Select(entry => new EngineShortcutSnapshot(FromCandidate(entry.Canonical),
                entry.Aliases.Select(FromCandidate).ToArray(), entry.ActionTarget))
            .ToArray();
        return new(apps, links);
    }

    public static EngineCandidateSnapshot FromCandidate(SearchCandidate candidate) =>
        new(candidate.Id, candidate.Name, candidate.Target, candidate.Kind.ToString(), candidate.Arguments,
            candidate.NormalPriority, candidate.IsApplicationEntry, candidate.AppUserModelId, candidate.IsDirectExecutable);

    public byte[] Serialize() => JsonSerializer.SerializeToUtf8Bytes(this, Json);
}
