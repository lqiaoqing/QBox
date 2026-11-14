using QBox.Windows.Ntfs;

namespace QBox.Windows.Indexing;

public sealed record IndexProgress(string Stage, long Processed, int Files);
public sealed class JournalGapException(string message) : IOException(message);

public sealed class NtfsIndexBuilder(INtfsVolume volume)
{
    private readonly NtfsFileResolver _resolver = new(volume);

    public VolumeIndex Build(IProgress<IndexProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        var bounds = volume.ReadJournalBounds();
        var start = new JournalCheckpoint(bounds.JournalId, bounds.NextUsn);
        var mft = _resolver.Resolve(new(0), cancellationToken) ?? throw new InvalidDataException("缺少 $MFT 记录");
        var geometry = volume.Geometry;
        if (geometry.MftValidLength % geometry.RecordSize != 0) throw new InvalidDataException("MFT 有效长度未对齐记录");
        var stream = new MappedNtfsStream(volume, geometry.ClusterSize, mft.Data, (ulong)geometry.MftValidLength);
        var files = new FileMap();
        var buffer = new byte[checked(geometry.RecordSize * 64)];
        ulong offset = 0;
        long records = 0;
        while (offset < stream.Length)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var size = (int)Math.Min((ulong)buffer.Length, stream.Length - offset);
            stream.ReadExactly(offset, buffer.AsSpan(0, size), cancellationToken);
            for (var position = 0; position < size; position += geometry.RecordSize)
            {
                var number = (offset + (uint)position) / (uint)geometry.RecordSize;
                MftRecord? record;
                try { record = MftRecordParser.Parse(buffer.AsSpan(position, geometry.RecordSize), number); }
                catch (InvalidDataException) { record = volume.ReadRecord(number, cancellationToken); }
                if (record == null || record.BaseFile != default) continue;
                if (record.HasAttributeList)
                {
                    var resolved = _resolver.Resolve(record.File, cancellationToken);
                    if (resolved != null) files.Set(ToFile(resolved.Base, resolved.Names));
                }
                else if (record.Names.Count > 0) files.Set(ToFile(record, record.Names));
            }
            offset += (uint)size;
            records += size / geometry.RecordSize;
            if (records % 32768 == 0) progress?.Report(new("读取 MFT", records, files.Count));
        }
        progress?.Report(new("核对文件系统缓存", records, files.Count));
        var seen = new HashSet<FileReference>();
        foreach (var entry in volume.EnumerateRecords(cancellationToken))
        {
            Ensure64(entry.File);
            seen.Add(entry.File);
            if (!files.TryGetValue(entry.File, out var old) || old.LastUsn != entry.Usn || old.LinkCount > 1 ||
                !old.Names.Any(n => n.Parent == entry.Parent && n.Name == entry.Name))
                Refresh(files, entry.File, cancellationToken);
        }
        // 低编号 NTFS 元数据不依赖枚举器是否呈现；普通记录必须被缓存视图确认仍存在。
        foreach (var id in files.Keys.Where(id => (id.Low & 0x0000ffffffffffffUL) >= 16 && !seen.Contains(id)).ToArray())
            files.Remove(id);
        var checkpoint = CatchUp(files, start, progress, cancellationToken);
        RepairParents(files, progress, cancellationToken);
        CheckContinuity(checkpoint, volume.ReadJournalBounds());
        progress?.Report(new("封装索引", records, files.Count));
        return VolumeIndex.Create(geometry, checkpoint, files.Values, cancellationToken);
    }

    public VolumeIndex Synchronize(VolumeIndex saved, IProgress<IndexProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (saved.Geometry.Serial != volume.Geometry.Serial || saved.Geometry.Root != volume.Geometry.Root)
            throw new JournalGapException("索引与当前卷身份不同，需要重建");
        var files = new FileMap();
        foreach (var file in saved.ExportFiles()) files.Set(file);
        var checkpoint = CatchUp(files, saved.Checkpoint, progress, cancellationToken);
        RepairParents(files, progress, cancellationToken);
        CheckContinuity(checkpoint, volume.ReadJournalBounds());
        return VolumeIndex.Create(volume.Geometry, checkpoint, files.Values, cancellationToken);
    }

    private void RepairParents(FileMap files, IProgress<IndexProgress>? progress, CancellationToken cancellationToken)
    {
        var unavailable = new HashSet<FileReference>();
        long unlinked = 0;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var missing = files.Values.SelectMany(f => f.Names).Select(n => n.Parent)
                .Where(id => !files.ContainsKey(id)).ToHashSet();
            if (missing.Count == 0) break;
            var pending = new Queue<FileReference>(missing.Where(id => !unavailable.Contains(id)));
            var requested = new HashSet<FileReference>();
            while (pending.TryDequeue(out var id))
            {
                if (files.ContainsKey(id) || unavailable.Contains(id) || !requested.Add(id)) continue;
                var parent = _resolver.Resolve(id, cancellationToken);
                if (parent == null || parent.Names.Count == 0) { unavailable.Add(id); continue; }
                if (!parent.Base.IsDirectory) throw new InvalidDataException("文件名称的父身份实际是普通文件");
                files.Set(ToFile(parent.Base, parent.Names));
                foreach (var name in parent.Names)
                    if (!files.ContainsKey(name.Parent)) pending.Enqueue(name.Parent);
            }
            var affected = files.Values.Where(f => f.Names.Any(n => unavailable.Contains(n.Parent))).Select(f => f.File).ToArray();
            if (affected.Length == 0)
            {
                if (missing.Any(id => !files.ContainsKey(id))) throw new InvalidDataException("无法闭合索引父目录关系");
                continue;
            }
            foreach (var id in affected)
            {
                Refresh(files, id, cancellationToken);
                if (!files.TryGetValue(id, out var file)) { unavailable.Add(id); continue; }
                var reachable = file.Names.Where(n => !unavailable.Contains(n.Parent)).ToArray();
                unlinked += file.Names.Count - reachable.Length;
                // 缓存确认父目录身份已经消失的名称没有可打开路径；其他硬链接必须继续保留。
                if (reachable.Length == 0) { files.Remove(id); unavailable.Add(id); }
                else files.Set(file with { Names = reachable });
            }
        }
        if (unlinked != 0) progress?.Report(new("移除已无父目录路径的名称", unlinked, files.Count));
    }

    private JournalCheckpoint CatchUp(FileMap files, JournalCheckpoint checkpoint,
        IProgress<IndexProgress>? progress, CancellationToken cancellationToken)
    {
        var bounds = volume.ReadJournalBounds();
        CheckContinuity(checkpoint, bounds);
        var goal = bounds.NextUsn;
        long changes = 0;
        while (checkpoint.NextUsn < goal)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CheckContinuity(checkpoint, volume.ReadJournalBounds());
            var batch = volume.ReadJournal(checkpoint, cancellationToken);
            if (batch.NextUsn <= checkpoint.NextUsn || batch.Records.Any(r => r.Usn < checkpoint.NextUsn || r.Usn >= batch.NextUsn))
                throw new InvalidDataException("USN 日志批次未推进或记录超出读取范围");
            // 重新读取当前 FILE_NAME 集合，统一处理移动、删除、硬链接和合并在同一条中的多种原因。
            foreach (var id in batch.Records.Select(r => r.File).Distinct()) Refresh(files, id, cancellationToken);
            checkpoint = new(checkpoint.JournalId, batch.NextUsn);
            changes += batch.Records.Count;
            progress?.Report(new("追赶 USN", changes, files.Count));
        }
        CheckContinuity(checkpoint, volume.ReadJournalBounds());
        return checkpoint;
    }

    private void Refresh(FileMap files, FileReference id, CancellationToken cancellationToken)
    {
        Ensure64(id);
        var resolved = _resolver.Resolve(id, cancellationToken);
        if (resolved == null) { files.Remove(id); return; }
        if (resolved.Names.Count == 0) files.Remove(id);
        else files.Set(ToFile(resolved.Base, resolved.Names));
    }

    private static IndexedFile ToFile(MftRecord record, IReadOnlyList<MftName> names) =>
        new(record.File, record.IsDirectory, record.LastUsn, record.LinkCount,
            names.Select(n => new IndexedName(n.Parent, n.Name, n.Namespace, n.Attributes)).ToArray());

    private static void CheckContinuity(JournalCheckpoint checkpoint, JournalBounds bounds)
    {
        if (JournalContinuity.Check(checkpoint, bounds) == JournalResume.RebuildRequired)
            throw new JournalGapException("USN 日志已断档、被重建或卷状态回退，需要重建索引；旧索引未被覆盖");
    }

    private static void Ensure64(FileReference reference)
    {
        if (reference.High != 0) throw new NotSupportedException("当前 NTFS 索引尚不支持 128 位文件身份");
    }

    private sealed class FileMap
    {
        private readonly Dictionary<FileReference, IndexedFile> _files = [];
        private readonly Dictionary<ulong, FileReference> _physical = [];
        public int Count => _files.Count;
        public IEnumerable<FileReference> Keys => _files.Keys;
        public IEnumerable<IndexedFile> Values => _files.Values;
        public bool TryGetValue(FileReference id, out IndexedFile value) => _files.TryGetValue(id, out value!);
        public bool ContainsKey(FileReference id) => _files.ContainsKey(id);
        public void Set(IndexedFile file)
        {
            var number = file.File.Low & 0x0000ffffffffffffUL;
            if (_physical.TryGetValue(number, out var previous) && previous != file.File) _files.Remove(previous);
            _physical[number] = file.File;
            _files[file.File] = file;
        }
        public void Remove(FileReference id)
        {
            if (_files.Remove(id)) _physical.Remove(id.Low & 0x0000ffffffffffffUL);
        }
    }
}
