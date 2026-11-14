namespace QBox.Windows.Indexing;

public sealed record LiveIndexFile(string Path, LiveVolumeIndex View, long Cursor, long Length, DateTime WrittenAt, string? Warning = null)
{
    public static LiveIndexFile Load(string path, CancellationToken token = default)
    {
        var stamp = new FileInfo(path);
        var index = VolumeIndexStore.Load(path, token);
        index.PrepareSearch(token);
        var source = new LiveIndexFile(path, new(index), 0, stamp.Length, stamp.LastWriteTimeUtc);
        try { return source.Refresh(token); }
        catch (IndexBaseChangedException) { return source; }
        catch (Exception error) when (error is InvalidDataException or System.Text.Json.JsonException)
        { return source with { Warning = "增量日志不可用，先使用完整快照：" + error.Message }; }
    }

    public bool NeedsReload()
    {
        var stamp = new FileInfo(Path);
        return !stamp.Exists || stamp.Length != Length || stamp.LastWriteTimeUtc != WrittenAt;
    }

    public LiveIndexFile Refresh(CancellationToken token = default)
    {
        if (NeedsReload()) throw new IndexBaseChangedException();
        var cursor = Cursor;
        var batches = IndexChangeLog.Read(IndexChangeLog.PathFor(Path), View.Base, ref cursor, cancellationToken: token);
        if (cursor == Cursor) return this;
        return this with { View = View.Apply(batches), Cursor = cursor };
    }
}
