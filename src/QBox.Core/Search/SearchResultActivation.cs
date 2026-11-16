namespace QBox.Core.Search;

// 判断已展示的当前查询结果是否可打开或导航；同一查询的静默续填不能把操作拦住。
public static class SearchResultActivation
{
    public static bool CanActivateDisplayed(
        bool loading,
        bool searching,
        bool silentRefresh,
        bool resultsDisplayed,
        string displayedQuery,
        string queryText,
        long resultVersion,
        long inputVersion)
    {
        ArgumentNullException.ThrowIfNull(displayedQuery);
        ArgumentNullException.ThrowIfNull(queryText);
        if (loading || !resultsDisplayed || displayedQuery.Length == 0 || displayedQuery != queryText)
            return false;
        if (!searching) return resultVersion == inputVersion;
        return silentRefresh;
    }

    // 单击结果行打开；滚动条、拖动和双击的第二次松开不打开，避免重复启动。
    public static bool ShouldOpenOnRelease(
        bool leftButton,
        int clickCount,
        bool isScrollChrome,
        bool samePressedItem,
        double deltaX,
        double deltaY,
        double maxHorizontalDrag,
        double maxVerticalDrag)
    {
        if (!leftButton || clickCount != 1 || isScrollChrome || !samePressedItem) return false;
        return Math.Abs(deltaX) <= maxHorizontalDrag && Math.Abs(deltaY) <= maxVerticalDrag;
    }
}