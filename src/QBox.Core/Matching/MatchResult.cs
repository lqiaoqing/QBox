namespace QBox.Core.Matching;

public readonly record struct TextRange(int Start, int Length);

// AlignmentScore 是内部对齐分值；调用方不能将它当作已恢复的 Launcher 最终排序分数。
public sealed record MatchResult(bool IsMatch, int AlignmentScore, IReadOnlyList<TextRange> Highlights)
{
    public static MatchResult None { get; } = new(false, 0, []);
}
