using System.Security.Cryptography;
using System.Text;
using System.Runtime.InteropServices;
using System.Runtime.CompilerServices;
using QBox.Windows.Ntfs;

namespace QBox.Windows.Indexing;

public static class VolumeIndexStore
{
    private static readonly byte[] Magic = "QBXNTFS1"u8.ToArray();

    public static IDisposable AcquireWriter(string path)
    {
        var absolute = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(absolute)!);
        return new FileStream(absolute + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
    }

    public static void Save(string path, VolumeIndex index, CancellationToken cancellationToken = default)
    {
        if (!BitConverter.IsLittleEndian) throw new PlatformNotSupportedException("索引保存需要小端平台");
        using var textLease = index.Text.Pin();
        var absolute = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(absolute)!);
        var temporary = absolute + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 65536))
            {
                using var writer = new BinaryWriter(file, Encoding.UTF8, leaveOpen: true);
                writer.Write(Magic);
                writer.Write(index.FormatVersion);
                var geometry = index.Geometry;
                writer.Write(geometry.Root);
                writer.Write(geometry.Serial);
                writer.Write(geometry.SectorSize);
                writer.Write(geometry.ClusterSize);
                writer.Write(geometry.RecordSize);
                writer.Write(geometry.MftValidLength);
                writer.Write(geometry.MftStartLcn);
                writer.Write(geometry.MajorVersion);
                writer.Write(geometry.MinorVersion);
                writer.Write(index.Checkpoint.JournalId);
                writer.Write(index.Checkpoint.NextUsn);
                writer.Write(index.CreatedAt.UtcTicks);
                writer.Write(index.Nodes.Length);
                writer.Write(index.Names.Length);
                writer.Write(index.Text.Length);
                writer.Flush();
                // v1/v2 记录布局一致；v2 只复用文本偏移，仍按块原样写出 UTF-16 与孤立代理项。
                WriteBlocks(file, index.Nodes.AsSpan(), cancellationToken);
                WriteBlocks(file, index.Names.AsSpan(), cancellationToken);
                WriteBlocks(file, index.Text.Span, cancellationToken);
                file.Flush();
                file.Position = 0;
                var digest = Hash(file, file.Length, cancellationToken);
                file.Position = file.Length;
                file.Write(digest);
                file.Flush(true);
            }
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporary, absolute, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    public static VolumeIndex Load(string path, CancellationToken cancellationToken = default)
    {
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete, 65536);
        if (file.Length < 128) throw new InvalidDataException("索引文件被截断");
        var bodyLength = file.Length - 32;
        var actual = Hash(file, bodyLength, cancellationToken);
        Span<byte> expected = stackalloc byte[32];
        file.ReadExactly(expected);
        if (!CryptographicOperations.FixedTimeEquals(actual, expected)) throw new InvalidDataException("索引校验失败；请保留故障文件并重建");
        file.Position = 0;
        using var reader = new BinaryReader(file, Encoding.UTF8, leaveOpen: true);
        if (!reader.ReadBytes(8).AsSpan().SequenceEqual(Magic))
            throw new InvalidDataException("索引格式版本无效");
        var version = reader.ReadInt32();
        if (version is not (1 or 2)) throw new InvalidDataException("索引格式版本无效");
        var root = reader.ReadString();
        var geometry = new NtfsGeometry(root, reader.ReadUInt64(), reader.ReadInt32(), reader.ReadInt32(), reader.ReadInt32(),
            reader.ReadInt64(), reader.ReadInt64(), reader.ReadUInt16(), reader.ReadUInt16());
        var checkpoint = new JournalCheckpoint(reader.ReadUInt64(), reader.ReadInt64());
        var created = new DateTimeOffset(reader.ReadInt64(), TimeSpan.Zero);
        var nodeCount = reader.ReadInt32();
        var nameCount = reader.ReadInt32();
        var textCount = reader.ReadInt32();
        // 在分配数组前用剩余文件长度交叉核对，损坏的计数不能触发任意大分配。
        if (nodeCount <= 0 || nameCount < nodeCount || textCount <= 0 || version == 1 && textCount < nameCount ||
            nodeCount * 28L + nameCount * 17L + textCount * 2L != bodyLength - file.Position)
            throw new InvalidDataException("索引计数与文件长度不符");
        var mapped = new MappedIndexStorage(file, file.Position, nodeCount, nameCount, textCount);
        try
        {
            // 旧 v1 继续直接映射；新建或合并产生 v2，转换时仍保留原基础身份。
            return new(geometry, checkpoint, created, new IndexNodeTable(mapped), new IndexNameTable(mapped),
                mapped.Text, cancellationToken, version);
        }
        catch { mapped.Dispose(); throw; }
    }

    private static byte[] Hash(Stream stream, long length, CancellationToken cancellationToken)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[65536];
        while (length > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var read = stream.Read(buffer.AsSpan(0, (int)Math.Min(length, buffer.Length)));
            if (read == 0) throw new EndOfStreamException("索引文件被截断");
            hash.AppendData(buffer.AsSpan(0, read));
            length -= read;
        }
        return hash.GetHashAndReset();
    }

    private static void WriteBlocks<T>(Stream stream, ReadOnlySpan<T> values, CancellationToken cancellationToken) where T : unmanaged
    {
        // 先按元素切块，避免超大映射整体转换为字节 Span 时溢出 Int32 长度。
        var maximum = 65536 / Unsafe.SizeOf<T>();
        while (!values.IsEmpty)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var count = Math.Min(values.Length, maximum);
            stream.Write(MemoryMarshal.AsBytes(values[..count]));
            values = values[count..];
        }
    }
}
