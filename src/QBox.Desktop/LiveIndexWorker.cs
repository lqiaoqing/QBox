using System.ComponentModel;
using QBox.Windows.Indexing;
using QBox.Windows.Ntfs;

namespace QBox.Desktop;

// 管理员进程只读取卷并写入索引；GUI 租约消失后自行退出，不在系统安装常驻服务。
internal static class LiveIndexWorker
{
    public static int Run(DesktopOptions options)
    {
        var directory = options.WorkerJob!;
        var statusPath = Path.Combine(directory, "status.json");
        var sessions = new Dictionary<string, Session>(StringComparer.OrdinalIgnoreCase);
        var retry = new Dictionary<string, DateTimeOffset>(StringComparer.OrdinalIgnoreCase);
        var drives = new Dictionary<string, UpdateDrive>(StringComparer.OrdinalIgnoreCase);
        using var cancellation = new CancellationTokenSource();
        using var searchWarmup = new SearchHotPathWarmup();
        void PauseWarmupForWrite()
        {
            searchWarmup.Cancel();
            searchWarmup.Wait(TimeSpan.FromSeconds(2));
            // 写盘打断的预热在窗口仍显示时再排队，避免这一轮 Append/Compact 后不再恢复。
            if (!IdleWorkingSetReclaim.IsIdle(directory)) IdleWorkingSetReclaim.RequestWarmup(directory);
        }
        using var timer = new Timer(_ =>
        {
            try
            {
                if (File.Exists(Path.Combine(directory, "cancel")) ||
                    DateTime.UtcNow - File.GetLastWriteTimeUtc(Path.Combine(directory, "lease")) > TimeSpan.FromSeconds(12)) cancellation.Cancel();
            }
            catch (ObjectDisposedException) { }
        }, null, 0, 500);
        string? previousStatus = null;
        void Save(string state, string? error = null)
        {
            var status = new UpdateStatus(state, drives.Values.ToArray(), error);
            var text = System.Text.Json.JsonSerializer.Serialize(status);
            if (text == previousStatus) return;
            DesktopStore.WriteJson(statusPath, status); previousStatus = text;
        }
        try
        {
            Save("Running");
            Directory.CreateDirectory(options.IndexDirectory!);
            while (true)
            {
                cancellation.Token.ThrowIfCancellationRequested();
                var probe = FixedNtfsDriveProbe.Probe();
                if (probe.EnumerationError != null)
                {
                    // 磁盘枚举本身出现瞬态故障时保留现有会话并等待下一轮重试，避免直接终止工作进程。
                    Save("Watching", "磁盘枚举稍后重试：" + probe.EnumerationError);
                    if (cancellation.Token.WaitHandle.WaitOne(750)) cancellation.Token.ThrowIfCancellationRequested();
                    continue;
                }
                foreach (var (root, message) in probe.FailedRoots)
                {
                    if (sessions.Remove(root, out var failed)) failed.Dispose();
                    drives[root] = new(root, "稍后重试，保留旧索引", 0, 0, message);
                    if (!retry.TryGetValue(root, out var next) || next <= DateTimeOffset.UtcNow)
                        retry[root] = DateTimeOffset.UtcNow.AddSeconds(30);
                }
                foreach (var gone in sessions.Keys.Where(key => !probe.ReadyRoots.Contains(key) && !probe.FailedRoots.ContainsKey(key)).ToArray())
                {
                    sessions[gone].Dispose(); sessions.Remove(gone);
                    drives[gone] = new(gone, "磁盘离线，保留旧索引", 0, 0);
                }
                foreach (var root in probe.ReadyRoots)
                {
                    if (retry.TryGetValue(root, out var next) && next > DateTimeOffset.UtcNow) continue;
                    try
                    {
                        if (!sessions.TryGetValue(root, out var session))
                        {
                            drives[root] = new(root, "准备实时索引", 0, 0); Save("Running");
                            session = new Session(Path.Combine(options.IndexDirectory!, root[0] + ".qbi"), root,
                                value => { drives[root] = new(root, value.Stage, value.Processed, value.Files); Save("Running"); }, cancellation.Token);
                            sessions.Add(root, session);
                        }
                        session.Poll(cancellation.Token, PauseWarmupForWrite);
                        drives[root] = new(root, "实时更新中", 0, session.View.Base.FileCount);
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or Win32Exception or NotSupportedException or System.Text.Json.JsonException)
                    {
                        if (sessions.Remove(root, out var failed)) failed.Dispose();
                        drives[root] = new(root, "稍后重试，保留旧索引", 0, 0, error.Message);
                        retry[root] = DateTimeOffset.UtcNow.AddSeconds(30);
                    }
                }
                Save("Watching");
                if (IdleWorkingSetReclaim.IsIdle(directory)) searchWarmup.Cancel();
                if (IdleWorkingSetReclaim.TryConsumeReclaim(directory))
                {
                    searchWarmup.Cancel();
                    searchWarmup.Wait(TimeSpan.FromSeconds(2));
                    foreach (var session in sessions.Values) session.ReleaseIdleCaches();
                    IdleWorkingSetReclaim.TrimCurrentProcess();
                }
                else if (IdleWorkingSetReclaim.TryConsumeWarmup(directory) && !IdleWorkingSetReclaim.IsIdle(directory))
                {
                    // 预取放到独立后台线程，不占用 Poll 与 750 ms 空闲等待的主循环。
                    searchWarmup.Begin(sessions.Values.Select(session => session.View));
                }
                if (cancellation.Token.WaitHandle.WaitOne(750)) cancellation.Token.ThrowIfCancellationRequested();
            }
        }
        catch (OperationCanceledException) { Save("Cancelled"); return 0; }
        catch (Exception error) { Save("Failed", error.Message); return 2; }
        finally
        {
            searchWarmup.Cancel();
            searchWarmup.Wait(TimeSpan.FromSeconds(2));
            foreach (var session in sessions.Values) session.Dispose();
        }
    }

