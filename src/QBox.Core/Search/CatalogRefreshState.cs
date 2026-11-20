namespace QBox.Core.Search;

/// <summary>
/// 协调应用与快捷方式目录的后台刷新，防止采集期间索引更新导致的旧结果回写或脏标记丢失。
/// </summary>
public sealed class CatalogRefreshState
{
    private readonly object _gate = new();
    private long _sourceVersion;
    private bool _refreshing;
    private bool _dirty;
    private DateTimeOffset _lastRefreshedAt;

    public CatalogRefreshState(DateTimeOffset initialRefreshedAt)
    {
        _lastRefreshedAt = initialRefreshedAt;
    }

    /// <summary>
    /// 整套索引重载完成且已同步刷新目录时重置状态并推进版本，使在途旧刷新自动失效。
    /// </summary>
    public void MarkReloaded(DateTimeOffset now)
    {
        lock (_gate)
        {
            _sourceVersion++;
            _dirty = false;
            _lastRefreshedAt = now;
        }
    }

    /// <summary>
    /// 实时索引中的快捷方式发生变化时推进源版本并标脏，等待或触发重新采集。
    /// </summary>
    public void MarkShortcutSourceChanged()
    {
        lock (_gate)
        {
            _sourceVersion++;
            _dirty = true;
        }
    }

    /// <summary>
    /// 判断当前是否需要发起新一轮目录刷新。
    /// </summary>
    public bool ShouldStart(DateTimeOffset now, TimeSpan interval)
    {
        lock (_gate)
        {
            if (_refreshing) return false;
            return _dirty || now - _lastRefreshedAt > interval;
        }
    }

    /// <summary>
    /// 尝试占用刷新槽位并捕获当前源版本；若已有刷新在途则返回 false。
    /// </summary>
    public bool TryBegin(out long capturedVersion)
    {
        lock (_gate)
        {
            if (_refreshing)
            {
                capturedVersion = _sourceVersion;
                return false;
            }
            _refreshing = true;
            _dirty = false;
            capturedVersion = _sourceVersion;
            return true;
        }
    }

    /// <summary>
    /// 仅当捕获的源版本仍为最新时允许发布采集结果，避免旧视图覆盖较新状态。
    /// </summary>
    public bool CanPublish(long capturedVersion)
    {
        lock (_gate)
        {
            return capturedVersion == _sourceVersion;
        }
    }

    /// <summary>
    /// 结束本次刷新；若采集期间源已变化则保留脏状态并返回 true，提示立即重新调度。
    /// </summary>
    public bool Complete(long capturedVersion, DateTimeOffset now, bool succeeded = true)
    {
        lock (_gate)
        {
            _refreshing = false;
            if (capturedVersion != _sourceVersion || _dirty)
            {
                _dirty = true;
                return true;
            }
            if (succeeded) _lastRefreshedAt = now;
            return false;
        }
    }
}
