using System.ComponentModel;

namespace QBox.Windows.Indexing;

public readonly record struct DriveProbeEntry(string Name, Func<bool> IsReady, Func<DriveType> DriveType, Func<string> DriveFormat);

public sealed record DriveProbeResult(
    HashSet<string> ReadyRoots,
    Dictionary<string, string> FailedRoots,
    string? EnumerationError = null);

/// <summary>
/// 逐盘隔离枚举固定 NTFS 磁盘，避免单盘瞬态故障抛出异常并拖垮整个后台索引进程。
/// </summary>
public static class FixedNtfsDriveProbe
{
    public static DriveProbeResult Probe(Func<IEnumerable<DriveProbeEntry>>? enumerateDrives = null)
    {
        var ready = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var failed = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        IEnumerable<DriveProbeEntry> entries;
        try
        {
            entries = enumerateDrives != null ? enumerateDrives() : EnumerateSystemDrives();
        }
        catch (Exception error) when (IsTransientDriveError(error))
        {
            return new DriveProbeResult(ready, failed, error.Message);
        }

        foreach (var entry in entries)
        {
            var root = entry.Name;
            try
            {
                if (!entry.IsReady()) continue;
                if (entry.DriveType() != DriveType.Fixed) continue;
                if (!string.Equals(entry.DriveFormat(), "NTFS", StringComparison.OrdinalIgnoreCase)) continue;
                ready.Add(root);
            }
            catch (Exception error) when (IsTransientDriveError(error))
            {
                failed[root] = error.Message;
            }
        }
        return new DriveProbeResult(ready, failed);
    }

    private static IEnumerable<DriveProbeEntry> EnumerateSystemDrives()
    {
        foreach (var drive in DriveInfo.GetDrives())
        {
            var d = drive;
            yield return new DriveProbeEntry(d.Name, () => d.IsReady, () => d.DriveType, () => d.DriveFormat);
        }
    }

    private static bool IsTransientDriveError(Exception error) =>
        error is IOException or UnauthorizedAccessException or Win32Exception or NotSupportedException;
}
