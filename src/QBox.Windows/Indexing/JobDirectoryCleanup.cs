namespace QBox.Windows.Indexing;

/// <summary>
/// 回收已退出或租约超时的后台索引任务工作目录，避免长期运行后在状态目录中累积孤立目录。
/// </summary>
public static class JobDirectoryCleanup
{
    public static void CleanupStaleDirectories(
        string jobsRoot,
        TimeSpan maxAge,
        string? excludeDirectory = null,
        DateTimeOffset? now = null)
    {
        if (!Directory.Exists(jobsRoot)) return;
        var currentUtc = (now ?? DateTimeOffset.UtcNow).UtcDateTime;
        var excluded = string.IsNullOrEmpty(excludeDirectory) ? null : Path.GetFullPath(excludeDirectory);
        IEnumerable<string> directories;
        try { directories = Directory.EnumerateDirectories(jobsRoot); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return; }

        foreach (var directory in directories)
        {
            if (excluded != null && string.Equals(Path.GetFullPath(directory), excluded, StringComparison.OrdinalIgnoreCase))
                continue;
            try
            {
                var latestWriteUtc = Directory.GetLastWriteTimeUtc(directory);
                foreach (var file in Directory.EnumerateFiles(directory))
                {
                    var fileWriteUtc = File.GetLastWriteTimeUtc(file);
                    if (fileWriteUtc > latestWriteUtc) latestWriteUtc = fileWriteUtc;
                }
                if (currentUtc - latestWriteUtc >= maxAge)
                    Directory.Delete(directory, recursive: true);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        }
    }

    public static void TryDeleteDirectory(string directory)
    {
        if (string.IsNullOrEmpty(directory) || !Directory.Exists(directory)) return;
        try { Directory.Delete(directory, recursive: true); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
    }
}
