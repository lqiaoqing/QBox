using System.Buffers.Binary;
using QBox.Windows.Ntfs;
using static QBox.Checks.CheckSuite;

namespace QBox.Checks;

internal static class NtfsParserChecks
{
    public static void Run(CheckSuite checks)
    {
        checks.Run("USN V2 文件、父目录、原因与名称", () =>
        {
            var batch = UsnJournalParser.Parse(Batch(Usn(2, "开发资料😀", 0x0007000000000042, 0x0009000000000005,
                UsnReason.RenameNewName | UsnReason.Close)));
            Equal(900L, batch.NextUsn);
            Equal(1, batch.Records.Count);
            Equal(new FileReference(0x0007000000000042), batch.Records[0].File);
            Equal(new FileReference(0x0009000000000005), batch.Records[0].Parent);
            Equal("开发资料😀", batch.Records[0].Name);
            True(batch.Records[0].Reason.HasFlag(UsnReason.RenameNewName));
        });
        checks.Run("USN V3 保留完整 128 位身份", () =>
        {
            var record = Usn(3, "Tool", 42, 5, UsnReason.FileCreate);
            U64(record, 16, 0xfedcba9876543210);
            U64(record, 32, 0x123456789abcdef0);
            var parsed = UsnJournalParser.Parse(Batch(record)).Records[0];
            Equal(new FileReference(42, 0xfedcba9876543210), parsed.File);
            Equal(new FileReference(5, 0x123456789abcdef0), parsed.Parent);
        });
        checks.Run("USN 多记录及重命名前后顺序", () =>
        {
            var records = UsnJournalParser.Parse(Batch(Usn(2, "old", 42, 5, UsnReason.RenameOldName),
                Usn(2, "new", 42, 8, UsnReason.RenameNewName))).Records;
            Equal(2, records.Count);
            Equal("old", records[0].Name);
            Equal("new", records[1].Name);
            Equal(new FileReference(8), records[1].Parent);
        });
        checks.Run("USN 保留原始 UTF-16 名称", () =>
            Equal("x\ud800y", UsnJournalParser.Parse(Batch(Usn(2, "x\ud800y", 42, 5, UsnReason.Close))).Records[0].Name));
        checks.Run("USN 空变更批次", () => Equal(0, UsnJournalParser.Parse(Batch()).Records.Count));
        checks.Run("USN 截断缓冲拒绝解析", () =>
        {
            Throws<InvalidDataException>(() => UsnJournalParser.Parse(new byte[7]));
            var bytes = Batch(Usn(2, "ab", 42, 5, UsnReason.Close));
            Throws<InvalidDataException>(() => UsnJournalParser.Parse(bytes.AsSpan(0, bytes.Length - 1)));
        });
        checks.Run("USN 非法名称边界拒绝推进", () =>
        {
            var bytes = Usn(2, "ab", 42, 5, UsnReason.Close);
            U16(bytes, 58, 0xfffe);
            Throws<InvalidDataException>(() => UsnJournalParser.Parse(Batch(bytes)));
            U16(bytes, 58, 60);
            U16(bytes, 56, 3);
            Throws<InvalidDataException>(() => UsnJournalParser.Parse(Batch(bytes)));
        });
        checks.Run("USN 未支持版本不静默跳过", () =>
        {
            var bytes = Usn(2, "ab", 42, 5, UsnReason.Close);
            U16(bytes, 4, 4);
            Throws<NotSupportedException>(() => UsnJournalParser.Parse(Batch(bytes)));
        });
        checks.Run("MFT 扇区恢复及跨扇区名称", () =>
        {
            const string name = "跨扇区名称-abcdefghijklmnopqrstuvwxyz😀";
            var raw = Mft(name, "ANOTHER~1");
            var before = raw.ToArray();
            var parsed = MftRecordParser.Parse(raw, 42)!;
            Equal(new FileReference(0x000700000000002a), parsed.File);
            Equal(2, parsed.Names.Count);
            Equal(name, parsed.Names[0].Name);
            Equal("ANOTHER~1", parsed.Names[1].Name);
            Equal((byte)2, parsed.Names[1].Namespace);
            True(raw.SequenceEqual(before));
        });
        checks.Run("MFT 破损传输拒绝使用", () =>
        {
            var raw = Mft("test");
            raw[1022] ^= 1;
            Throws<InvalidDataException>(() => MftRecordParser.Parse(raw, 42));
        });
        checks.Run("MFT 已释放与未初始化记录", () =>
        {
            Equal<MftRecord?>(null, MftRecordParser.Parse(new byte[1024], 42));
            var bytes = Mft("test");
            U16(bytes, 22, 0);
            Equal<MftRecord?>(null, MftRecordParser.Parse(bytes, 42));
        });
        checks.Run("MFT 目录标志与扩展记录身份", () =>
        {
            var bytes = Mft("test");
            U16(bytes, 22, 3);
            U64(bytes, 32, 0x0002000000000011);
            var parsed = MftRecordParser.Parse(bytes, 42)!;
            True(parsed.IsDirectory);
            Equal(new FileReference(0x0002000000000011), parsed.BaseFile);
        });
        checks.Run("MFT 属性越界不产生部分索引", () =>
        {
            var bytes = Mft("test");
            U32(bytes, 404, uint.MaxValue);
            Throws<InvalidDataException>(() => MftRecordParser.Parse(bytes, 42));
        });
        checks.Run("MFT 区段负偏移与稀疏区段", () =>
        {
            var runs = MftRecordParser.ParseRuns([0x11, 3, 100, 0x11, 2, 0xec, 0x01, 4, 0x11, 1, 5, 0], 10);
            Equal(4, runs.Count);
            Equal(new DataRun(10, 3, 100), runs[0]);
            Equal(new DataRun(13, 2, 80), runs[1]);
            Equal(new DataRun(15, 4, null), runs[2]);
            Equal(new DataRun(19, 1, 85), runs[3]);
        });
        checks.Run("MFT 区段非法编码与溢出", () =>
        {
            Throws<InvalidDataException>(() => MftRecordParser.ParseRuns([0x11, 1]));
            Throws<InvalidDataException>(() => MftRecordParser.ParseRuns([0x11, 1, 0xff, 0]));
            Throws<InvalidDataException>(() => MftRecordParser.ParseRuns([0x11, 0, 1, 0]));
            Throws<InvalidDataException>(() => MftRecordParser.ParseRuns([0x11, 1, 1]));
            Throws<InvalidDataException>(() => MftRecordParser.ParseRuns([0x11, 2, 1, 0], ulong.MaxValue));
        });
        checks.Run("MFT 非常驻流区段及长度校验", () =>
        {
            var bytes = MftData(out var offset);
            var parsed = MftRecordParser.Parse(bytes, 42)!;
            Equal<ulong?>(12288, parsed.Data[0].FileSize);
            Equal(new DataRun(0, 3, 100), parsed.Data[0].Runs[0]);
            U64(bytes, offset + 24, 1);
            Throws<InvalidDataException>(() => MftRecordParser.Parse(bytes, 42));
        });
        checks.Run("MFT 空数据流及外部属性标记", () =>
        {
            var bytes = MftData(out var offset);
            bytes[offset + 64] = 0;
            Throws<InvalidDataException>(() => MftRecordParser.Parse(bytes, 42));
            U64(bytes, offset + 48, 0);
            U64(bytes, offset + 24, ulong.MaxValue);
            Equal(0, MftRecordParser.Parse(bytes, 42)!.Data[0].Runs.Count);
            U32(bytes, offset, 0x20);
            True(MftRecordParser.Parse(bytes, 42)!.HasAttributeList);
        });
        checks.Run("USN 日志连续性与初建后追赶", () =>
        {
            var bounds = new JournalBounds(7, 100, 150, 300);
            Equal(JournalResume.ReadChanges, JournalContinuity.Check(new(7, 200), bounds));
            Equal(JournalResume.CaughtUp, JournalContinuity.Check(new(7, 300), bounds));
            Equal(JournalResume.RebuildRequired, JournalContinuity.Check(new(8, 200), bounds));
            Equal(JournalResume.RebuildRequired, JournalContinuity.Check(new(7, 149), bounds));
            Equal(JournalResume.RebuildRequired, JournalContinuity.Check(new(7, 301), bounds));
        });

        var random = new Random(2211);
        for (var i = 0; i < 200; i++)
        {
            var mft = Mft("mutation");
            var usn = Batch(Usn(2, "mutation", 42, 5, UsnReason.FileCreate));
            for (var change = 0; change < 4; change++)
            {
                mft[random.Next(mft.Length)] = (byte)random.Next(256);
                usn[random.Next(8, usn.Length)] = (byte)random.Next(256);
            }
            checks.Run($"磁盘记录变异边界 {i}", () =>
            {
                try { MftRecordParser.Parse(mft, 42); }
                catch (InvalidDataException) { }
                try { UsnJournalParser.Parse(usn); }
                catch (Exception error) when (error is InvalidDataException or NotSupportedException) { }
            });
        }
    }

