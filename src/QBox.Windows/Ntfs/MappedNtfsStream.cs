namespace QBox.Windows.Ntfs;

public sealed class MappedNtfsStream
{
    private readonly IReadOnlyBlockDevice _device;
    private readonly DataRun[] _runs;
    private readonly int _clusterSize;
    public ulong Length { get; }
    public ulong DeclaredLength { get; }

    public MappedNtfsStream(IReadOnlyBlockDevice device, int clusterSize, IEnumerable<MftDataExtent> extents,
        ulong? requiredLength = null, bool allowPartialMapping = false)
    {
        _device = device;
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(clusterSize);
        _clusterSize = clusterSize;
        var ordered = extents.OrderBy(e => e.LowestVcn).ToArray();
        DeclaredLength = ordered.FirstOrDefault(e => e.LowestVcn == 0)?.FileSize
            ?? throw new InvalidDataException("NTFS 流缺少首段长度");
        var runs = new List<DataRun>();
        ulong next = 0;
        foreach (var extent in ordered)
        {
            if (extent.LowestVcn != next)
            {
                if (allowPartialMapping && extent.LowestVcn > next) break;
                throw new InvalidDataException("NTFS 流区段存在空洞或重叠");
            }
            foreach (var run in extent.Runs)
            {
                if (run.FirstVcn != next || run.ClusterCount == 0 || run.FirstLcn == null || run.FirstLcn < 0)
                    throw new InvalidDataException("NTFS 元数据流映射无效或包含稀疏区段");
                runs.Add(run);
                next = checked(next + run.ClusterCount);
            }
        }
        _runs = runs.ToArray();
        var requested = requiredLength ?? DeclaredLength;
        if (requested > DeclaredLength) throw new InvalidDataException("所需 MFT 长度超过文件流长度");
        var mapped = checked(next * (ulong)clusterSize);
        if (!allowPartialMapping && mapped < requested) throw new InvalidDataException("NTFS 流映射尚未覆盖所需长度");
        Length = Math.Min(requested, mapped);
    }

    public void ReadExactly(ulong position, Span<byte> destination, CancellationToken cancellationToken = default)
    {
        if (position > Length || (ulong)destination.Length > Length - position) throw new EndOfStreamException("读取超出 NTFS 流范围");
        while (!destination.IsEmpty)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var vcn = position / (ulong)_clusterSize;
            var low = 0;
            var high = _runs.Length;
            while (low < high)
            {
                var middle = low + (high - low) / 2;
                if (_runs[middle].FirstVcn + _runs[middle].ClusterCount <= vcn) low = middle + 1;
                else high = middle;
            }
            if (low == _runs.Length || _runs[low].FirstVcn > vcn) throw new InvalidDataException("NTFS 流位置没有物理映射");
            var run = _runs[low];
            var relative = position - run.FirstVcn * (ulong)_clusterSize;
            var available = run.ClusterCount * (ulong)_clusterSize - relative;
            var count = (int)Math.Min((ulong)destination.Length, available);
            var physical = checked(run.FirstLcn!.Value * _clusterSize + (long)relative);
            _device.ReadExactly(physical, destination[..count], cancellationToken);
            destination = destination[count..];
            position += (uint)count;
        }
    }
}
