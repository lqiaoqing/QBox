using System.Security.Cryptography;
using System.Text;
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
        var absolute = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(absolute)!);
        var temporary = absolute + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 65536))
            {
                using var writer = new BinaryWriter(file, Encoding.UTF8, leaveOpen: true);
                writer.Write(Magic);
                writer.Write(1);
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
                foreach (var node in index.Nodes)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    writer.Write(node.File); writer.Write(node.FirstName); writer.Write(node.NameCount);
                    writer.Write(node.IsDirectory); writer.Write(node.LastUsn.HasValue); writer.Write(node.LastUsn ?? 0);
                    writer.Write(node.LinkCount);
                }
                foreach (var name in index.Names)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    writer.Write(name.Parent); writer.Write(name.Offset); writer.Write(name.Length);
                    writer.Write(name.Namespace); writer.Write(name.Attributes);
                }
                // 直接保存 UTF-16 代码单元，避免 UTF-8 编码把孤立代理项替换成其他文件名。
                foreach (var character in index.Text) writer.Write((ushort)character);
                writer.Flush();
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
        if (!reader.ReadBytes(8).AsSpan().SequenceEqual(Magic) || reader.ReadInt32() != 1)
            throw new InvalidDataException("索引格式版本无效");
        var root = reader.ReadString();
        var geometry = new NtfsGeometry(root, reader.ReadUInt64(), reader.ReadInt32(), reader.ReadInt32(), reader.ReadInt32(),
            reader.ReadInt64(), reader.ReadInt64(), reader.ReadUInt16(), reader.ReadUInt16());
        var checkpoint = new JournalCheckpoint(reader.ReadUInt64(), reader.ReadInt64());
        var created = new DateTimeOffset(reader.ReadInt64(), TimeSpan.Zero);
        var nodeCount = reader.ReadInt32();
        var nameCount = reader.ReadInt32();
        var textCount = reader.ReadInt32();
        // 在分配数组前用剩余文件长度交叉核对，损坏的计数不能触发任意大分配。
        if (nodeCount <= 0 || nameCount < nodeCount || textCount < nameCount ||
            nodeCount * 28L + nameCount * 17L + textCount * 2L != bodyLength - file.Position)
            throw new InvalidDataException("索引计数与文件长度不符");
        var nodes = new IndexNode[nodeCount];
        var names = new IndexName[nameCount];
        var text = new char[textCount];
        for (var i = 0; i < nodes.Length; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var id = reader.ReadUInt64(); var first = reader.ReadInt32(); var count = reader.ReadInt32();
            var directory = reader.ReadBoolean(); var hasUsn = reader.ReadBoolean(); var usn = reader.ReadInt64();
            nodes[i] = new(id, first, count, directory, hasUsn ? usn : null, reader.ReadUInt16());
        }
        for (var i = 0; i < names.Length; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            names[i] = new(reader.ReadInt32(), reader.ReadInt32(), reader.ReadInt32(), reader.ReadByte(), reader.ReadUInt32());
        }
        for (var i = 0; i < text.Length; i++) text[i] = (char)reader.ReadUInt16();
        return new(geometry, checkpoint, created, nodes, names, new(text));
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
}
