using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json;
using QBox.Windows.Indexing;
using QBox.Windows.Ntfs;

namespace QBox.Desktop;

internal sealed record UpdateDrive(string Drive, string Stage, long Processed, int Files, string? Error = null);
internal sealed record UpdateStatus(string State, UpdateDrive[] Drives, string? Error = null);

internal sealed class IndexUpdateJob : IDisposable
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly string _jobDirectory;
    private readonly DesktopOptions _options;
    private readonly string _indexes;
    private Process? _process;
    private Timer? _heartbeat;
    public bool HasExited => _process?.HasExited == true;
    public IndexUpdateJob(DesktopOptions options, string indexes)
    {
        _options = options;
        _indexes = indexes;
        _jobDirectory = Path.Combine(options.StateDirectory, "jobs", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_jobDirectory);
    }

    public void Start()
    {
        void Beat(object? state)
        {
            try { File.WriteAllText(Path.Combine(_jobDirectory, "lease"), "active"); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        }
        Beat(null);
        _heartbeat = new Timer(Beat, null, 1000, 1000);
        var process = new ProcessStartInfo
        {
            FileName = Environment.ProcessPath ?? throw new IOException("无法定位 QBox.exe"),
            UseShellExecute = true,
            Verb = "runas",
            WindowStyle = ProcessWindowStyle.Hidden
        };
        process.ArgumentList.Add("--state-directory"); process.ArgumentList.Add(_options.StateDirectory);
        process.ArgumentList.Add("--index-directory"); process.ArgumentList.Add(_indexes);
        process.ArgumentList.Add("--sync-worker"); process.ArgumentList.Add(_jobDirectory);
        process.ArgumentList.Add("--watch-worker");
        _process = Process.Start(process) ?? throw new IOException("索引更新进程未启动");
    }
    public void Dispose() { _heartbeat?.Dispose(); _process?.Dispose(); }

    public void Cancel() => File.WriteAllText(Path.Combine(_jobDirectory, "cancel"), "");
    public UpdateStatus? Read()
    {
        try
        {
            var path = Path.Combine(_jobDirectory, "status.json");
            return File.Exists(path) ? JsonSerializer.Deserialize<UpdateStatus>(File.ReadAllBytes(path), Json) : null;
        }
        catch (Exception error) when (error is IOException or JsonException) { return null; }
    }

    public static int RunWorker(DesktopOptions options)
    {
        var directory = options.WorkerJob!;
        var jobsRoot = Path.Combine(options.StateDirectory, "jobs") + Path.DirectorySeparatorChar;
        if (!directory.StartsWith(jobsRoot, StringComparison.OrdinalIgnoreCase) || options.IndexDirectory == null)
            throw new ArgumentException("更新任务目录或索引目录无效");
        if (options.WatchWorker) return LiveIndexWorker.Run(options);
        var statusPath = Path.Combine(directory, "status.json");
        var drives = new List<UpdateDrive>();
        using var cancellation = new CancellationTokenSource();
        using var timer = new Timer(_ =>
        {
            try { if (File.Exists(Path.Combine(directory, "cancel"))) cancellation.Cancel(); }
            catch (ObjectDisposedException) { }
        }, null, 0, 300);
        void Save(string state, string? error = null) => DesktopStore.WriteJson(statusPath, new UpdateStatus(state, drives.ToArray(), error));
        try
        {
            Save("Running");
            if (File.Exists(Path.Combine(directory, "cancel"))) cancellation.Cancel();
            cancellation.Token.ThrowIfCancellationRequested();
            Directory.CreateDirectory(options.IndexDirectory);
            var volumes = DriveInfo.GetDrives().Where(d => d.IsReady && d.DriveType == DriveType.Fixed && d.DriveFormat == "NTFS").ToArray();
            if (volumes.Length == 0) throw new IOException("没有找到可用的固定 NTFS 磁盘");
            foreach (var drive in volumes)
            {
                cancellation.Token.ThrowIfCancellationRequested();
                var entry = drives.Count;
                drives.Add(new(drive.Name, "准备更新", 0, 0));
                Save("Running");
                try
                {
                    var path = Path.Combine(options.IndexDirectory, drive.Name[0] + ".qbi");
                    using var writer = VolumeIndexStore.AcquireWriter(path);
                    using var volume = new NativeNtfsVolume(drive.Name);
                    var builder = new NtfsIndexBuilder(volume);
                    var progress = new DirectProgress(value =>
                    {
                        drives[entry] = new(drive.Name, value.Stage, value.Processed, value.Files);
                        Save("Running");
                    });
                    VolumeIndex index;
                    if (File.Exists(path))
                    {
                        var saved = VolumeIndexStore.Load(path, cancellation.Token);
                        try { index = builder.Synchronize(saved, progress, cancellation.Token); }
                        catch (JournalGapException) { index = builder.Build(progress, cancellation.Token); }
                    }
                    else index = builder.Build(progress, cancellation.Token);
                    VolumeIndexStore.Save(path, index, cancellation.Token);
                    drives[entry] = new(drive.Name, "已完成", index.FileCount, index.FileCount);
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or Win32Exception or NotSupportedException)
                {
                    drives[entry] = new(drive.Name, "更新失败", 0, 0, error.Message);
                }
                Save("Running");
            }
            var failed = drives.Any(d => d.Error != null);
            Save(failed ? "CompletedWithErrors" : "Completed");
            return failed ? 2 : 0;
        }
        catch (OperationCanceledException) { Save("Cancelled"); return 130; }
        catch (Exception error) { Save("Failed", error.Message); return 2; }
    }

    private sealed class DirectProgress(Action<IndexProgress> report) : IProgress<IndexProgress>
    {
        public void Report(IndexProgress value) => report(value);
    }
}
