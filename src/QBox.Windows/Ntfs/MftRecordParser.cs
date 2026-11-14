using System.Buffers.Binary;

namespace QBox.Windows.Ntfs;

public sealed record MftName(FileReference Parent, string Name, byte Namespace, ulong StoredSize, uint Attributes);
public sealed record DataRun(ulong FirstVcn, ulong ClusterCount, long? FirstLcn);
public sealed record MftDataExtent(ulong LowestVcn, ulong HighestVcn, ulong? FileSize, IReadOnlyList<DataRun> Runs, ushort Instance = 0);
public sealed record MftAttribute(uint Type, string Name, ushort Instance, ulong LowestVcn);
public sealed record MftRecord(FileReference File, FileReference BaseFile, bool IsDirectory,
    IReadOnlyList<MftName> Names, IReadOnlyList<MftDataExtent> Data, bool HasAttributeList,
    IReadOnlyList<AttributeListEntry>? AttributeList = null, IReadOnlyList<MftDataExtent>? AttributeListData = null,
    ushort LinkCount = 1, long? LastUsn = null, IReadOnlyList<MftAttribute>? Attributes = null);

public enum MftRecordEncoding { RawProtected, FileSystemControl }

public static class MftRecordParser
{
    public static MftRecord? Parse(ReadOnlySpan<byte> raw, ulong recordNumber, MftRecordEncoding encoding = MftRecordEncoding.RawProtected)
    {
        if (recordNumber > 0x0000ffffffffffffUL) throw new ArgumentOutOfRangeException(nameof(recordNumber));
        if (raw.Length < 48 || raw.Length % 512 != 0) throw new InvalidDataException("MFT 记录尺寸无效");
        var signature = BinaryPrimitives.ReadUInt32LittleEndian(raw);
        if (signature == 0) return null;
        if (signature != 0x454c4946) throw new InvalidDataException("MFT FILE 签名无效");
        var flags = BinaryPrimitives.ReadUInt16LittleEndian(raw[22..]);
        if ((flags & 1) == 0) return null;
        var usaOffset = BinaryPrimitives.ReadUInt16LittleEndian(raw[4..]);
        var usaCount = BinaryPrimitives.ReadUInt16LittleEndian(raw[6..]);
        var attributeOffset = BinaryPrimitives.ReadUInt16LittleEndian(raw[20..]);
        var used = BinaryPrimitives.ReadUInt32LittleEndian(raw[24..]);
        if ((usaOffset & 1) != 0 || usaOffset < 42 || usaCount != raw.Length / 512 + 1 ||
            usaOffset + usaCount * 2 > Math.Min((int)attributeOffset, 510) || attributeOffset < 42 ||
            used > raw.Length || used < 4 || attributeOffset > used - 4)
            throw new InvalidDataException("MFT 更新序列或属性范围无效");

        var sequence = BinaryPrimitives.ReadUInt16LittleEndian(raw[usaOffset..]);
        var protectedTails = true;
        for (var i = 1; i < usaCount; i++)
        {
            var tail = BinaryPrimitives.ReadUInt16LittleEndian(raw[(i * 512 - 2)..]);
            protectedTails &= tail == sequence;
        }
        if (encoding == MftRecordEncoding.RawProtected && !protectedTails)
            throw new InvalidDataException("MFT 多扇区传输不完整，需要重新读取该记录");
        var restored = raw.ToArray();
        // 控制接口返回缓存中的逻辑记录，USA 可能仍是上次落盘值；只对原始盘面验证并恢复扇区尾部。
        if (encoding == MftRecordEncoding.RawProtected)
            for (var i = 1; i < usaCount; i++) raw.Slice(usaOffset + i * 2, 2).CopyTo(restored.AsSpan(i * 512 - 2, 2));
        var record = restored.AsSpan(0, (int)used);
        var names = new List<MftName>();
        var data = new List<MftDataExtent>();
        var hasAttributeList = false;
        var attributeList = new List<AttributeListEntry>();
        var attributeListData = new List<MftDataExtent>();
        var attributes = new List<MftAttribute>();
        long? lastUsn = null;
        var offset = (int)attributeOffset;
        var ended = false;
        while (offset <= record.Length - 4)
        {
            var type = BinaryPrimitives.ReadUInt32LittleEndian(record[offset..]);
            if (type == uint.MaxValue) { ended = true; break; }
            if (record.Length - offset < 16) throw new InvalidDataException("MFT 属性头截断");
            var length = BinaryPrimitives.ReadUInt32LittleEndian(record[(offset + 4)..]);
            var form = record[offset + 8];
            if (form > 1 || length < (form == 0 ? 24 : 64) || (length & 7) != 0 || length > record.Length - offset)
                throw new InvalidDataException("MFT 属性长度或形式无效");
            var attribute = record.Slice(offset, (int)length);
            var attributeNameLength = attribute[9] * 2;
            var attributeNameOffset = BinaryPrimitives.ReadUInt16LittleEndian(attribute[10..]);
            if (attributeNameLength != 0 && (attributeNameOffset < (form == 0 ? 24 : 64) ||
                (attributeNameOffset & 1) != 0 || attributeNameOffset > length || attributeNameLength > length - attributeNameOffset))
                throw new InvalidDataException("MFT 属性名称越界");
            attributes.Add(new(type, attributeNameLength == 0 ? "" :
                NtfsText.ReadUtf16(attribute.Slice(attributeNameOffset, attributeNameLength)),
                BinaryPrimitives.ReadUInt16LittleEndian(attribute[14..]),
                form == 0 ? 0 : BinaryPrimitives.ReadUInt64LittleEndian(attribute[16..])));
            if (type == 0x20) hasAttributeList = true;
            if (type == 0x20 && form == 0)
                attributeList.AddRange(MftAttributeListParser.Parse(ResidentValue(attribute)).Entries);
            if (type == 0x10 && form == 0)
            {
                var standard = ResidentValue(attribute);
                if (standard.Length >= 72) lastUsn = BinaryPrimitives.ReadInt64LittleEndian(standard[64..]);
            }
            if (type == 0x30)
            {
                if (form != 0) throw new InvalidDataException("FILE_NAME 属性不应为非常驻属性");
                var size = BinaryPrimitives.ReadUInt32LittleEndian(attribute[16..]);
                var start = BinaryPrimitives.ReadUInt16LittleEndian(attribute[20..]);
                if (start < 24 || start > length || size > length - start || size < 66)
                    throw new InvalidDataException("FILE_NAME 属性内容越界");
                var value = attribute.Slice(start, (int)size);
                var nameBytes = value[64] * 2;
                if (value[65] > 3 || nameBytes > value.Length - 66) throw new InvalidDataException("FILE_NAME 名称无效");
                names.Add(new(new(BinaryPrimitives.ReadUInt64LittleEndian(value)),
                    NtfsText.ReadUtf16(value.Slice(66, nameBytes)), value[65],
                    BinaryPrimitives.ReadUInt64LittleEndian(value[48..]), BinaryPrimitives.ReadUInt32LittleEndian(value[56..])));
            }
            if (type is 0x80 or 0x20 && form == 1 && attribute[9] == 0)
            {
                var runOffset = BinaryPrimitives.ReadUInt16LittleEndian(attribute[32..]);
                if (runOffset < 64 || runOffset >= length) throw new InvalidDataException("MFT 数据区段表越界");
                var lowest = BinaryPrimitives.ReadUInt64LittleEndian(attribute[16..]);
                var highest = BinaryPrimitives.ReadUInt64LittleEndian(attribute[24..]);
                // 非首段的 FileSize 字段未定义，不能作为完整流长度使用。
                ulong? fileSize = lowest == 0 ? BinaryPrimitives.ReadUInt64LittleEndian(attribute[48..]) : null;
                var runs = ParseRuns(attribute[runOffset..], lowest);
                if (runs.Count == 0 && !(lowest == 0 && highest == ulong.MaxValue && fileSize == 0))
                    throw new InvalidDataException("非空 MFT 数据流缺少区段");
                if (runs.Count != 0 && (highest == ulong.MaxValue || runs[^1].FirstVcn + runs[^1].ClusterCount - 1 != highest))
                    throw new InvalidDataException("MFT 区段表与 VCN 范围不符");
                var extent = new MftDataExtent(lowest, highest, fileSize, runs,
                    BinaryPrimitives.ReadUInt16LittleEndian(attribute[14..]));
                if (type == 0x80) data.Add(extent);
                else attributeListData.Add(extent);
            }
            offset += (int)length;
        }
        if (!ended) throw new InvalidDataException("MFT 属性列表缺少结束标记");
        var id = recordNumber | ((ulong)BinaryPrimitives.ReadUInt16LittleEndian(raw[16..]) << 48);
        return new(new(id), new(BinaryPrimitives.ReadUInt64LittleEndian(raw[32..])), (flags & 2) != 0,
            names.AsReadOnly(), data.AsReadOnly(), hasAttributeList, attributeList.AsReadOnly(),
            attributeListData.AsReadOnly(), BinaryPrimitives.ReadUInt16LittleEndian(raw[18..]), lastUsn,
            attributes.AsReadOnly());
    }

