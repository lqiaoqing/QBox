using QBox.Core.History;
using QBox.Core.Matching;

namespace QBox.Core.Search;

public interface IUniqueSearchSource
{
    SearchCandidate Materialize(ulong key, ReadOnlyMemory<char> name, SearchItemKind kind, ulong priority);
    string Identity(ulong key);
    int CompareIdentity(ulong key, IUniqueSearchSource other, ulong otherKey);
}

public sealed class SearchCollector
{
    private readonly int _limit;
    private readonly CancellationToken _cancellationToken;
    private readonly PriorityQueue<Row, Row>[] _best = Enum.GetValues<LauncherLane>()
        .Select(_ => new PriorityQueue<Row, Row>(new WorstFirst())).ToArray();
    private int _total;
    public PreparedQuery Query { get; }

    public SearchCollector(string query, int limit = 50, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(limit);
        Query = new FuzzyMatcher().Prepare(query);
        _limit = limit;
        _cancellationToken = cancellationToken;
    }

    // 来源必须保证每个逻辑目标只提交一次；别名在来源中归并，不能用显示条数截断输入。
    public void Add(IUniqueSearchSource source, ulong key, ReadOnlyMemory<char> name, SearchItemKind kind,
        ulong priority, HistorySignals history, bool applicationEntry = false, bool directExecutable = false)
    {
        _cancellationToken.ThrowIfCancellationRequested();
        if (!Query.CanMatch(name.Span, _cancellationToken)) return;
        AddMatched(source, key, name, kind, priority, history, applicationEntry, directExecutable);
    }

    // 调用方已用同一个 Query 核实名称或别名匹配，避免全盘命中在收集阶段重复扫描。
    public void AddMatched(IUniqueSearchSource source, ulong key, ReadOnlyMemory<char> name, SearchItemKind kind,
        ulong priority, HistorySignals history, bool applicationEntry = false, bool directExecutable = false, MatchResult? displayMatch = null)
    {
        _cancellationToken.ThrowIfCancellationRequested();
        if (displayMatch != null && (!displayMatch.IsMatch || displayMatch.AlignmentScore <= 0))
            throw new ArgumentException("补充匹配必须具有有效分值", nameof(displayMatch));
        _total = checked(_total + 1);
        if (_limit == 0) return;
        var earliest = LauncherOrdering.Lane(kind, applicationEntry, int.MaxValue, history, directExecutable);
        // 更早的桶已经填满展示区时，仍核算总命中，但无需计算这个候选的对齐分数。
        if (Capacity(earliest) == 0) return;
        var upper = displayMatch?.AlignmentScore ?? Query.ScoreUpperBound(name.Span);
        var upperLane = LauncherOrdering.Lane(kind, applicationEntry, upper, history, directExecutable);
        bool CanEnter(LauncherLane lane, int scoreBound)
        {
            var bucket = _best[(int)lane];
            var capacity = Capacity(lane);
            return capacity != 0 && (bucket.Count < capacity || BestFirst.Instance.Compare(
                new(source, key, name, kind, priority, scoreBound, lane, history, applicationEntry, directExecutable, displayMatch, _total), bucket.Peek()) < 0);
        }
        // 强弱目录分桶前尚不知道实际分数，必须证明两个可能的桶都进不去才能跳过打分。
        if (!CanEnter(upperLane, upper) && (upperLane != LauncherLane.StrongDirectory || !CanEnter(LauncherLane.WeakDirectory, 1))) return;
        var score = displayMatch?.AlignmentScore ?? Query.ScoreMatched(name.Span, _cancellationToken);
        var lane = LauncherOrdering.Lane(kind, applicationEntry, score, history, directExecutable);
        var capacity = Capacity(lane);
        if (capacity == 0) return;
        var best = _best[(int)lane];
        var row = new Row(source, key, name, kind, priority, score, lane, history, applicationEntry, directExecutable, displayMatch, _total);
        if (best.Count == capacity)
        {
            if (BestFirst.Instance.Compare(row, best.Peek()) >= 0) return;
            best.Dequeue();
        }
        best.Enqueue(row, row);
        // 早桶增加后，晚桶只保留仍可能展示的条数；不能用晚桶的名额反向裁掉混合桶前十。
        for (var index = (int)lane + 1; index < _best.Length; index++)
            while (_best[index].Count > Capacity((LauncherLane)index)) _best[index].Dequeue();
    }

    private int Capacity(LauncherLane lane)
    {
        var earlier = 0;
        for (var index = 0; index < (int)lane; index++)
            earlier += index == (int)LauncherLane.Mixed ? Math.Min(10, _best[index].Count) : _best[index].Count;
        return Math.Max(0, _limit - earlier);
    }

    public SearchResponse Finish()
    {
        _cancellationToken.ThrowIfCancellationRequested();
        var lanes = _best.Select(bucket => (IReadOnlyList<Row>)bucket.UnorderedItems.Select(item => item.Element)
            .Order(BestFirst.Instance).ToArray()).ToArray();
        var hits = LauncherOrdering.Merge(lanes).Take(_limit).Select(row => new SearchHit(
            row.Source.Materialize(row.Key, row.Name, row.Kind, row.Priority) with
                { IsApplicationEntry = row.ApplicationEntry, IsDirectExecutable = row.DirectExecutable },
            row.DisplayMatch ?? Query.Match(row.Name.ToString(), _cancellationToken), LauncherOrdering.Group(row.History, row.ApplicationEntry, row.DirectExecutable), row.History)).ToArray();
        return new(Query.Text, _total, hits);
    }

    private readonly record struct Row(IUniqueSearchSource Source, ulong Key, ReadOnlyMemory<char> Name,
        SearchItemKind Kind, ulong Priority, int Score, LauncherLane Lane, HistorySignals History, bool ApplicationEntry, bool DirectExecutable,
        MatchResult? DisplayMatch, long Ordinal);

    private sealed class WorstFirst : IComparer<Row>
    {
        public int Compare(Row left, Row right) => BestFirst.Instance.Compare(right, left);
    }

    private sealed class BestFirst : IComparer<Row>
    {
        public static BestFirst Instance { get; } = new();
        public int Compare(Row left, Row right)
        {
            var compare = left.Lane.CompareTo(right.Lane);
            if (compare != 0) return compare;
            compare = LauncherOrdering.Compare(left.Lane, left.Kind, left.Priority, left.Score, left.History,
                right.Kind, right.Priority, right.Score, right.History);
            return compare != 0 ? compare : left.Ordinal.CompareTo(right.Ordinal);
        }
    }
}
