namespace QBox.Core.Search;

public sealed class LatestQueryGate : IDisposable
{
    private readonly object _gate = new();
    private long _version;
    private Request? _active;
    private bool _disposed;

    public Request Begin()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _active?.Cancel();
            return _active = new(this, ++_version);
        }
    }

    public bool IsCurrent(Request request)
    {
        lock (_gate) return !_disposed && request.Version == _version && !request.Token.IsCancellationRequested;
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _active?.Cancel();
        }
    }

    public sealed class Request : IDisposable
    {
        private readonly LatestQueryGate _owner;
        private readonly CancellationTokenSource _cancellation = new();
        private bool _disposed;
        internal Request(LatestQueryGate owner, long version)
        {
            _owner = owner;
            Version = version;
            Token = _cancellation.Token;
        }
        public long Version { get; }
        public CancellationToken Token { get; }
        internal void Cancel() => _cancellation.Cancel();
        public void Dispose()
        {
            lock (_owner._gate)
            {
                if (_disposed) return;
                _disposed = true;
                if (ReferenceEquals(_owner._active, this)) _owner._active = null;
                // 请求完成后再释放取消源，避免新输入与仍在运行的任务注册取消回调发生竞态。
                _cancellation.Dispose();
            }
        }
    }
}
