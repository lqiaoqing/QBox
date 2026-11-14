using System.Diagnostics;
using System.Globalization;
using System.Runtime.Versioning;
using QBox.Core.Search;
using QBox.Windows.Ntfs;
using QBox.Windows.Applications;

namespace QBox.Windows;

[SupportedOSPlatform("windows")]
public static class ShellTargetLauncher
{
    public static bool OpenIndexed(SearchCandidate candidate, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (candidate.AppUserModelId != null) return PackagedApplicationActivation.Open(candidate, cancellationToken);
        var fields = candidate.Id.Split(':');
        if (fields.Length != 5 || fields[0] != "ntfs") throw new IOException("索引目标身份格式无效");
        var expected = new NtfsPathIdentity(ulong.Parse(fields[1], NumberStyles.HexNumber, CultureInfo.InvariantCulture),
            new(ulong.Parse(fields[2], NumberStyles.HexNumber, CultureInfo.InvariantCulture)));
        using var pinned = NativeFileIdentity.Pin(candidate.Target, expected);
        using var process = Process.Start(new ProcessStartInfo
        {
            FileName = candidate.Target,
            Arguments = candidate.Arguments ?? "",
            WorkingDirectory = candidate.Target.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? Path.GetDirectoryName(candidate.Target) ?? "" : "",
            UseShellExecute = true
        });
        return true;
    }
}