    public static IReadOnlyList<DataRun> ParseRuns(ReadOnlySpan<byte> bytes, ulong firstVcn = 0)
    {
        var runs = new List<DataRun>();
        var offset = 0;
        long lcn = 0;
        var vcn = firstVcn;
        while (offset < bytes.Length)
        {
            var header = bytes[offset++];
            if (header == 0) return runs.AsReadOnly();
            var lengthBytes = header & 15;
            var deltaBytes = header >> 4;
            if (lengthBytes is < 1 or > 8 || deltaBytes > 8 || lengthBytes + deltaBytes > bytes.Length - offset)
                throw new InvalidDataException("MFT 区段编码无效");
            var count = ReadUnsigned(bytes.Slice(offset, lengthBytes));
            offset += lengthBytes;
            if (count == 0 || count > ulong.MaxValue - vcn) throw new InvalidDataException("MFT 区段长度溢出");
            long? location = null;
            if (deltaBytes != 0)
            {
                var delta = ReadUnsigned(bytes.Slice(offset, deltaBytes));
                if (deltaBytes < 8 && (bytes[offset + deltaBytes - 1] & 0x80) != 0)
                    delta |= ulong.MaxValue << (deltaBytes * 8);
                try { lcn = checked(lcn + unchecked((long)delta)); }
                catch (OverflowException error) { throw new InvalidDataException("MFT 物理区段溢出", error); }
                if (lcn < 0) throw new InvalidDataException("MFT 物理区段位置为负");
                location = lcn;
            }
            offset += deltaBytes;
            runs.Add(new(vcn, count, location));
            vcn += count;
        }
        throw new InvalidDataException("MFT 区段表未终止");
    }

    private static ulong ReadUnsigned(ReadOnlySpan<byte> bytes)
    {
        ulong value = 0;
        for (var i = 0; i < bytes.Length; i++) value |= (ulong)bytes[i] << (i * 8);
        return value;
    }

    private static ReadOnlySpan<byte> ResidentValue(ReadOnlySpan<byte> attribute)
    {
        var length = BinaryPrimitives.ReadUInt32LittleEndian(attribute[16..]);
        var offset = BinaryPrimitives.ReadUInt16LittleEndian(attribute[20..]);
        if (offset < 24 || offset > attribute.Length || length > attribute.Length - offset)
            throw new InvalidDataException("常驻属性范围无效");
        return attribute.Slice(offset, (int)length);
    }
}
