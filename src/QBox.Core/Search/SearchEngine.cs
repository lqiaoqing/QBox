using QBox.Core.History;
using QBox.Core.Matching;

namespace QBox.Core.Search;

public enum SearchItemKind { File, Directory, Application, Shortcut }
public enum RankGroup { ExactQueryHistory, PrefixQueryHistory, Ordinary, ApplicationEntry, ExecutableEntry }

public sealed record SearchCandidate(string Id, string Name, string Target, SearchItemKind Kind,
    string? Arguments = null, ulong NormalPriority = 0, bool IsApplicationEntry = false, string? AppUserModelId = null,
    bool IsDirectExecutable = false);

public sealed record SearchHit(SearchCandidate Candidate, MatchResult Match, RankGroup Group, HistorySignals History);
public sealed record SearchResponse(string Query, int TotalMatches, IReadOnlyList<SearchHit> Items);

public sealed class SearchEngine(SelectionHistory history)
{
    private readonly FuzzyMatcher _matcher = new();

    public SearchResponse Search(string query, IEnumerable<SearchCandidate> candidates, int limit = 50,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        ArgumentOutOfRangeException.ThrowIfNegative(limit);
        var prepared = _matcher.Prepare(query);
        var historyView = history.ForQuery(query);
        var matches = new Dictionary<string, RankedCandidate>(StringComparer.Ordinal);
        long ordinal = 0;
        foreach (var candidate in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ArgumentException.ThrowIfNullOrWhiteSpace(candidate.Id);
            var score = prepared.Score(candidate.Name, cancellationToken);
            if (score == 0) continue;
            var signals = historyView.Get(candidate.Id);
            var lane = LauncherOrdering.Lane(candidate.Kind, candidate.IsApplicationEntry, score, signals, candidate.IsDirectExecutable);
            var hit = new RankedCandidate(candidate, score, lane, signals, ordinal++);
            // 同一目标的别名先各自匹配，再去重，避免丢掉只有别名能命中的目标。
            if (!matches.TryGetValue(candidate.Id, out var old)) matches[candidate.Id] = hit;
            else if (hit.Score > old.Score) matches[candidate.Id] = hit with { Ordinal = old.Ordinal };
        }
        var lanes = Enum.GetValues<LauncherLane>().Select(lane => (IReadOnlyList<RankedCandidate>)matches.Values
            .Where(hit => hit.Lane == lane).Order(HitComparer.Instance).ToArray()).ToArray();
        return new(query, matches.Count, LauncherOrdering.Merge(lanes).Take(limit).Select(hit =>
            new SearchHit(hit.Candidate, prepared.Match(hit.Candidate.Name, cancellationToken),
                LauncherOrdering.Group(hit.History, hit.Candidate.IsApplicationEntry, hit.Candidate.IsDirectExecutable), hit.History)).ToArray());
    }

    private readonly record struct RankedCandidate(SearchCandidate Candidate, int Score, LauncherLane Lane, HistorySignals History, long Ordinal);

    private sealed class HitComparer : IComparer<RankedCandidate>
    {
        public static HitComparer Instance { get; } = new();

        public int Compare(RankedCandidate left, RankedCandidate right)
        {
            var compare = left.Lane.CompareTo(right.Lane);
            if (compare != 0) return compare;
            compare = LauncherOrdering.Compare(left.Lane, left.Candidate.Kind, left.Candidate.NormalPriority, left.Score, left.History,
                right.Candidate.Kind, right.Candidate.NormalPriority, right.Score, right.History);
            if (compare != 0) return compare;
            return compare != 0 ? compare : left.Ordinal.CompareTo(right.Ordinal);
        }
    }
}
