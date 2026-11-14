using System.Buffers.Binary;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32.SafeHandles;

namespace QBox.Windows.Ntfs;

[SupportedOSPlatform("windows")]
public sealed unsafe class NativeNtfsVolume : INtfsVolume
{
    private const uint GetVolumeData = 0x90064, GetFileRecord = 0x90068,
        QueryJournal = 0x900f4, ReadUsn = 0x900bb, EnumMft = 0x900b3;
    private readonly SafeFileHandle _handle;
    private readonly object _ioGate = new();
    private void* _alignedBuffer;
    private nuint _bufferCapacity;
    private bool _disposed;
    public NtfsGeometry Geometry { get; }

    public NativeNtfsVolume(string drive)
    {
        if (drive.Length is < 1 or > 3 || !char.IsAsciiLetter(drive[0]) ||
            (drive.Length >= 2 && drive[1] != ':') || (drive.Length == 3 && drive[2] != '\\'))
            throw new ArgumentException("卷必须使用盘符，例如 C: 或 D:\\", nameof(drive));
        var root = char.ToUpperInvariant(drive[0]) + @":\";
        if (new DriveInfo(root).DriveFormat != "NTFS") throw new NotSupportedException($"{root} 不是 NTFS 卷");
        // 此类只申请 GENERIC_READ，不创建日志、不写原始卷，也不启用系统级权限。
        _handle = CreateFileW(@"\\.\" + root[..2], 0x80000000, 3, 0, 3, 0, 0);
        if (_handle.IsInvalid)
        {
            var code = Marshal.GetLastPInvokeError();
            _handle.Dispose();
            throw new Win32Exception(code, $"无法只读打开 {root} 的 NTFS 卷（Windows 错误 {code}）");
        }
        try
        {
            var bytes = Control(GetVolumeData, [], 256);
            if (bytes.Length < 104) throw new InvalidDataException("NTFS 卷信息缺少格式版本");
            Geometry = new(root, U64(bytes, 0), checked((int)U32(bytes, 40)), checked((int)U32(bytes, 44)),
                checked((int)U32(bytes, 48)), I64(bytes, 56), I64(bytes, 64), U16(bytes, 100), U16(bytes, 102));
            if (Geometry.MajorVersion != 3 || Geometry.MinorVersion > 1)
                throw new NotSupportedException($"尚未支持的 NTFS 格式：{Geometry.MajorVersion}.{Geometry.MinorVersion}");
            if (!PowerOfTwo(Geometry.SectorSize) || Geometry.SectorSize < 512 ||
                !PowerOfTwo(Geometry.ClusterSize) || Geometry.ClusterSize < Geometry.SectorSize ||
                Geometry.RecordSize < 512 || Geometry.RecordSize % 512 != 0 || Geometry.MftValidLength <= 0)
                throw new InvalidDataException("NTFS 卷几何信息无效");
        }
        catch { _handle.Dispose(); throw; }
    }

    public void ReadExactly(long offset, Span<byte> destination, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        lock (_ioGate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            while (!destination.IsEmpty)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var amount = Math.Min(destination.Length, 1024 * 1024);
                var alignedOffset = offset / Geometry.SectorSize * Geometry.SectorSize;
                var skip = (int)(offset - alignedOffset);
                var bytes = checked((amount + skip + Geometry.SectorSize - 1) / Geometry.SectorSize * Geometry.SectorSize);
                if (_bufferCapacity < (nuint)bytes)
                {
                    if (_alignedBuffer != null) NativeMemory.AlignedFree(_alignedBuffer);
                    _alignedBuffer = null;
                    _bufferCapacity = 0;
                    _alignedBuffer = NativeMemory.AlignedAlloc((nuint)bytes, (nuint)Math.Max(4096, Geometry.SectorSize));
                    if (_alignedBuffer == null) throw new OutOfMemoryException();
                    _bufferCapacity = (nuint)bytes;
                }
                if (!SetFilePointerEx(_handle, alignedOffset, out _, 0) ||
                    !ReadFile(_handle, _alignedBuffer, (uint)bytes, out var read, 0))
                    throw new Win32Exception(Marshal.GetLastPInvokeError(), "读取 NTFS 卷失败");
                if (read < amount + skip) throw new EndOfStreamException("NTFS 原始卷出现短读");
                new ReadOnlySpan<byte>((byte*)_alignedBuffer + skip, amount).CopyTo(destination);
                offset = checked(offset + amount);
                destination = destination[amount..];
            }
        }
    }

