using System.Buffers.Binary;

namespace QBox.Windows.Ntfs;

public sealed record AttributeListEntry(uint Type, ulong LowestVcn, FileReference Segment, ushort Instance, string Name);
public sealed record AttributeListResult(IReadOnlyList<AttributeListEntry> Entries, bool Complete);

public static class MftAttributeListParser
{
    public static AttributeListResult Parse(ReadOnlySpan<byte> value, bool allowIncompleteTail = false)
    {
        var entries = new List<AttributeListEntry>();
        var offset = 0;
        while (offset < value.Length)
        {
            var remaining = value[offset..];
            if (remaining.IndexOfAnyExcept((byte)0) < 0) return new(entries.AsReadOnly(), true);
            if (remaining.Length < 26)
            {
                if (allowIncompleteTail) return new(entries.AsReadOnly(), false);
                throw new InvalidDataException("属性列表条目被截断");
            }
            var type = BinaryPrimitives.ReadUInt32LittleEndian(remaining);
            var length = BinaryPrimitives.ReadUInt16LittleEndian(remaining[4..]);
            if (type == 0 || length < 26 || (length & 7) != 0) throw new InvalidDataException("属性列表条目长度或类型无效");
            if (length > remaining.Length)
            {
                if (allowIncompleteTail) return new(entries.AsReadOnly(), false);
                throw new InvalidDataException("属性列表条目内容被截断");
            }
            var nameBytes = remaining[6] * 2;
            var nameOffset = remaining[7];
            if (nameBytes != 0 && (nameOffset < 26 || (nameOffset & 1) != 0 || nameOffset > length || nameBytes > length - nameOffset))
                throw new InvalidDataException("属性列表名称范围无效");
            entries.Add(new(type, BinaryPrimitives.ReadUInt64LittleEndian(remaining[8..]),
                new(BinaryPrimitives.ReadUInt64LittleEndian(remaining[16..])),
                BinaryPrimitives.ReadUInt16LittleEndian(remaining[24..]),
                nameBytes == 0 ? "" : NtfsText.ReadUtf16(remaining.Slice(nameOffset, nameBytes))));
            offset += length;
        }
        return new(entries.AsReadOnly(), true);
    }
}
