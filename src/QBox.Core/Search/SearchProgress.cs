namespace QBox.Core.Search;

// 判断底栏要不要显示「正在搜索…」：用户刚输入的首次查询可以提示，已有结果的重搜保持统计。
public static class SearchProgress
{
    public static bool SilentRefresh(string query, string displayedQuery, bool resultsDisplayed) =>
        query.Length > 0 && query == displayedQuery && resultsDisplayed;

    public static bool ShowSearching(bool searching, bool silentRefresh) => searching && !silentRefresh;

    // 查询框有内容或正在搜索时，不得继续展示「输入名称，开始搜索」。
    public static bool ShowStartPrompt(string query, bool searching) =>
        query.Length == 0 && !searching;
}
