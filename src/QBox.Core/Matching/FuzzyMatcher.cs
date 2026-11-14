using System.Text;

namespace QBox.Core.Matching;

public sealed class FuzzyMatcher
{
    public PreparedQuery Prepare(string query) => new(query);

    public MatchResult Match(string query, string candidate, CancellationToken cancellationToken = default) =>
        Prepare(query).Match(candidate, cancellationToken);
}

public sealed class PreparedQuery
{
    private readonly Rune[] _query;
    private readonly string? _asciiUpper;

    internal PreparedQuery(string query)
    {
        ArgumentNullException.ThrowIfNull(query);
        Text = query;
        _query = query.EnumerateRunes().ToArray();
        _asciiUpper = Ascii.IsValid(query) ? query.ToUpperInvariant() : null;
    }

    public string Text { get; }

    public int ScoreUpperBound(ReadOnlySpan<char> candidate)
    {
        // 每个 Rune 至多占两个 UTF-16 单元；对齐只有扣分，因此该上界不会误删可进入前列的结果。
        var minimumRunes = Ascii.IsValid(candidate) ? candidate.Length : (candidate.Length + 1) / 2;
        return Math.Max(1, checked(_query.Length * 1000 - minimumRunes * 10));
    }

    public bool CanMatch(ReadOnlySpan<char> candidate, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_asciiUpper != null && Ascii.IsValid(candidate))
        {
            foreach (var letter in _asciiUpper)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var lower = char.IsAsciiLetterUpper(letter) ? (char)(letter + 32) : letter;
                var at = candidate.IndexOfAny(letter, lower);
                if (at < 0) return false;
                candidate = candidate[(at + 1)..];
            }
            return true;
        }
        var next = 0;
        var scanned = 0;
        foreach (var rune in candidate.EnumerateRunes())
        {
            if ((scanned++ & 63) == 0) cancellationToken.ThrowIfCancellationRequested();
            if (next == _query.Length) return true;
            if (Equivalent(_query[next], rune)) next++;
        }
        return next == _query.Length;
    }

    public int Score(ReadOnlySpan<char> candidate, CancellationToken cancellationToken = default)
    {
        if (!CanMatch(candidate, cancellationToken)) return 0;
        return ScoreMatched(candidate, cancellationToken);
    }

    internal int ScoreMatched(ReadOnlySpan<char> candidate, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_query.Length == 0) return 1;
        if (_asciiUpper == null || !Ascii.IsValid(candidate)) return Match(candidate.ToString(), cancellationToken).AlignmentScore;
        var width = _query.Length + 1;
        // 排序阶段只计算分值，避免为所有命中分配高亮链；Unicode 继续走已验证的完整实现。
        Span<int> storage = width <= 256 ? stackalloc int[width * 4] : new int[checked(width * 4)];
        storage.Clear();
        var best = storage[..width];
        var snapshot = storage.Slice(width, width);
        var previousEnd = storage.Slice(width * 2, width);
        var currentEnd = storage.Slice(width * 3, width);
        previousEnd.Fill(int.MinValue);
        var baseScore = checked(_query.Length * 1000 - candidate.Length * 10);
        best[0] = baseScore;
        var acronymScore = baseScore - 100;
        var acronymPrefix = 0;
        for (var at = 0; at < candidate.Length && acronymPrefix < _asciiUpper.Length; at = NextAsciiBoundary(candidate, at))
        {
            if (_asciiUpper[acronymPrefix] == UpperAscii(candidate[at])) acronymPrefix++;
            else acronymScore -= 50;
        }
        if (acronymPrefix != _asciiUpper.Length) acronymScore = 0;
        for (var start = 0; start < candidate.Length;)
        {
            cancellationToken.ThrowIfCancellationRequested();
            best.CopyTo(snapshot);
            currentEnd.Fill(int.MinValue);
            if (AsciiSeparator(candidate[start]))
            {
                for (var prefix = 0; prefix < _asciiUpper.Length; prefix++)
                {
                    if (_asciiUpper[prefix] != candidate[start]) continue;
                    best[prefix + 1] = Math.Max(best[prefix + 1], snapshot[prefix]);
                    currentEnd[prefix + 1] = Math.Max(currentEnd[prefix + 1], previousEnd[prefix]);
                }
                start++;
            }
            else
            {
                var end = NextAsciiBoundary(candidate, start);
                for (var prefix = _asciiUpper.Length - 1; prefix >= 0; prefix--)
                {
                    if (snapshot[prefix] == 0 || _asciiUpper[prefix] != UpperAscii(candidate[start])) continue;
                    var score = snapshot[prefix] - 100;
                    if (prefix != 0) score = Math.Max(score, previousEnd[prefix]);
                    score -= 2 * start;
                    var next = prefix + 1;
                    best[next] = Math.Max(best[next], score);
                    if (start + 1 == end) currentEnd[next] = Math.Max(currentEnd[next], score);
                    var skipped = false;
                    for (var position = start + 1; position < end && next < _asciiUpper.Length; position++)
                    {
                        if (_asciiUpper[next] != UpperAscii(candidate[position])) { skipped = true; continue; }
                        score -= 2 * position + (skipped ? 400 : 0);
                        next++;
                        best[next] = Math.Max(best[next], score);
                        if (position + 1 == end) currentEnd[next] = Math.Max(currentEnd[next], score);
                    }
                }
                start = end;
            }
            var swap = previousEnd; previousEnd = currentEnd; currentEnd = swap;
        }
        return Math.Max(1, Math.Max(best[^1], acronymScore));
    }

    private static char UpperAscii(char value) => char.IsAsciiLetterLower(value) ? (char)(value - 32) : value;
    private static bool AsciiSeparator(char value) => char.IsWhiteSpace(value) ||
        value is >= '!' and <= '/' or >= ':' and <= '@' or >= '[' and <= '`' or >= '{' and <= '~';
    private static int NextAsciiBoundary(ReadOnlySpan<char> value, int start)
    {
        for (var i = start + 1; i < value.Length; i++)
        {
            var current = value[i]; var previous = value[i - 1];
            if (AsciiSeparator(current)) return i;
            if (char.IsAsciiLetterUpper(current) && (!char.IsAsciiLetterUpper(previous) ||
                (i + 1 < value.Length && char.IsAsciiLetterLower(value[i + 1])))) return i;
            if ((char.IsAsciiDigit(current) && char.IsAsciiLetter(previous)) ||
                (char.IsAsciiLetter(current) && char.IsAsciiDigit(previous))) return i;
        }
        return value.Length;
    }

    public MatchResult Match(string candidate, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        if (!CanMatch(candidate, cancellationToken)) return MatchResult.None;
        if (_query.Length == 0) return new(true, 1, []);

        var tokens = Tokenize(candidate);
        var baseScore = checked(_query.Length * 1000 - tokens.Length * 10);
        var acronymScore = baseScore - 100;
        var acronymPrefix = 0;
        Position? acronymPositions = null;
        for (var start = 0; start < tokens.Length && acronymPrefix < _query.Length;
             start = NextBoundary(tokens, start))
        {
            if (Equivalent(_query[acronymPrefix], tokens[start].Value))
            {
                acronymPositions = new(start, acronymPositions);
                acronymPrefix++;
            }
            else acronymScore -= 50;
        }
        if (acronymPrefix != _query.Length) acronymScore = 0;

        // 全局前缀最优值与“恰好在上一段末尾结束”的状态分别保存，跨段的起始代价不同。
        var best = new State[_query.Length + 1];
        var snapshot = new State[best.Length];
        var previousEnd = new State[best.Length];
        var currentEnd = new State[best.Length];
        best[0] = new(baseScore, null);
        Array.Fill(previousEnd, State.Invalid);

        for (var start = 0; start < tokens.Length;)
        {
            cancellationToken.ThrowIfCancellationRequested();
            best.CopyTo(snapshot, 0);
            Array.Fill(currentEnd, State.Invalid);
            if (IsSeparator(tokens[start].Value))
            {
                // 标点和空白按字面匹配；它们本身不收取词段起始及位置代价。
                for (var prefix = 0; prefix < _query.Length; prefix++)
                {
                    if (_query[prefix] != tokens[start].Value) continue;
                    Update(best, prefix + 1, snapshot[prefix].AddPosition(start));
                    if (previousEnd[prefix].Score != int.MinValue)
                        Update(currentEnd, prefix + 1, previousEnd[prefix].AddPosition(start));
                }
                start++;
            }
            else
            {
                var end = NextBoundary(tokens, start);
                for (var prefix = _query.Length - 1; prefix >= 0; prefix--)
                {
                    if ((prefix & 31) == 0) cancellationToken.ThrowIfCancellationRequested();
                    if (snapshot[prefix].Score == 0 || !Equivalent(_query[prefix], tokens[start].Value))
                        continue;
                    var state = snapshot[prefix].WithScore(snapshot[prefix].Score - 100);
                    if (prefix != 0 && previousEnd[prefix].Score > state.Score)
                        state = previousEnd[prefix];
                    state = state.WithScore(state.Score - 2 * tokens[start].Utf8Offset);
                    state = state.AddPosition(start);
                    var next = prefix + 1;
                    Update(best, next, state);
                    if (start + 1 == end) Update(currentEnd, next, state);
                    var skipped = false;
                    for (var position = start + 1; position < end && next < _query.Length; position++)
                    {
                        if (!Equivalent(_query[next], tokens[position].Value))
                        {
                            skipped = true;
                            continue;
                        }
                        // 同一词段发生跳字后，后续匹配字符继续承担跳字代价。
                        state = state.WithScore(state.Score - 2 * tokens[position].Utf8Offset - (skipped ? 400 : 0));
                        state = state.AddPosition(position);
                        next++;
                        Update(best, next, state);
                        if (position + 1 == end) Update(currentEnd, next, state);
                    }
                }
                start = end;
            }
            (previousEnd, currentEnd) = (currentEnd, previousEnd);
        }

        var result = best[^1];
        if (acronymScore >= result.Score) result = new(acronymScore, acronymPositions);
        if (result.Score < 1)
        {
            // 有序筛选已经确认命中；低分对齐仍返回结果，并用完整有序匹配产生高亮。
            var next = 0;
            Position? positions = null;
            for (var i = 0; i < tokens.Length && next < _query.Length; i++)
                if (Equivalent(_query[next], tokens[i].Value))
                {
                    positions = new(i, positions);
                    next++;
                }
            result = new(1, positions);
        }
        return new(true, result.Score, Ranges(tokens, result.Positions));
    }

    private static bool Equivalent(Rune left, Rune right) =>
        left == right || Rune.ToUpperInvariant(left) == Rune.ToUpperInvariant(right);

    private static void Update(State[] states, int index, State value)
    {
        if (value.Score > states[index].Score) states[index] = value;
    }

    private static bool IsSeparator(Rune rune) => Rune.IsWhiteSpace(rune) ||
        rune.Value is >= 33 and <= 47 or >= 58 and <= 64 or >= 91 and <= 96 or >= 123 and <= 126;

    private static bool IsCjk(Rune rune) => rune.Value is >= 0x3400 and <= 0x9fff
        or >= 0xf900 and <= 0xfaff or >= 0x20000 and <= 0x3134f;

    private static int NextBoundary(Token[] tokens, int start)
    {
        if (IsCjk(tokens[start].Value)) return start + 1;
        for (var i = start + 1; i < tokens.Length; i++)
        {
            var current = tokens[i].Value;
            var previous = tokens[i - 1].Value;
            if (IsSeparator(current) || IsCjk(current)) return i;
            if (Rune.IsUpper(current) && (!Rune.IsUpper(previous) ||
                (i + 1 < tokens.Length && Rune.IsLower(tokens[i + 1].Value)))) return i;
            if ((Rune.IsDigit(current) && Rune.IsLetter(previous)) ||
                (Rune.IsLetter(current) && Rune.IsDigit(previous))) return i;
        }
        return tokens.Length;
    }

    private static Token[] Tokenize(string text)
    {
        var tokens = new List<Token>(text.Length);
        var utf16 = 0;
        var utf8 = 0;
        foreach (var rune in text.EnumerateRunes())
        {
            tokens.Add(new(rune, utf16, utf8));
            utf16 += rune.Utf16SequenceLength;
            utf8 += rune.Utf8SequenceLength;
        }
        return tokens.ToArray();
    }

    private static TextRange[] Ranges(Token[] tokens, Position? position)
    {
        var offsets = new List<int>();
        for (; position != null; position = position.Previous) offsets.Add(position.Index);
        offsets.Reverse();
        var ranges = new List<TextRange>();
        foreach (var index in offsets)
        {
            var token = tokens[index];
            if (ranges.Count != 0 && ranges[^1].Start + ranges[^1].Length == token.Utf16Offset)
                ranges[^1] = ranges[^1] with { Length = ranges[^1].Length + token.Value.Utf16SequenceLength };
            else ranges.Add(new(token.Utf16Offset, token.Value.Utf16SequenceLength));
        }
        return ranges.ToArray();
    }

    private readonly record struct Token(Rune Value, int Utf16Offset, int Utf8Offset);
    private sealed record Position(int Index, Position? Previous);
    private readonly record struct State(int Score, Position? Positions)
    {
        public static State Invalid => new(int.MinValue, null);
        public State WithScore(int score) => new(score, Positions);
        public State AddPosition(int index) => new(Score, new(index, Positions));
    }
}