    private sealed class Session : IDisposable
    {
        private readonly string _path, _log;
        private readonly IDisposable _writer;
        private readonly NativeNtfsVolume _volume;
        private readonly Action<IndexProgress> _progress;
        private DateTimeOffset _savedAt = DateTimeOffset.UtcNow;
        public LiveVolumeIndex View { get; private set; }

        public Session(string path, string root, Action<IndexProgress> progress, CancellationToken token)
        {
            _path = path; _log = IndexChangeLog.PathFor(path); _progress = progress;
            _writer = VolumeIndexStore.AcquireWriter(path);
            try
            {
                _volume = new NativeNtfsVolume(root);
                try
                {
                    var builder = new NtfsIndexBuilder(_volume);
                    VolumeIndex index;
                    var built = false;
                    if (File.Exists(path))
                    {
                        try { index = VolumeIndexStore.Load(path, token); }
                        catch (InvalidDataException) { index = builder.Build(new ProgressSink(progress), token); built = true; }
                    }
                    else { index = builder.Build(new ProgressSink(progress), token); built = true; }
                    if (built)
                    {
                        VolumeIndexStore.Save(path, index, token);
                        index = VolumeIndexStore.Load(path, token);
                    }
                    View = new(index);
                    long cursor = 0;
                    try
                    {
                        if (!File.Exists(_log)) IndexChangeLog.Reset(_log, index);
                        View = View.Apply(IndexChangeLog.Read(_log, index, ref cursor, true, token));
                    }
                    catch (IndexBaseChangedException) { IndexChangeLog.Reset(_log, index); }
                    catch (Exception error) when (error is InvalidDataException or System.Text.Json.JsonException)
                    {
                        // 损坏日志不直接丢弃，先从完整快照的 USN 追齐后再替换，避免漏掉删除事件。
                        try { index = builder.Synchronize(index, new ProgressSink(progress), token); }
                        catch (JournalGapException) { index = builder.Build(new ProgressSink(progress), token); }
                        VolumeIndexStore.Save(path, index, token);
                        index = VolumeIndexStore.Load(path, token);
                        IndexChangeLog.Reset(_log, index); View = new(index);
                    }
                }
                catch { _volume.Dispose(); throw; }
            }
            catch { _writer.Dispose(); throw; }
        }

        public void ReleaseIdleCaches() => View?.ReleaseIdleCaches();

        public void Poll(CancellationToken token, Action? beforeWrite = null)
        {
            if (NativeFileIdentity.Read(_volume.Geometry.Root).VolumeSerial != _volume.Geometry.Serial)
                throw new IOException("磁盘挂载身份已变化，重新连接后再追赶日志");
            IndexChangeSet batch;
            try { batch = new NtfsChangeReader(_volume).Read(View, token); }
            catch (JournalGapException) { beforeWrite?.Invoke(); Compact(token, true); return; }
            if (batch.Checkpoint == View.Checkpoint) return;
            var next = View.Apply([batch]);
            // 无名称变化时只定期保存检查点，崩溃重读这一分钟的 USN 不会漏项。
            if (batch.Changes.Length != 0 || DateTimeOffset.UtcNow - _savedAt > TimeSpan.FromMinutes(1))
            {
                beforeWrite?.Invoke();
                IndexChangeLog.Append(_log, batch); _savedAt = DateTimeOffset.UtcNow;
            }
            View = next;
            if (View.ChangedRecords >= 10000) { beforeWrite?.Invoke(); Compact(token, false); }
        }

        private void Compact(CancellationToken token, bool rebuild)
        {
            var builder = new NtfsIndexBuilder(_volume);
            VolumeIndex index;
            if (rebuild) index = builder.Build(new ProgressSink(_progress), token);
            else
            {
                try { index = builder.Synchronize(View.Base, new ProgressSink(_progress), token); }
                catch (JournalGapException) { index = builder.Build(new ProgressSink(_progress), token); }
            }
            // 先原子替换完整基础索引，再切换日志身份；读端在间隙继续使用上个完整视图。
            VolumeIndexStore.Save(_path, index, token);
            // 初建、恢复和合并后都转回共享视图，避免工作进程永久保留构建用的大数组。
            index = VolumeIndexStore.Load(_path, token);
            IndexChangeLog.Reset(_log, index);
            View = new(index); _savedAt = DateTimeOffset.UtcNow;
        }
        public void Dispose() { _volume.Dispose(); _writer.Dispose(); }
    }

    private sealed class ProgressSink(Action<IndexProgress> action) : IProgress<IndexProgress>
    {
        public void Report(IndexProgress value) => action(value);
    }
}

