using System.Buffers.Binary;

namespace QBox.Windows.Ntfs;

public readonly record struct FileReference(ulong Low, ulong High = 0)
{
    [System.Text.Json.Serialization.JsonIgnore]
    public ulong RecordNumber => Low & 0x0000ffffffffffffUL;
}

[Flags]
public enum UsnReason : uint
{
    DataOverwrite = 1, DataExtend = 2, DataTruncation = 4,
    FileCreate = 0x100, FileDelete = 0x200,
    RenameOldName = 0x1000, RenameNewName = 0x2000,
    BasicInfoChange = 0x8000, HardLinkChange = 0x10000, Close = 0x80000000
}

public sealed record UsnChange(FileReference File, FileReference Parent, long Usn, long FileTime,
    UsnReason Reason, uint Attributes, string Name);
public sealed record UsnBatch(long NextUsn, IReadOnlyList<UsnChange> Records);

public static class UsnJournalParser
{
    public static UsnBatch Parse(ReadOnlySpan<byte> buffer)
    {
        if (buffer.Length < 8) throw new InvalidDataException("USN 返回缓冲缺少下一位置");
        var nextUsn = BinaryPrimitives.ReadInt64LittleEndian(buffer);
        var records = new List<UsnChange>();
        var offset = 8;
        while (offset < buffer.Length)
        {
            var remaining = buffer[offset..];
            if (remaining.Length < 8) throw new InvalidDataException("USN 记录头被截断");
            var length = BinaryPrimitives.ReadUInt32LittleEndian(remaining);
            if (length < 8 || length > remaining.Length || (length & 7) != 0)
                throw new InvalidDataException("USN 记录长度无效");
            var record = remaining[..(int)length];
            var major = BinaryPrimitives.ReadUInt16LittleEndian(record[4..]);
            var headerSize = major switch
            {
                2 => 60,
                3 => 76,
                _ => throw new NotSupportedException($"未支持的 USN 主版本：{major}，不能跳过后继续推进日志位置")
            };
            if (record.Length < headerSize) throw new InvalidDataException("USN 记录短于版本头");
            var field = major == 2 ? 24 : 40;
            var file = new FileReference(BinaryPrimitives.ReadUInt64LittleEndian(record[8..]),
                major == 2 ? 0 : BinaryPrimitives.ReadUInt64LittleEndian(record[16..]));
            var parentOffset = major == 2 ? 16 : 24;
            var parent = new FileReference(BinaryPrimitives.ReadUInt64LittleEndian(record[parentOffset..]),
                major == 2 ? 0 : BinaryPrimitives.ReadUInt64LittleEndian(record[(parentOffset + 8)..]));
            var nameLength = BinaryPrimitives.ReadUInt16LittleEndian(record[(field + 32)..]);
            var nameOffset = BinaryPrimitives.ReadUInt16LittleEndian(record[(field + 34)..]);
            if ((nameLength & 1) != 0 || (nameOffset & 1) != 0 || nameOffset < headerSize ||
                nameOffset > record.Length || nameLength > record.Length - nameOffset)
                throw new InvalidDataException("USN 名称范围无效");
            records.Add(new(file, parent, BinaryPrimitives.ReadInt64LittleEndian(record[field..]),
                BinaryPrimitives.ReadInt64LittleEndian(record[(field + 8)..]),
                (UsnReason)BinaryPrimitives.ReadUInt32LittleEndian(record[(field + 16)..]),
                BinaryPrimitives.ReadUInt32LittleEndian(record[(field + 28)..]),
                NtfsText.ReadUtf16(record.Slice(nameOffset, nameLength))));
            offset += (int)length;
        }
        return new(nextUsn, records.AsReadOnly());
    }
}

internal static class NtfsText
{
    public static string ReadUtf16(ReadOnlySpan<byte> bytes)
    {
        // 按原始 UTF-16 代码单元保留名称，不能用替换字符悄悄改变磁盘上的名字。
        var chars = new char[bytes.Length / 2];
        for (var i = 0; i < chars.Length; i++) chars[i] = (char)BinaryPrimitives.ReadUInt16LittleEndian(bytes[(i * 2)..]);
        return new(chars);
    }
}
