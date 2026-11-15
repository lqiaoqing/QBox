namespace QBox.Core.Search;

public enum SearchChromeKind { Compact, Results, Full }

// 快捷窗口按 Listary 习惯：可视高度固定 8 行，列表最多约 40 条；完整界面再展示全部命中。
public static class SearchChromeLayout
{
    public const int VisibleResultRows = 8;
    public const int QuickResultCap = 40;

    public static int ResultLimit(SearchChromeKind kind) =>
        kind == SearchChromeKind.Full ? int.MaxValue : QuickResultCap;

    public static int DisplayedCount(SearchChromeKind kind, int totalMatches)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(totalMatches);
        var limit = ResultLimit(kind);
        return totalMatches < limit ? totalMatches : limit;
    }

    public static int WindowListRows(SearchChromeKind kind) =>
        kind == SearchChromeKind.Compact ? 0 : VisibleResultRows;

    public static bool HideOnDeactivation(SearchChromeKind kind) => kind != SearchChromeKind.Full;
}

// 双 Ctrl / 托盘唤起按 Listary 式渐进展开：紧凑框、结果列表、完整界面；隐藏后下次从紧凑框开始。
public sealed class SearchChromeState
{
    public SearchChromeKind Kind { get; private set; } = SearchChromeKind.Compact;

    public void ShowFromHidden() => Kind = SearchChromeKind.Compact;

    public void Reset() => Kind = SearchChromeKind.Compact;

    // 已显示时再按唤起热键：未到完整界面则展开，已在完整界面则应收起。
    public bool TryExpandToFull()
    {
        if (Kind == SearchChromeKind.Full) return false;
        Kind = SearchChromeKind.Full;
        return true;
    }

    // 完整界面右上角切回快捷窗口：有查询回到结果列表，空查询回到紧凑框，窗口保持显示。
    public bool TryCollapseFromFull(string query)
    {
        if (Kind != SearchChromeKind.Full) return false;
        Kind = query.Length == 0 ? SearchChromeKind.Compact : SearchChromeKind.Results;
        return true;
    }

    // 完整界面保持到隐藏；紧凑与结果列表随输入在两者间切换。
    public bool OnQueryText(string query)
    {
        if (Kind == SearchChromeKind.Full) return false;
        var next = query.Length == 0 ? SearchChromeKind.Compact : SearchChromeKind.Results;
        if (next == Kind) return false;
        Kind = next;
        return true;
    }
}