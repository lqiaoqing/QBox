using System.Collections.Immutable;

namespace QBox.Core.History;

public sealed record QueryUse(string Query, DateTimeOffset LastUsedUtc);
public sealed record RecentQuery(string Query, string DisplayText, DateTimeOffset LastUsedUtc);
public sealed record TargetUse(string TargetId, DateTimeOffset LastUsedUtc, ImmutableArray<QueryUse> Queries);
public sealed record HistorySnapshot(int Version, ImmutableArray<TargetUse> Targets, ImmutableArray<RecentQuery> RecentQueries)
{
    public const int CurrentVersion = 1;
    public static HistorySnapshot Empty { get; } = new(CurrentVersion, [], []);
}

public readonly record struct HistorySignals(DateTimeOffset? ExactQuery, DateTimeOffset? PrefixQuery,
    DateTimeOffset? LastUsed);

public interface IHistoryPersistence
{
    HistorySnapshot Read();
    void Write(HistorySnapshot snapshot);
}

public sealed class HistoryConflictException(string message) : IOException(message);

public sealed class MemoryHistoryPersistence : IHistoryPersistence
{
    private HistorySnapshot _snapshot = HistorySnapshot.Empty;
    public HistorySnapshot Read() => _snapshot;
    public void Write(HistorySnapshot snapshot) => _snapshot = snapshot;
}
