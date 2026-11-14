namespace QBox.Windows.Ntfs;

public sealed record ResolvedMftFile(MftRecord Base, IReadOnlyList<MftRecord> Segments,
    IReadOnlyList<MftName> Names, IReadOnlyList<MftDataExtent> Data);

public sealed class NtfsFileResolver(INtfsVolume volume)
{
    public ResolvedMftFile? Resolve(FileReference reference, CancellationToken cancellationToken = default)
    {
        if (reference.High != 0) throw new NotSupportedException("NTFS 文件记录读取只接受 64 位身份");
        var root = volume.ReadRecord(reference.Low & 0x0000ffffffffffffUL, cancellationToken);
        if (root == null || (reference.Low >> 48 != 0 && root.File != reference)) return null;
        if (root.BaseFile != default) throw new InvalidDataException("请求的文件记录是扩展段，不能作为基段使用");
        var segments = new Dictionary<FileReference, MftRecord> { [root.File] = root };
        var entries = new List<AttributeListEntry>(root.AttributeList ?? []);
        var complete = !root.HasAttributeList || (root.AttributeListData?.Count ?? 0) == 0;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var before = segments.Count;
            foreach (var entry in entries.ToArray())
            {
                if (segments.ContainsKey(entry.Segment)) continue;
                var segment = volume.ReadRecord(entry.Segment.Low & 0x0000ffffffffffffUL, cancellationToken);
                if (segment == null || segment.File != entry.Segment || segment.BaseFile != root.File)
                    throw new InvalidDataException("外部属性段身份已改变，需要重新构建或重试");
                segments.Add(segment.File, segment);
            }
            var listExtents = segments.Values.SelectMany(s => s.AttributeListData ?? [])
                .GroupBy(e => (e.LowestVcn, e.Instance)).Select(g => g.First()).ToArray();
            if (listExtents.Length != 0)
            {
                var listStream = new MappedNtfsStream(volume, volume.Geometry.ClusterSize, listExtents, allowPartialMapping: true);
                if (listStream.Length > int.MaxValue) throw new NotSupportedException("外部属性列表超过当前可解析尺寸");
                var bytes = new byte[(int)listStream.Length];
                listStream.ReadExactly(0, bytes, cancellationToken);
                var parsed = MftAttributeListParser.Parse(bytes, listStream.Length < listStream.DeclaredLength);
                entries = parsed.Entries.ToList();
                complete = parsed.Complete && listStream.Length == listStream.DeclaredLength;
            }
            if (entries.All(e => segments.ContainsKey(e.Segment)) && complete) break;
            if (segments.Count == before && entries.All(e => segments.ContainsKey(e.Segment)))
                throw new InvalidDataException("无法补齐外部属性列表映射");
        }
        var all = segments.Values.ToArray();
        var names = all.SelectMany(s => s.Names).Distinct().ToArray();
        var data = all.SelectMany(s => s.Data).OrderBy(d => d.LowestVcn).ToArray();
        // 常驻 DATA 也会出现在属性列表中，不能只检查非常驻数据区段。
        foreach (var entry in entries)
            if (!(segments[entry.Segment].Attributes ?? []).Any(a => a.Type == entry.Type &&
                a.Name == entry.Name && a.Instance == entry.Instance && a.LowestVcn == entry.LowestVcn))
                throw new InvalidDataException("属性列表引用的属性缺失");
        return new(root, all, names, data);
    }
}
