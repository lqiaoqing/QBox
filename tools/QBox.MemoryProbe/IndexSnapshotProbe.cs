using System.Buffers.Binary;
using System.IO.MemoryMappedFiles;
using System.Security.Cryptography;
using System.Text.Json;
using QBox.Windows.Indexing;

internal static class IndexSnapshotProbe
{
    public static void Pack(string original, string output)
    {
        if (Path.GetFullPath(original).Equals(Path.GetFullPath(output), StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("探针只允许向独立路径转换，不覆盖原始快照");
        VolumeIndexStore.Save(output, VolumeIndexStore.Load(original).ShareNameText(force: true));
    }

    public static void Compare(string original, string packed, string output)
    {
        // 先过产品的摘要及完整结构校验，再以独立的原始字节视图逐条核对，避免只验证首屏结果。
        var left = VolumeIndexStore.Load(original);
        var right = VolumeIndexStore.Load(packed);
        using var a = new RawSnapshot(original);
        using var b = new RawSnapshot(packed);
        if (a.Files != b.Files || a.Names != b.Names || a.Header != b.Header ||
            !a.Bytes.Slice(12, a.Header - 16).SequenceEqual(b.Bytes.Slice(12, b.Header - 16)) ||
            !a.Bytes.Slice(a.Header, a.Files * 28).SequenceEqual(b.Bytes.Slice(b.Header, b.Files * 28)))
            throw new InvalidDataException("快照头、节点或文件身份不一致");
        using var digest = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        digest.AppendData(a.Bytes.Slice(12, a.Header - 16));
        digest.AppendData(a.Bytes.Slice(a.Header, a.Files * 28));
        for (var i = 0; i < a.Names; i++)
        {
            var first = a.Descriptor(i); var second = b.Descriptor(i);
            if (!first[..4].SequenceEqual(second[..4]) || !first[8..].SequenceEqual(second[8..]) ||
                !a.Text(first).SequenceEqual(b.Text(second))) throw new InvalidDataException($"名称 {i} 的身份、元数据或 UTF-16 不一致");
            digest.AppendData(first[..4]); digest.AppendData(first[8..]); digest.AppendData(a.Text(first));
        }
        File.WriteAllText(output, JsonSerializer.Serialize(new
        {
            filesCompared = a.Files, namesCompared = a.Names, allMetadataAndRawUtf16Equal = true,
            logicalSha256 = Convert.ToHexStringLower(digest.GetHashAndReset()),
            originalBytes = a.Length, packedBytes = b.Length, bytesSaved = a.Length - b.Length,
            originalFormat = a.Version, packedFormat = b.Version,
            basicIdentityAndCheckpointPreserved = true
        }, new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true }));
        GC.KeepAlive(left); GC.KeepAlive(right);
    }

    private unsafe sealed class RawSnapshot : IDisposable
    {
        private readonly MemoryMappedFile _mapping;
        private readonly MemoryMappedViewAccessor _view;
        private byte* _pointer;
        public int Length { get; }
        public int Header { get; }
        public int Files { get; }
        public int Names { get; }
        public int Version { get; }
        public ReadOnlySpan<byte> Bytes => new(_pointer, Length);
        public RawSnapshot(string path)
        {
            using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
            Length = checked((int)file.Length);
            using var reader = new BinaryReader(file);
            file.Position = 8; Version = reader.ReadInt32(); _ = reader.ReadString();
            // 卷根后 64 字节为几何信息、检查点和创建时间，随后三个 Int32 为数量。
            file.Position += 64;
            Files = reader.ReadInt32(); Names = reader.ReadInt32(); _ = reader.ReadInt32();
            Header = checked((int)file.Position);
            _mapping = MemoryMappedFile.CreateFromFile(file, null, 0, MemoryMappedFileAccess.Read, HandleInheritability.None, true);
            _view = _mapping.CreateViewAccessor(0, 0, MemoryMappedFileAccess.Read);
            _view.SafeMemoryMappedViewHandle.AcquirePointer(ref _pointer);
        }
        public ReadOnlySpan<byte> Descriptor(int index) => Bytes.Slice(Header + Files * 28 + index * 17, 17);
        public ReadOnlySpan<byte> Text(ReadOnlySpan<byte> name) => Bytes.Slice(Header + Files * 28 + Names * 17 +
            BinaryPrimitives.ReadInt32LittleEndian(name[4..]) * 2, BinaryPrimitives.ReadInt32LittleEndian(name[8..]) * 2);
        public void Dispose()
        {
            _view.SafeMemoryMappedViewHandle.ReleasePointer();
            _view.Dispose(); _mapping.Dispose();
        }
    }
}
