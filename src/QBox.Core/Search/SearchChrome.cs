namespace QBox.Core.Search;

public enum SearchChromeKind { Compact, Results, Full }

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