    private static byte[] Batch(params byte[][] records)
    {
        var bytes = new byte[8 + records.Sum(r => r.Length)];
        U64(bytes, 0, 900);
        var offset = 8;
        foreach (var record in records) { record.CopyTo(bytes, offset); offset += record.Length; }
        return bytes;
    }

    private static byte[] Usn(ushort major, string name, ulong id, ulong parent, UsnReason reason)
    {
        var header = major == 2 ? 60 : 76;
        var bytes = new byte[(header + name.Length * 2 + 7) & ~7];
        var field = major == 2 ? 24 : 40;
        U32(bytes, 0, (uint)bytes.Length);
        U16(bytes, 4, major);
        U64(bytes, 8, id);
        U64(bytes, major == 2 ? 16 : 24, parent);
        U64(bytes, field, 100);
        U64(bytes, field + 8, 133000000000000000);
        U32(bytes, field + 16, (uint)reason);
        U32(bytes, field + 28, 0x20);
        U16(bytes, field + 32, (ushort)(name.Length * 2));
        U16(bytes, field + 34, (ushort)header);
        for (var i = 0; i < name.Length; i++) U16(bytes, header + i * 2, name[i]);
        return bytes;
    }

    internal static byte[] Mft(params string[] names)
    {
        var bytes = new byte[1024];
        U32(bytes, 0, 0x454c4946);
        U16(bytes, 4, 48);
        U16(bytes, 6, 3);
        U16(bytes, 16, 7);
        U16(bytes, 20, 400);
        U16(bytes, 22, 1);
        U32(bytes, 28, 1024);
        var offset = 400;
        for (var index = 0; index < names.Length; index++)
        {
            var name = names[index];
            var valueLength = 66 + name.Length * 2;
            var length = (24 + valueLength + 7) & ~7;
            U32(bytes, offset, 0x30);
            U32(bytes, offset + 4, (uint)length);
            U32(bytes, offset + 16, (uint)valueLength);
            U16(bytes, offset + 20, 24);
            var value = offset + 24;
            U64(bytes, value, 0x0009000000000005);
            U64(bytes, value + 48, 1234);
            U32(bytes, value + 56, 0x20);
            bytes[value + 64] = (byte)name.Length;
            bytes[value + 65] = index == 0 ? (byte)1 : (byte)2;
            for (var i = 0; i < name.Length; i++) U16(bytes, value + 66 + i * 2, name[i]);
            offset += length;
        }
        U32(bytes, offset, uint.MaxValue);
        U32(bytes, 24, (uint)(offset + 4));
        U16(bytes, 48, 0xa55a);
        U16(bytes, 50, BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(510)));
        U16(bytes, 52, BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(1022)));
        U16(bytes, 510, 0xa55a);
        U16(bytes, 1022, 0xa55a);
        return bytes;
    }

    internal static byte[] MftData(out int offset)
    {
        var bytes = Mft("test");
        U16(bytes, 510, BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(50)));
        U16(bytes, 1022, BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(52)));
        offset = (int)BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(24)) - 4;
        U32(bytes, offset, 0x80);
        U32(bytes, offset + 4, 72);
        bytes[offset + 8] = 1;
        U64(bytes, offset + 16, 0);
        U64(bytes, offset + 24, 2);
        U16(bytes, offset + 32, 64);
        U64(bytes, offset + 48, 12288);
        bytes[offset + 64] = 0x11;
        bytes[offset + 65] = 3;
        bytes[offset + 66] = 100;
        U32(bytes, offset + 72, uint.MaxValue);
        U32(bytes, 24, (uint)(offset + 76));
        U16(bytes, 50, BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(510)));
        U16(bytes, 52, BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(1022)));
        U16(bytes, 510, 0xa55a);
        U16(bytes, 1022, 0xa55a);
        return bytes;
    }

    private static void U16(byte[] bytes, int offset, ushort value) => BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(offset), value);
    private static void U32(byte[] bytes, int offset, uint value) => BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(offset), value);
    private static void U64(byte[] bytes, int offset, ulong value) => BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(offset), value);
}
