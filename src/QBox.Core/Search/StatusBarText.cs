namespace QBox.Core.Search;

/// <summary>
/// 统一计算底部状态栏展示文本，确保一次性通知可被清除且持续性警告不遮挡有效搜索耗时。
/// </summary>
public static class StatusBarText
{
    public static string? FirstNonEmpty(params string?[] values)
    {
        foreach (var value in values)
        {
            if (!string.IsNullOrEmpty(value)) return value;
        }
        return null;
    }

    public static string Format(
        string? updateText,
        bool loading,
        string? notice,
        bool showSearching,
        bool hasCurrentQueryResult,
        string searchSummaryText,
        string? persistentWarning,
        string snapshotText)
    {
        if (updateText != null) return updateText;
        if (loading) return "正在载入索引…";
        if (notice != null) return notice;
        if (showSearching) return "正在搜索…";
        if (hasCurrentQueryResult) return searchSummaryText;
        return persistentWarning ?? snapshotText;
    }
}
