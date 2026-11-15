using System.Runtime.InteropServices;

namespace QBox.Desktop;

// 窗口在托盘空闲 30 秒后收缩工作集：索引保持映射，下次搜索或增量再 fault 热页。
internal static class IdleWorkingSetReclaim
{
    public static readonly TimeSpan Delay = TimeSpan.FromSeconds(30);
    internal const string IdleFileName = "idle";
    internal const string ReclaimFileName = "reclaim";
    internal const string WarmupFileName = "warmup";

    public static void SetIdle(string? jobDirectory, bool idle)
    {
        if (string.IsNullOrEmpty(jobDirectory)) return;
        var idlePath = Path.Combine(jobDirectory, IdleFileName);
        var reclaimPath = Path.Combine(jobDirectory, ReclaimFileName);
        var warmupPath = Path.Combine(jobDirectory, WarmupFileName);
        try
        {
            if (idle)
            {
                File.WriteAllText(idlePath, "1");
                File.Delete(warmupPath);
            }
            else
            {
                File.Delete(idlePath);
                File.Delete(reclaimPath);
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
    }

    public static void RequestReclaim(string? jobDirectory)
    {
        if (string.IsNullOrEmpty(jobDirectory)) return;
        try { File.WriteAllText(Path.Combine(jobDirectory, ReclaimFileName), "1"); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
    }

    public static void RequestWarmup(string? jobDirectory)
    {
        if (string.IsNullOrEmpty(jobDirectory)) return;
        try { File.WriteAllText(Path.Combine(jobDirectory, WarmupFileName), "1"); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
    }

    public static bool TryConsumeReclaim(string jobDirectory)
    {
        var path = Path.Combine(jobDirectory, ReclaimFileName);
        if (!File.Exists(path)) return false;
        try { File.Delete(path); return true; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return false; }
    }

    public static bool TryConsumeWarmup(string jobDirectory)
    {
        var path = Path.Combine(jobDirectory, WarmupFileName);
        if (!File.Exists(path)) return false;
        try { File.Delete(path); return true; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return false; }
    }

    public static bool IsIdle(string jobDirectory) => File.Exists(Path.Combine(jobDirectory, IdleFileName));

    public static void TrimCurrentProcess()
    {
        try { EmptyWorkingSet(GetCurrentProcess()); }
        catch (Exception error) when (error is EntryPointNotFoundException or DllNotFoundException) { }
    }

    [DllImport("psapi.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EmptyWorkingSet(nint process);

    [DllImport("kernel32.dll")]
    private static extern nint GetCurrentProcess();
}
