using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace QBox.Windows.Indexing;

public sealed class IndexBaseChangedException() : IOException("增量日志已切换到新基础索引");

// 每帧独立校验，读取时不消费未写完的尾帧；完整帧损坏时停止，不能跳过后续删除记录。
public static class IndexChangeLog
{
    private const int HeaderSize = 40, MaximumFrame = 64 * 1024 * 1024;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    public static string PathFor(string indexPath) => Path.ChangeExtension(indexPath, ".qbd");
    private static byte[] Header(VolumeIndex index)
    {
        var bytes = new byte[HeaderSize];
        "QBOXDLT1"u8.CopyTo(bytes);
        var identity = FormattableString.Invariant($"{index.Geometry.Root}|{index.Geometry.Serial}|{index.Checkpoint.JournalId}|{index.Checkpoint.NextUsn}|{index.CreatedAt.UtcTicks}");
        SHA256.HashData(Encoding.UTF8.GetBytes(identity)).CopyTo(bytes, 8);
        return bytes;
    }

    public static void Reset(string path, VolumeIndex index)
    {
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            { file.Write(Header(index)); file.Flush(true); }
            File.Move(temporary, path, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    public static IReadOnlyList<IndexChangeSet> Read(string path, VolumeIndex index, ref long cursor,
        bool repairTail = false, CancellationToken cancellationToken = default)
    {
        if (!File.Exists(path)) return [];
        using var file = new FileStream(path, FileMode.Open, repairTail ? FileAccess.ReadWrite : FileAccess.Read,
            repairTail ? FileShare.Read : FileShare.ReadWrite | FileShare.Delete);
        Span<byte> header = stackalloc byte[HeaderSize];
        if (file.Length < HeaderSize) throw new InvalidDataException("增量日志头不完整");
        file.ReadExactly(header);
        if (!header.SequenceEqual(Header(index))) throw new IndexBaseChangedException();
        var position = cursor == 0 ? HeaderSize : cursor;
        if (position < HeaderSize || position > file.Length) throw new IndexBaseChangedException();
        file.Position = position;
        var batches = new List<IndexChangeSet>();
        Span<byte> length = stackalloc byte[4];
        Span<byte> digest = stackalloc byte[32];
        while (file.Length - position >= 4)
        {
            cancellationToken.ThrowIfCancellationRequested();
            file.ReadExactly(length);
            var count = BinaryPrimitives.ReadInt32LittleEndian(length);
            if (count <= 0 || count > MaximumFrame) throw new InvalidDataException("增量日志帧长度无效");
            if (file.Length - file.Position < count + 32L) break;
            var payload = new byte[count];
            file.ReadExactly(payload); file.ReadExactly(digest);
            if (!SHA256.HashData(payload).AsSpan().SequenceEqual(digest)) throw new InvalidDataException("增量日志校验失败");
            var batch = JsonSerializer.Deserialize<IndexChangeSet>(payload, Json) ?? throw new InvalidDataException("增量日志帧为空");
            if (batch.Changes == null) throw new InvalidDataException("增量日志缺少变更集合");
            batches.Add(batch);
            position = file.Position;
        }
        if (repairTail && file.Length != position) { file.SetLength(position); file.Flush(true); }
        cursor = position;
        return batches;
    }

    public static void Append(string path, IndexChangeSet batch)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(batch, Json);
        if (bytes.Length > MaximumFrame) throw new InvalidDataException("单次增量过大，请重建基础索引");
        using var file = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
        file.Seek(0, SeekOrigin.End);
        Span<byte> length = stackalloc byte[4]; BinaryPrimitives.WriteInt32LittleEndian(length, bytes.Length);
        file.Write(length); file.Write(bytes); file.Write(SHA256.HashData(bytes)); file.Flush(true);
    }
}
