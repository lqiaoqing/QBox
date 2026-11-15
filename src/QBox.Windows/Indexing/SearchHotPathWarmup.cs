namespace QBox.Windows.Indexing;

// 窗口显示后在后台预热搜索热路径，隐藏时取消，避免堵塞 UI 或与空闲回收抢跑。
public sealed class SearchHotPathWarmup : IDisposable
{
    private readonly object _gate = new();
    private CancellationTokenSource? _cts;
    private Task _work = Task.CompletedTask;
    private bool _running;

    public bool IsRunning
    {
        get { lock (_gate) return _running; }
    }

    internal Task Completion
    {
        get { lock (_gate) return _work; }
    }

    public void Begin(IEnumerable<LiveVolumeIndex> indexes)
    {
        var snapshot = indexes as LiveVolumeIndex[] ?? indexes.ToArray();
        Begin(token =>
        {
            var previous = Thread.CurrentThread.Priority;
            try
            {
                Thread.CurrentThread.Priority = ThreadPriority.BelowNormal;
                foreach (var index in snapshot)
                {
                    token.ThrowIfCancellationRequested();
                    index.PrefetchSearchHotPath(token);
                }
            }
            finally { Thread.CurrentThread.Priority = previous; }
        });
    }

    internal void Begin(Action<CancellationToken> work)
    {
        Cancel();
        var cts = new CancellationTokenSource();
        lock (_gate)
        {
            _cts = cts;
            _running = true;
            _work = Task.Factory.StartNew(() =>
            {
                try { work(cts.Token); }
                catch (OperationCanceledException) { }
                catch (Exception)
                {
                    // 预热是尽力而为，映射换版或回收过程中的失败不能传到前台。
                }
                finally
                {
                    lock (_gate)
                    {
                        if (ReferenceEquals(_cts, cts)) _running = false;
                    }
                }
            }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        }
    }

    public void Cancel()
    {
        CancellationTokenSource? cts;
        lock (_gate)
        {
            cts = _cts;
            _cts = null;
            _running = false;
        }
        try { cts?.Cancel(); }
        catch (ObjectDisposedException) { }
        cts?.Dispose();
    }

    public bool Wait(TimeSpan timeout)
    {
        Task work;
        lock (_gate) work = _work;
        return work.Wait(timeout);
    }

    public void Dispose() => Cancel();
}