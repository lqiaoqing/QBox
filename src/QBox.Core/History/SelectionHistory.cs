using System.Collections.Immutable;

namespace QBox.Core.History;

public sealed class SelectionHistory
{
    public const int QueriesPerTarget = 8;
    public const int RecentQueryLimit = 500;
    private readonly object _gate = new();
    private readonly IHistoryPersistence _persistence;
    private HistorySnapshot _snapshot;
    private Dictionary<string, TargetUse> _targets;

    public SelectionHistory(IHistoryPersistence? persistence = null)
    {
        _persistence = persistence ?? new MemoryHistoryPersistence();
        _snapshot = _persistence.Read();
        Validate(_snapshot);
        _targets = _snapshot.Targets.ToDictionary(t => t.TargetId, StringComparer.Ordinal);
    }

    public HistorySnapshot Snapshot
    {
        get { lock (_gate) return _snapshot; }
    }

    public HistoryView ForQuery(string query)
    {
        lock (_gate) return new(_targets, NormalizeQuery(query));
    }

    public void RecordSelection(string targetId, string query, DateTimeOffset? selectedAt = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(targetId);
        ArgumentNullException.ThrowIfNull(query);
        var normalized = NormalizeQuery(query);
        var now = (selectedAt ?? DateTimeOffset.UtcNow).ToUniversalTime();
        lock (_gate)
        {
            for (var attempt = 0; ; attempt++)
            {
                var targets = new Dictionary<string, TargetUse>(_targets, StringComparer.Ordinal);
                targets.TryGetValue(targetId, out var old);
                var queries = old?.Queries ?? [];
                if (normalized.Length != 0)
                {
                    var previousTime = queries.FirstOrDefault(q => q.Query == normalized)?.LastUsedUtc;
                    var time = previousTime > now ? previousTime.Value : now;
                    queries = queries.Where(q => q.Query != normalized)
                        .Prepend(new QueryUse(normalized, time)).Take(QueriesPerTarget).ToImmutableArray();
                }
                targets[targetId] = new(targetId, old?.LastUsedUtc > now ? old.LastUsedUtc : now, queries);
                var recent = _snapshot.RecentQueries;
                if (normalized.Length != 0)
                    recent = recent.Where(q => q.Query != normalized)
                        .Prepend(new RecentQuery(normalized, query.Trim(), now)).Take(RecentQueryLimit).ToImmutableArray();
                var next = new HistorySnapshot(HistorySnapshot.CurrentVersion,
                    targets.Values.OrderBy(t => t.TargetId, StringComparer.Ordinal).ToImmutableArray(), recent);
                try
                {
                    // 先持久化再替换内存快照，写入失败时不能给出已保存的假象。
                    _persistence.Write(next);
                    _snapshot = next;
                    _targets = targets;
                    return;
                }
                catch (HistoryConflictException) when (attempt < 3)
                {
                    // 多进程同时选择时重新合并，避免后一份快照覆盖另一进程的新记录。
                    var current = _persistence.Read();
                    Validate(current);
                    _snapshot = current;
                    _targets = current.Targets.ToDictionary(t => t.TargetId, StringComparer.Ordinal);
                }
            }
        }
    }

    public static string NormalizeQuery(string query)
    {
        ArgumentNullException.ThrowIfNull(query);
        return query.Trim().ToLowerInvariant();
    }

    internal static void Validate(HistorySnapshot snapshot)
    {
        if (snapshot.Version != HistorySnapshot.CurrentVersion)
            throw new InvalidDataException($"不支持的历史格式版本：{snapshot.Version}");
        if (snapshot.Targets.IsDefault || snapshot.RecentQueries.IsDefault || snapshot.RecentQueries.Length > RecentQueryLimit)
            throw new InvalidDataException("历史记录集合无效");
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var target in snapshot.Targets)
        {
            if (target == null || string.IsNullOrWhiteSpace(target.TargetId) || !ids.Add(target.TargetId) ||
                target.Queries.IsDefault || target.Queries.Length > QueriesPerTarget)
                throw new InvalidDataException("历史目标身份或关联查询无效");
            var queries = new HashSet<string>(StringComparer.Ordinal);
            foreach (var query in target.Queries)
                if (query == null || string.IsNullOrEmpty(query.Query) || NormalizeQuery(query.Query) != query.Query ||
                    !queries.Add(query.Query) || query.LastUsedUtc > target.LastUsedUtc)
                    throw new InvalidDataException("目标查询历史无效");
        }
        var recentKeys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var recent in snapshot.RecentQueries)
            if (recent == null || string.IsNullOrEmpty(recent.Query) || NormalizeQuery(recent.Query) != recent.Query ||
                recent.DisplayText == null || !recentKeys.Add(recent.Query))
                throw new InvalidDataException("最近查询历史无效");
    }
}

public sealed class HistoryView
{
    private readonly IReadOnlyDictionary<string, TargetUse> _targets;
    private readonly string _query;

    internal HistoryView(IReadOnlyDictionary<string, TargetUse> targets, string query) => (_targets, _query) = (targets, query);

    public HistorySignals Get(string targetId)
    {
        if (!_targets.TryGetValue(targetId, out var target)) return default;
        DateTimeOffset? exact = null, prefix = null;
        if (_query.Length != 0)
        {
            foreach (var use in target.Queries)
            {
                if (use.Query == _query) exact = use.LastUsedUtc;
                else if (use.Query.StartsWith(_query, StringComparison.Ordinal) && (prefix == null || use.LastUsedUtc > prefix))
                    prefix = use.LastUsedUtc;
            }
        }
        return new(exact, exact != null ? null : prefix, target.LastUsedUtc);
    }

    public IEnumerable<(string TargetId, HistorySignals Signals)> EnumerateSignals()
    {
        foreach (var id in _targets.Keys) yield return (id, Get(id));
    }
}
