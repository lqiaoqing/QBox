using System.ComponentModel;
using QBox.Windows.Indexing;

namespace QBox.Checks;

internal static class LiveWorkerReliabilityChecks
{
    public static void Run(CheckSuite checks)
    {
        checks.Run("单盘探测异常隔离，不影响其余固定 NTFS 磁盘", () =>
        {
            var entries = new[]
            {
                new DriveProbeEntry(@"C:\", () => true, () => DriveType.Fixed, () => "NTFS"),
                new DriveProbeEntry(@"D:\", () => true, () => DriveType.Fixed, () => throw new IOException("设备未就绪")),
                new DriveProbeEntry(@"E:\", () => true, () => DriveType.Fixed, () => "exFAT"),
                new DriveProbeEntry(@"F:\", () => false, () => DriveType.Fixed, () => "NTFS"),
                new DriveProbeEntry(@"G:\", () => true, () => throw new UnauthorizedAccessException("拒绝访问"), () => "NTFS"),
                new DriveProbeEntry(@"H:\", () => true, () => DriveType.Removable, () => "NTFS"),
            };

            var result = FixedNtfsDriveProbe.Probe(() => entries);
            CheckSuite.Equal(null, result.EnumerationError);
            CheckSuite.Equal(1, result.ReadyRoots.Count);
            CheckSuite.True(result.ReadyRoots.Contains(@"C:\"));
            CheckSuite.Equal(2, result.FailedRoots.Count);
            CheckSuite.True(result.FailedRoots.ContainsKey(@"D:\"));
            CheckSuite.True(result.FailedRoots.ContainsKey(@"G:\"));
        });

        checks.Run("整体磁盘枚举异常可捕获而不抛出到进程外", () =>
        {
            var result = FixedNtfsDriveProbe.Probe(() => throw new Win32Exception(21, "枚举设备失败"));
            CheckSuite.Equal(0, result.ReadyRoots.Count);
            CheckSuite.True(result.EnumerationError != null);
        });

        checks.Run("任务工作目录仅清理超时残留目录，保留活跃租约与当前目录", () =>
        {
            var root = Path.Combine(Path.GetTempPath(), "QBox-Jobs-Checks-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            try
            {
                var now = new DateTimeOffset(2026, 11, 20, 12, 0, 0, TimeSpan.Zero);
                var staleDir = Path.Combine(root, "stale");
                var activeDir = Path.Combine(root, "active");
                var currentDir = Path.Combine(root, "current");
                Directory.CreateDirectory(staleDir);
                Directory.CreateDirectory(activeDir);
                Directory.CreateDirectory(currentDir);

                var staleLease = Path.Combine(staleDir, "lease");
                File.WriteAllText(staleLease, "active");
                File.SetLastWriteTimeUtc(staleLease, now.AddSeconds(-45).UtcDateTime);
                Directory.SetLastWriteTimeUtc(staleDir, now.AddSeconds(-45).UtcDateTime);

                var activeLease = Path.Combine(activeDir, "lease");
                File.WriteAllText(activeLease, "active");
                File.SetLastWriteTimeUtc(activeLease, now.AddSeconds(-5).UtcDateTime);

                var currentLease = Path.Combine(currentDir, "lease");
                File.WriteAllText(currentLease, "active");
                File.SetLastWriteTimeUtc(currentLease, now.AddSeconds(-60).UtcDateTime);
                Directory.SetLastWriteTimeUtc(currentDir, now.AddSeconds(-60).UtcDateTime);

                JobDirectoryCleanup.CleanupStaleDirectories(root, TimeSpan.FromSeconds(30), currentDir, now);

                CheckSuite.True(!Directory.Exists(staleDir));
                CheckSuite.True(Directory.Exists(activeDir));
                CheckSuite.True(Directory.Exists(currentDir));

                JobDirectoryCleanup.TryDeleteDirectory(currentDir);
                CheckSuite.True(!Directory.Exists(currentDir));
            }
            finally
            {
                if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
            }
        });
    }
}
