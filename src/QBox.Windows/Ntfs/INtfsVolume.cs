namespace QBox.Windows.Ntfs;

public sealed record NtfsGeometry(string Root, ulong Serial, int SectorSize, int ClusterSize, int RecordSize,
    long MftValidLength, long MftStartLcn, ushort MajorVersion, ushort MinorVersion);

public interface IReadOnlyBlockDevice
{
    void ReadExactly(long offset, Span<byte> destination, CancellationToken cancellationToken = default);
}

public interface INtfsVolume : IReadOnlyBlockDevice, IDisposable
{
    NtfsGeometry Geometry { get; }
    MftRecord? ReadRecord(ulong recordNumber, CancellationToken cancellationToken = default);
    JournalBounds ReadJournalBounds();
    UsnBatch ReadJournal(JournalCheckpoint checkpoint, CancellationToken cancellationToken = default);
    IEnumerable<UsnChange> EnumerateRecords(CancellationToken cancellationToken = default);
}
