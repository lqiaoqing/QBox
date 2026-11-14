using QBox.Core.History;

namespace QBox.Core.Search;

internal enum LauncherLane { ExactHistory, PrefixHistoryOrApplication, DirectExecutable, StrongDirectory, Mixed, WeakDirectory }

internal static class LauncherOrdering
{
    public static LauncherLane Lane(SearchItemKind kind, bool application, int score, HistorySignals history, bool directExecutable = false) =>
        history.ExactQuery != null ? LauncherLane.ExactHistory :
        history.PrefixQuery != null ? LauncherLane.PrefixHistoryOrApplication :
        application ? directExecutable ? LauncherLane.DirectExecutable : LauncherLane.PrefixHistoryOrApplication :
        kind == SearchItemKind.Directory ? score > 1 ? LauncherLane.StrongDirectory : LauncherLane.WeakDirectory : LauncherLane.Mixed;

    public static RankGroup Group(HistorySignals history, bool application, bool directExecutable = false) =>
        history.ExactQuery != null ? RankGroup.ExactQueryHistory :
        history.PrefixQuery != null ? RankGroup.PrefixQueryHistory : application ?
            directExecutable ? RankGroup.ExecutableEntry : RankGroup.ApplicationEntry : RankGroup.Ordinary;

    // 对应 Launcher 的混合桶比较器；非零键来自候选来源，不能用文件类型权重冒充。
    internal static ulong MixedKey(bool normalTag, ulong normalKey, bool hasScore, int score, byte kind)
    {
        if (!normalTag) return hasScore ? (ulong)Math.Max(0, score) : 0;
        if (normalKey != 0) return normalKey;
        if (kind != 1) return hasScore ? (ulong)Math.Max(0, score) : 0;
        return hasScore ? (0x10000000UL | (ulong)((long)Math.Clamp(score, -0x800000, 0x7fffff) + 0x800000)) << 32 :
            0x1080000000000000UL;
    }

    internal static ulong MixedKey(SearchItemKind kind, ulong normalKey, int score) =>
        MixedKey(true, normalKey, true, score, kind is SearchItemKind.Shortcut or SearchItemKind.Application ? (byte)3 : (byte)1);

    internal static int CompareNative(string mode, bool leftTag, ulong leftKey, bool leftScoreTag, int leftScore,
        byte leftKind, long? leftUsed, bool rightTag, ulong rightKey, bool rightScoreTag, int rightScore, byte rightKind, long? rightUsed)
    {
        var compare = mode == "mixed" ? MixedKey(rightTag, rightKey, rightScoreTag, rightScore, rightKind)
            .CompareTo(MixedKey(leftTag, leftKey, leftScoreTag, leftScore, leftKind)) :
            (rightScoreTag ? rightScore : 0).CompareTo(leftScoreTag ? leftScore : 0);
        return compare != 0 ? compare : Nullable.Compare(rightUsed, leftUsed);
    }

    public static int Compare(LauncherLane lane, SearchItemKind leftKind, ulong leftKey, int leftScore, HistorySignals left,
        SearchItemKind rightKind, ulong rightKey, int rightScore, HistorySignals right)
    {
        // 原程序的历史桶只按关联时间排序；无历史的应用位于此前缀桶的末尾。
        if (lane == LauncherLane.ExactHistory) return Nullable.Compare(right.ExactQuery, left.ExactQuery);
        if (lane == LauncherLane.PrefixHistoryOrApplication)
        {
            var time = Nullable.Compare(right.PrefixQuery, left.PrefixQuery);
            if (time != 0 || left.PrefixQuery != null) return time;
            // QBox 合并多个应用来源时按匹配分值择优，不能让先遍历到的来源占满展示区。
            return rightScore.CompareTo(leftScore);
        }
        var compare = lane == LauncherLane.Mixed ? MixedKey(rightKind, rightKey, rightScore).CompareTo(MixedKey(leftKind, leftKey, leftScore)) :
            rightScore.CompareTo(leftScore);
        return compare != 0 ? compare : Nullable.Compare(right.LastUsed, left.LastUsed);
    }

    // QBox 将直接 EXE 放在已定义的应用之后；其余仍保持已恢复的混合前十、弱目录、混合尾部次序。
    public static IEnumerable<T> Merge<T>(IReadOnlyList<T>[] lanes) => lanes[(int)LauncherLane.ExactHistory]
        .Concat(lanes[(int)LauncherLane.PrefixHistoryOrApplication]).Concat(lanes[(int)LauncherLane.DirectExecutable])
        .Concat(lanes[(int)LauncherLane.StrongDirectory]).Concat(lanes[(int)LauncherLane.Mixed].Take(10))
        .Concat(lanes[(int)LauncherLane.WeakDirectory]).Concat(lanes[(int)LauncherLane.Mixed].Skip(10));
}
