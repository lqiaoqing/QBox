using QBox.Windows.Ntfs;

namespace QBox.Windows.Indexing;

public sealed class NtfsChangeReader(INtfsVolume volume)
{
    private readonly NtfsFileResolver _resolver = new(volume);

    public IndexChangeSet Read(LiveVolumeIndex saved, CancellationToken cancellationToken = default)
    {
        if (saved.Base.Geometry.Serial != volume.Geometry.Serial || saved.Base.Geometry.Root != volume.Geometry.Root ||
            JournalContinuity.Check(saved.Checkpoint, volume.ReadJournalBounds()) == JournalResume.RebuildRequired)
            throw new JournalGapException("卷身份或 USN 连续性变化，需要重建索引");
        var bounds = volume.ReadJournalBounds();
        if (saved.Checkpoint.NextUsn == bounds.NextUsn) return new(saved.Checkpoint, []);
        var batch = volume.ReadJournal(saved.Checkpoint, cancellationToken);
        if (batch.NextUsn <= saved.Checkpoint.NextUsn || batch.Records.Any(r => r.File.High != 0 || r.Usn < saved.Checkpoint.NextUsn || r.Usn >= batch.NextUsn))
            throw new InvalidDataException("USN 增量批次无效或未推进");
        var changes = new Dictionary<ulong, IndexChange>();
        var visiting = new HashSet<FileReference>();
        void Refresh(FileReference requested)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (changes.ContainsKey(requested.RecordNumber)) return;
            if (!visiting.Add(requested)) throw new InvalidDataException("USN 父目录关系存在循环");
            // 按物理记录读取当前序列，旧序列的删除通知不能遮住复用后的新文件。
            var current = volume.ReadRecord(requested.RecordNumber, cancellationToken);
            if (current is { BaseFile: var baseFile } && baseFile != default)
            {
                var owner = volume.ReadRecord(baseFile.RecordNumber, cancellationToken);
                if (owner == null || owner.File != baseFile) throw new InvalidDataException("USN 扩展记录的主文件身份已改变，请稍后重试");
                Refresh(baseFile);
                visiting.Remove(requested);
                return;
            }
            var resolved = current == null ? null : _resolver.Resolve(current.File, cancellationToken);
            var file = resolved == null || resolved.Names.Count == 0 ? null : new IndexedFile(resolved.Base.File,
                resolved.Base.IsDirectory, resolved.Base.LastUsn, resolved.Base.LinkCount,
                resolved.Names.Select(n => new IndexedName(n.Parent, n.Name, n.Namespace, n.Attributes)).ToArray());
            if (file != null)
                foreach (var parent in file.Names.Select(n => n.Parent).Distinct())
                    if (parent != file.File && saved.FindFile(parent) == null) Refresh(parent);
            var identity = file?.File ?? requested;
            var previous = saved.FindFile(identity);
            if (!Equivalent(previous, file)) changes[requested.RecordNumber] = new(identity, file);
            visiting.Remove(requested);
        }
        foreach (var id in batch.Records.Select(r => r.File).DistinctBy(id => id.RecordNumber)) Refresh(id);
        if (JournalContinuity.Check(new(saved.Checkpoint.JournalId, batch.NextUsn), volume.ReadJournalBounds()) == JournalResume.RebuildRequired)
            throw new JournalGapException("读取期间 USN 日志断档，保留旧索引");
        return new(new(saved.Checkpoint.JournalId, batch.NextUsn), changes.Values.ToArray());
    }

    private static bool Equivalent(IndexedFile? left, IndexedFile? right)
    {
        if (left == null || right == null) return left == right;
        // 数据写入只更新 USN，不需要重建名称视图，也不会让索引自身的日志写入形成循环。
        return left.File == right.File && left.IsDirectory == right.IsDirectory && left.LinkCount == right.LinkCount &&
            left.Names.Count == right.Names.Count && left.Names.OrderBy(n => n.Parent.Low).ThenBy(n => n.Name, StringComparer.Ordinal)
                .SequenceEqual(right.Names.OrderBy(n => n.Parent.Low).ThenBy(n => n.Name, StringComparer.Ordinal));
    }
}
