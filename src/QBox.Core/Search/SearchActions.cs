using QBox.Core.History;

namespace QBox.Core.Search;

public sealed record ActivationResult(bool Opened, string? Error, string? HistoryError = null);

public sealed class SearchActions(SelectionHistory history)
{
    public async Task<ActivationResult> ActivateAsync(SearchCandidate candidate, string query,
        Func<SearchCandidate, CancellationToken, Task<bool>> launch, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(launch);
        ArgumentException.ThrowIfNullOrWhiteSpace(candidate.Id);
        ArgumentException.ThrowIfNullOrWhiteSpace(candidate.Target);
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            if (!await launch(candidate, cancellationToken).ConfigureAwait(false)) return new(false, "目标未能打开");
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception error) when (error is IOException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return new(false, error.Message);
        }
        try
        {
            // 启动已成功后再记历史；随后取消查询不能抹掉这次真实使用。
            history.RecordSelection(candidate.Id, query);
            return new(true, null);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return new(true, null, error.Message);
        }
    }
}