    public MftRecord? ReadRecord(ulong recordNumber, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var input = new byte[8];
        BinaryPrimitives.WriteUInt64LittleEndian(input, recordNumber);
        var output = Control(GetFileRecord, input, checked(Geometry.RecordSize + 32));
        if (output.Length < 12) throw new InvalidDataException("NTFS 文件记录响应被截断");
        // Windows 会向下返回最近的已用记录，不能把别的文件冒充请求的记录。
        if ((U64(output, 0) & 0x0000ffffffffffffUL) != recordNumber) return null;
        var length = U32(output, 8);
        if (length > output.Length - 12 || length != Geometry.RecordSize) throw new InvalidDataException("NTFS 文件记录长度异常");
        try { return MftRecordParser.Parse(output.AsSpan(12, (int)length), recordNumber, MftRecordEncoding.FileSystemControl); }
        catch (InvalidDataException error)
        {
            var record = output.AsSpan(12, (int)length);
            var usaOffset = BinaryPrimitives.ReadUInt16LittleEndian(record[4..]);
            var usaCount = BinaryPrimitives.ReadUInt16LittleEndian(record[6..]);
            var tails = new List<string>();
            if (usaOffset + usaCount * 2 <= record.Length && usaCount == record.Length / 512 + 1)
                for (var i = 1; i < usaCount; i++)
                    tails.Add($"{BinaryPrimitives.ReadUInt16LittleEndian(record[(i * 512 - 2)..]):x4}/" +
                        $"{BinaryPrimitives.ReadUInt16LittleEndian(record[(usaOffset + i * 2)..]):x4}");
            throw new InvalidDataException($"缓存记录 {recordNumber} 解析失败：{error.Message}；" +
                $"USA={usaOffset}/{usaCount}，尾部/恢复值={string.Join(',', tails)}", error);
        }
    }

    public JournalBounds ReadJournalBounds()
    {
        var bytes = Control(QueryJournal, [], 128);
        if (bytes.Length < 56) throw new InvalidDataException("USN 日志状态响应被截断");
        return new(U64(bytes, 0), I64(bytes, 8), I64(bytes, 24), I64(bytes, 16));
    }

    public UsnBatch ReadJournal(JournalCheckpoint checkpoint, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var input = NtfsControlBuffers.ReadJournal(checkpoint);
        // Timeout 与 BytesToWaitFor 保持零，读到当前末尾即可返回，不等待后续文件活动。
        return UsnJournalParser.Parse(Control(ReadUsn, input, 1024 * 1024));
    }

    public IEnumerable<UsnChange> EnumerateRecords(CancellationToken cancellationToken = default)
    {
        ulong next = 0;
        var input = new byte[24];
        BinaryPrimitives.WriteInt64LittleEndian(input.AsSpan(16), long.MaxValue);
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            BinaryPrimitives.WriteUInt64LittleEndian(input, next);
            byte[] output;
            try { output = Control(EnumMft, input, 1024 * 1024); }
            catch (Win32Exception error) when (error.NativeErrorCode == 38) { yield break; }
            if (output.Length < 8) throw new InvalidDataException("MFT 枚举响应被截断");
            var following = U64(output, 0);
            var parsed = UsnJournalParser.Parse(output);
            foreach (var record in parsed.Records) yield return record;
            if (following <= next) throw new InvalidDataException("MFT 枚举位置未推进");
            next = following;
        }
    }

    private byte[] Control(uint code, byte[] input, int outputSize)
    {
        lock (_ioGate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var output = new byte[outputSize];
            if (!DeviceIoControl(_handle, code, input, (uint)input.Length, output, (uint)output.Length, out var returned, 0))
            {
                var error = Marshal.GetLastPInvokeError();
                throw new Win32Exception(error, $"NTFS 控制请求 0x{code:x} 失败（Windows 错误 {error}）");
            }
            if (returned > output.Length) throw new InvalidDataException("NTFS 返回缓冲尺寸异常");
            return output.AsSpan(0, (int)returned).ToArray();
        }
    }

    public void Dispose()
    {
        lock (_ioGate)
        {
            if (_disposed) return;
            _disposed = true;
            if (_alignedBuffer != null) NativeMemory.AlignedFree(_alignedBuffer);
            _alignedBuffer = null;
            _handle.Dispose();
        }
    }

    private static bool PowerOfTwo(int value) => value > 0 && (value & (value - 1)) == 0;
    private static ushort U16(byte[] bytes, int offset) => BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(offset));
    private static uint U32(byte[] bytes, int offset) => BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(offset));
    private static ulong U64(byte[] bytes, int offset) => BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(offset));
    private static long I64(byte[] bytes, int offset) => BinaryPrimitives.ReadInt64LittleEndian(bytes.AsSpan(offset));

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(string name, uint access, uint share, nint security, uint creation, uint flags, nint template);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeviceIoControl(SafeFileHandle handle, uint code, byte[] input, uint inputSize,
        [Out] byte[] output, uint outputSize, out uint returned, nint overlapped);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetFilePointerEx(SafeFileHandle handle, long distance, out long position, uint method);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ReadFile(SafeFileHandle handle, void* buffer, uint size, out uint read, nint overlapped);
}
