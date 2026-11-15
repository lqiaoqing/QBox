using System.Buffers.Binary;
using QBox.Windows.Indexing;
using QBox.Windows.Ntfs;
using QBox.Core.History;
using QBox.Core.Search;
using static QBox.Checks.CheckSuite;

namespace QBox.Checks;

internal static class NtfsIndexChecks
{
    private static readonly FileReference Root = Id(5, 9);
    private static readonly NtfsGeometry Geometry = new(@"X:\", 0x1122334455667788, 512, 4096, 1024, 65536, 100, 3, 1);

    public static void Run(CheckSuite checks)
    {
        RunLive(checks);
        checks.Run("USN V0 原生请求 ABI 布局", () =>
        {
            var input = NtfsControlBuffers.ReadJournal(new(0xfedcba9876543210, 0x1020304050607080));
            Equal(40, input.Length);
            Equal(0x1020304050607080L, BinaryPrimitives.ReadInt64LittleEndian(input));
            Equal(uint.MaxValue, BinaryPrimitives.ReadUInt32LittleEndian(input.AsSpan(8)));
            True(input.AsSpan(12, 20).IndexOfAnyExcept((byte)0) < 0);
            Equal(0xfedcba9876543210UL, BinaryPrimitives.ReadUInt64LittleEndian(input.AsSpan(32)));
        });
        checks.Run("控制接口逻辑记录不能再次用旧 USA 覆盖内容", () =>
        {
            var raw = Raw("cross-sector-filename-abcdefghijklmnopqrstuvwxyz");
            Restore(raw);
            Equal("cross-sector-filename-abcdefghijklmnopqrstuvwxyz", MftRecordParser.Parse(raw, 20, MftRecordEncoding.FileSystemControl)!.Names[0].Name);
            Throws<InvalidDataException>(() => MftRecordParser.Parse(raw, 20));
            U16(raw, 50, 0x0231);
            U16(raw, 52, 0x4321);
            Equal("cross-sector-filename-abcdefghijklmnopqrstuvwxyz", MftRecordParser.Parse(raw, 20, MftRecordEncoding.FileSystemControl)!.Names[0].Name);
            Throws<InvalidDataException>(() => MftRecordParser.Parse(raw, 20));
        });
        checks.Run("属性列表截断必须显式标记未完成", () =>
        {
            var entry = AttributeEntry(0x30, Id(20), 3);
            Equal(Id(20), MftAttributeListParser.Parse(entry).Entries[0].Segment);
            True(!MftAttributeListParser.Parse(entry.AsSpan(0, 30), true).Complete);
            Throws<InvalidDataException>(() => MftAttributeListParser.Parse(entry.AsSpan(0, 30)));
            U16(entry, 4, 27);
            Throws<InvalidDataException>(() => MftAttributeListParser.Parse(entry));
        });
        checks.Run("跨记录名称与常驻数据属性均按实例验证", () =>
        {
            using var volume = new FakeVolume();
            var id = Id(20);
            var extension = Id(25);
            var root = Record(20, "base") with
            {
                HasAttributeList = true,
                AttributeList = [new(0x30, 0, extension, 3, ""), new(0x80, 0, id, 2, "")],
                Attributes = [new(0x30, "", 0, 0), new(0x80, "", 2, 0)]
            };
            volume.Current[20] = root;
            volume.Current[25] = Record(25, "linked") with { BaseFile = id, Attributes = [new(0x30, "", 3, 0)] };
            var resolved = new NtfsFileResolver(volume).Resolve(id)!;
            Equal(2, resolved.Names.Count);
            Equal(0, resolved.Data.Count);
            volume.Current[25] = volume.Current[25] with { Attributes = [] };
            Throws<InvalidDataException>(() => new NtfsFileResolver(volume).Resolve(id));
        });
        checks.Run("属性扩展段的序列号和基段身份不可复用", () =>
        {
            using var volume = new FakeVolume();
            volume.Current[20] = Record(20, "base") with
            {
                HasAttributeList = true, AttributeList = [new(0x30, 0, Id(25), 0, "")]
            };
            volume.Current[25] = Record(25, "link") with { File = Id(25, 8), BaseFile = Id(20) };
            Throws<InvalidDataException>(() => new NtfsFileResolver(volume).Resolve(Id(20)));
            volume.Current[25] = volume.Current[25] with { File = Id(25), BaseFile = Id(21) };
            Throws<InvalidDataException>(() => new NtfsFileResolver(volume).Resolve(Id(20)));
            Equal<ResolvedMftFile?>(null, new NtfsFileResolver(volume).Resolve(Id(20, 8)));
        });
        checks.Run("非常驻属性列表逐段扩充映射后解析", () =>
        {
            using var volume = new FakeVolume(Geometry with { ClusterSize = 512 });
            var entries = Enumerable.Range(0, 32).Select(_ => AttributeEntry(0x10, Id(20), 2)).ToArray();
            entries[0] = AttributeEntry(0x20, Id(20), 0);
            entries[1] = AttributeEntry(0x20, Id(25), 1, 1);
            var content = entries.SelectMany(b => b).ToArray();
            content.AsSpan(0, 512).CopyTo(volume.Disk.AsSpan(2 * 512));
            content.AsSpan(512).CopyTo(volume.Disk.AsSpan(4 * 512));
            volume.Current[20] = Record(20, "base") with
            {
                HasAttributeList = true, AttributeListData = [new(0, 0, 1024, [new(0, 1, 2)])],
                Attributes = [new(0x20, "", 0, 0), new(0x10, "", 2, 0)]
            };
            volume.Current[25] = Record(25, "extension") with
            {
                BaseFile = Id(20), AttributeListData = [new(1, 1, null, [new(1, 1, 4)], 1)],
                Attributes = [new(0x20, "", 1, 1)]
            };
            Equal(2, new NtfsFileResolver(volume).Resolve(Id(20))!.Segments.Count);
        });
        checks.Run("碎片化元数据流跨物理区段读取", () =>
        {
            using var volume = new FakeVolume();
            for (var i = 0; i < volume.Disk.Length; i++) volume.Disk[i] = (byte)(i / 512);
            var stream = new MappedNtfsStream(volume, 512,
                [new(0, 1, 1400, [new(0, 2, 2)]), new(2, 2, null, [new(2, 1, 8)])]);
            var bytes = new byte[400];
            stream.ReadExactly(900, bytes);
            True(bytes.AsSpan(0, 124).IndexOfAnyExcept((byte)3) < 0);
            True(bytes.AsSpan(124).IndexOfAnyExcept((byte)8) < 0);
            Throws<EndOfStreamException>(() => stream.ReadExactly(1399, new byte[2]));
        });
        checks.Run("元数据映射空洞、重叠、稀疏与缺段不能静默补零", () =>
        {
            using var volume = new FakeVolume();
            MftDataExtent[] hole = [new(0, 0, 1400, [new(0, 1, 2)]), new(2, 2, null, [new(2, 1, 8)])];
            Throws<InvalidDataException>(() => new MappedNtfsStream(volume, 512, hole));
            Equal(512UL, new MappedNtfsStream(volume, 512, hole, allowPartialMapping: true).Length);
            Throws<InvalidDataException>(() => new MappedNtfsStream(volume, 512, [new(0, 0, 512, [new(0, 1, null)])]));
            Throws<InvalidDataException>(() => new MappedNtfsStream(volume, 512,
                [new(0, 1, 1024, [new(0, 2, 2)]), new(1, 1, null, [new(1, 1, 4)])]));
        });
        checks.Run("原始 MFT 初建经缓存视图纠正旧名并剔除已删记录", () =>
        {
            using var volume = new FakeVolume();
            volume.PutRaw(20, Raw("oldname"));
            volume.PutRaw(24, Raw("deleted"));
            volume.Current[20] = Record(20, "currentname");
            volume.Current.Remove(24);
            var index = new NtfsIndexBuilder(volume).Build();
            Equal(1, index.Candidates("current").Count());
            Equal(0, index.Candidates("oldname").Count());
            Equal(0, index.Candidates("deleted").Count());
            Equal(@"X:\currentname", index.Candidates("current").Single().Target);
            Equal(new JournalCheckpoint(7, 100), index.Checkpoint);
        });
        checks.Run("初建扫描后必须追齐期间产生的 USN", () =>
        {
            using var volume = new FakeVolume();
            volume.Current[20] = Record(20, "created");
            volume.Bounds = call => new(7, 1, 1, call == 1 ? 100 : 200);
            volume.Batches[100] = new(200, [Change(20, 120, UsnReason.FileCreate)]);
            var index = new NtfsIndexBuilder(volume).Build();
            Equal(new JournalCheckpoint(7, 200), index.Checkpoint);
            Equal(1, index.Candidates("created").Count());
            Equal(1, volume.JournalReads);
        });
        checks.Run("USN 重命名移动、新建和删除更新整条路径", () =>
        {
            using var volume = new FakeVolume();
            var saved = Snapshot(File(20, "old"), File(21, "folder", directory: true), File(23, "deleted"));
            volume.Current[20] = Record(20, "moved", Id(21));
            volume.Current[21] = Record(21, "folder", directory: true);
            volume.Current[22] = Record(22, "created");
            volume.Bounds = _ => new(7, 1, 1, 200);
            volume.Batches[100] = new(200, [Change(20, 110, UsnReason.RenameOldName), Change(20, 120, UsnReason.RenameNewName),
                Change(22, 130, UsnReason.FileCreate), Change(23, 140, UsnReason.FileDelete)]);
            var updated = new NtfsIndexBuilder(volume).Synchronize(saved);
            Equal(@"X:\folder\moved", updated.Candidates("moved").Single().Target);
            Equal(1, updated.Candidates("created").Count());
            Equal(0, updated.Candidates("deleted").Count());
            Equal("moved", updated.ExportFiles().Single(f => f.File == Id(20)).Names.Single().Name);
            Equal("old", saved.ExportFiles().Single(f => f.File == Id(20)).Names.Single().Name);
            Equal(new JournalCheckpoint(7, 100), saved.Checkpoint);
        });
        checks.Run("目录移动后无需逐个改写子文件路径", () =>
        {
            using var volume = new FakeVolume();
            var saved = Snapshot(File(20, "before", directory: true), File(21, "child", Id(20)));
            volume.Current[20] = Record(20, "after", directory: true);
            volume.Bounds = _ => new(7, 1, 1, 200);
            volume.Batches[100] = new(200, [Change(20, 120, UsnReason.RenameNewName)]);
            var updated = new NtfsIndexBuilder(volume).Synchronize(saved);
            Equal(@"X:\after\child", updated.Candidates("child").Single().Target);
        });
        checks.Run("缓存读到新父目录时递归补齐父关系", () =>
        {
            using var volume = new FakeVolume();
            var saved = Snapshot(File(20, "before"));
            volume.Current[20] = Record(20, "after", Id(21));
            volume.Current[21] = Record(21, "new-parent", Id(22), true);
            volume.Current[22] = Record(22, "grandparent", directory: true);
            volume.Bounds = _ => new(7, 1, 1, 200);
            volume.Batches[100] = new(200, [Change(20, 120, UsnReason.RenameNewName)]);
            var updated = new NtfsIndexBuilder(volume).Synchronize(saved);
            Equal(@"X:\grandparent\new-parent\after", updated.Candidates("after").Single().Target);
        });
        checks.Run("父身份已消失时仅移除无路径名称，保留有效硬链接", () =>
        {
            using var volume = new FakeVolume();
            volume.Current[20] = Record(20, "reachable") with { Names = [Name("reachable"), Name("unlinked", Id(50))] };
            volume.Bounds = _ => new(7, 1, 1, 200);
            volume.Batches[100] = new(200, [Change(20, 120, UsnReason.HardLinkChange)]);
            var updated = new NtfsIndexBuilder(volume).Synchronize(Snapshot(File(20, "before")));
            Equal(1, updated.Candidates("reachable").Count());
            Equal(0, updated.Candidates("unlinked").Count());
        });
        checks.Run("无路径目录的子项逐层确认后移除", () =>
        {
            using var volume = new FakeVolume();
            volume.Current[20] = Record(20, "unlinked-parent", Id(50), true);
            volume.Current[21] = Record(21, "child", Id(20));
            volume.Bounds = _ => new(7, 1, 1, 200);
            volume.Batches[100] = new(200, [Change(20, 120, UsnReason.RenameNewName)]);
            var updated = new NtfsIndexBuilder(volume).Synchronize(Snapshot(File(20, "before", directory: true), File(21, "child", Id(20))));
            Equal(1, updated.FileCount);
        });
        checks.Run("硬链接变化读取完整名称集合并保留各路径", () =>
        {
            using var volume = new FakeVolume();
            var saved = Snapshot(File(20, "first"), File(21, "folder", directory: true));
            volume.Current[20] = Record(20, "first") with
            {
                LinkCount = 2, Names = [Name("first"), Name("second", Id(21))]
            };
            volume.Bounds = _ => new(7, 1, 1, 200);
            volume.Batches[100] = new(200, [Change(20, 120, UsnReason.HardLinkChange | UsnReason.Close)]);
            var updated = new NtfsIndexBuilder(volume).Synchronize(saved);
            Equal(@"X:\first", updated.Candidates("first").Single().Target);
            Equal(@"X:\folder\second", updated.Candidates("second").Single().Target);
            True(updated.Candidates("first").Single().Id != updated.Candidates("second").Single().Id);
        });
        checks.Run("MFT 编号复用后旧序列号不能删除新文件", () =>
        {
            using var volume = new FakeVolume();
            var saved = Snapshot(File(20, "old"));
            volume.Current[20] = Record(20, "new") with { File = Id(20, 8) };
            volume.Bounds = _ => new(7, 1, 1, 200);
            volume.Batches[100] = new(200, [Change(20, 110, UsnReason.FileCreate, 8), Change(20, 120, UsnReason.FileDelete)]);
            var updated = new NtfsIndexBuilder(volume).Synchronize(saved);
            Equal(0, updated.Candidates("old").Count());
            True(updated.Candidates("new").Single().Id.Contains("0008000000000014", StringComparison.Ordinal));
        });
        checks.Run("日志断档、重置及读取未推进均拒绝同步", () =>
        {
            using var volume = new FakeVolume();
            var saved = Snapshot(File(20, "old"));
            volume.Bounds = _ => new(8, 1, 1, 200);
            Throws<JournalGapException>(() => new NtfsIndexBuilder(volume).Synchronize(saved));
            Equal(0, volume.JournalReads);
            volume.Bounds = _ => new(7, 101, 101, 200);
            Throws<JournalGapException>(() => new NtfsIndexBuilder(volume).Synchronize(saved));
            volume.Bounds = _ => new(7, 1, 1, 200);
            volume.Batches[100] = new(100, []);
            Throws<InvalidDataException>(() => new NtfsIndexBuilder(volume).Synchronize(saved));
            volume.Batches[100] = new(200, [Change(20, 200, UsnReason.Close)]);
            Throws<InvalidDataException>(() => new NtfsIndexBuilder(volume).Synchronize(saved));
        });
        checks.Run("同步完成前再次检查日志身份", () =>
        {
            using var volume = new FakeVolume();
            volume.Bounds = call => new(call >= 3 ? 8UL : 7UL, 1, 1, 200);
            volume.Batches[100] = new(200, []);
            Throws<JournalGapException>(() => new NtfsIndexBuilder(volume).Synchronize(Snapshot()));
        });
        checks.Run("父目录缺失、非目录、目录环与重复物理记录不能发布", () =>
        {
            Throws<InvalidDataException>(() => Snapshot(File(20, "orphan", Id(21))));
            Throws<InvalidDataException>(() => Snapshot(File(20, "child", Id(21)), File(21, "parent")));
            Throws<InvalidDataException>(() => Snapshot(File(20, "a", Id(21), true), File(21, "b", Id(20), true)));
            Throws<InvalidDataException>(() => Snapshot(File(20, "a"), File(20, "b") with { File = Id(20, 8) }));
        });
        checks.Run("索引原子保存、重启读取及 UTF-16 原样保留", () => WithDirectory(directory =>
        {
            var path = Path.Combine(directory, "volume.qbi");
            var index = Snapshot(File(20, "x\ud800y"), File(21, "folder", directory: true), File(22, "child", Id(21)));
            VolumeIndexStore.Save(path, index);
            var restored = VolumeIndexStore.Load(path);
            Equal(index.Checkpoint, restored.Checkpoint);
            Equal(index.CreatedAt, restored.CreatedAt);
            Equal("x\ud800y", restored.ExportFiles().Single(f => f.File == Id(20)).Names[0].Name);
            Equal(@"X:\folder\child", restored.Candidates("child").Single().Target);
        }));
        checks.Run("映射快照跨原子替换仍保持旧查询完整", () => WithDirectory(directory =>
        {
            var path = Path.Combine(directory, "volume.qbi");
            VolumeIndexStore.Save(path, Snapshot(File(20, "old资料😀")));
            var old = VolumeIndexStore.Load(path);
            var collector = new SearchCollector("old");
            old.SearchInto(collector, new SelectionHistory().ForQuery("old"));
            VolumeIndexStore.Save(path, Snapshot(File(20, "new资料😀")));
            var next = VolumeIndexStore.Load(path);
            GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
            Equal("old资料😀", collector.Finish().Items.Single().Candidate.Name);
            Equal(@"X:\old资料😀", old.Candidates("old").Single().Target);
            Equal(@"X:\new资料😀", next.Candidates("new").Single().Target);
            Equal(0, next.Candidates("old").Count());
        }));
        checks.Run("映射快照再保存保持当前格式字节与孤立代理项", () => WithDirectory(directory =>
        {
            var path = Path.Combine(directory, "volume.qbi");
            var copy = Path.Combine(directory, "copy.qbi");
            VolumeIndexStore.Save(path, Snapshot(File(20, "x\ud800y"), File(21, "𠀀资料😀")));
            VolumeIndexStore.Save(copy, VolumeIndexStore.Load(path));
            True(System.IO.File.ReadAllBytes(path).SequenceEqual(System.IO.File.ReadAllBytes(copy)));
            Equal("𠀀资料😀", VolumeIndexStore.Load(copy).Candidates("资料").Single().Name);
        }));
        checks.Run("旧 v1 转换共享名称保留全结构、基础身份、增量和旧映射", () => WithDirectory(directory =>
        {
            var path = Path.Combine(directory, "volume.qbi");
            var index = Snapshot(File(20, "folder-a", directory: true), File(21, "shared资料\ud800", Id(20)),
                File(22, "folder-b", directory: true), File(23, "shared资料\ud800", Id(22)), File(24, "Shared资料\ud800"));
            System.IO.File.WriteAllBytes(path, LegacyBytes(index, directory));
            var legacy = VolumeIndexStore.Load(path);
            var before = legacy.ExportFiles().ToArray();
            var old = new SearchCollector("shared"); legacy.SearchInto(old, new SelectionHistory().ForQuery("shared"));
            var log = IndexChangeLog.PathFor(path);
            IndexChangeLog.Reset(log, legacy);
            IndexChangeLog.Append(log, new(new(7, 110), [new(Id(21), File(21, "renamed资料", Id(20)))]));
            var pooled = legacy.ShareNameText();
            Equal(legacy.Geometry, pooled.Geometry); Equal(legacy.Checkpoint, pooled.Checkpoint); Equal(legacy.CreatedAt, pooled.CreatedAt);
            VolumeIndexStore.Save(path, pooled);
            var restored = VolumeIndexStore.Load(path);
            var after = restored.ExportFiles().ToArray();
            Equal(before.Length, after.Length);
            for (var i = 0; i < before.Length; i++)
            {
                Equal(before[i] with { Names = [] }, after[i] with { Names = [] });
                True(before[i].Names.SequenceEqual(after[i].Names));
            }
            Equal(3, old.Finish().TotalMatches);
            long cursor = 0;
            var live = new LiveVolumeIndex(restored).Apply(IndexChangeLog.Read(log, restored, ref cursor));
            Equal("renamed资料", live.FindFile(Id(21))!.Names[0].Name);
            Equal(2, BinaryPrimitives.ReadInt32LittleEndian(System.IO.File.ReadAllBytes(path).AsSpan(8)));
            True(new FileInfo(path).Length < LegacyBytes(index, directory).Length);
        }));
        foreach (var kind in new[] { "中间偏移", "跨片段长度", "未引用文本", "非根点目录" })
            checks.Run("共享名称仍拒绝有正确摘要的非法引用：" + kind, () => WithDirectory(directory =>
            {
                var path = Path.Combine(directory, "bad.qbi");
                VolumeIndexStore.Save(path, Snapshot(File(20, "alpha"), File(21, "beta"), File(22, "alpha")));
                var bytes = System.IO.File.ReadAllBytes(path);
                var namesAt = 92 + 4 * 28;
                var last = namesAt + 3 * 17;
                if (kind == "中间偏移") BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(last + 4), 2);
                if (kind == "跨片段长度") BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(last + 8), 9);
                if (kind == "未引用文本")
                {
                    BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(namesAt + 2 * 17 + 4), 1);
                    BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(namesAt + 2 * 17 + 8), 5);
                }
                if (kind == "非根点目录")
                {
                    BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(last + 4), 0);
                    BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(last + 8), 1);
                }
                System.Security.Cryptography.SHA256.HashData(bytes.AsSpan(0, bytes.Length - 32)).CopyTo(bytes, bytes.Length - 32);
                System.IO.File.WriteAllBytes(path, bytes);
                Throws<InvalidDataException>(() => VolumeIndexStore.Load(path));
            }));
        checks.Run("长名称共享跨越多个校验位图字且保留独立身份", () => WithDirectory(directory =>
        {
            var longName = new string('a', 300) + "资料\ud800";
            var index = Snapshot(File(20, longName), File(21, new string('b', 200)), File(22, longName));
            var path = Path.Combine(directory, "long.qbi"); VolumeIndexStore.Save(path, index);
            var restored = VolumeIndexStore.Load(path);
            var names = restored.ExportFiles().SelectMany(file => file.Names).Where(name => name.Name == longName).ToArray();
            Equal(2, names.Length);
            Equal(2, restored.Candidates("资料").Select(candidate => candidate.Id).Distinct().Count());
        }));
        checks.Run("共享构建策略跨过唯一名称采样与远距离文本后仍保持契约", () => WithDirectory(directory =>
        {
            var files = Enumerable.Range(20, 5000).Select(i => File((ulong)i, $"unique-{i}-" + new string('x', 80)))
                .Prepend(File(19, "shared-across-entire-pool"))
                .Append(File(6000, "shared-across-entire-pool")).ToArray();
            var index = Snapshot(files);
            var path = Path.Combine(directory, "policy.qbi"); VolumeIndexStore.Save(path, index);
            var bytes = System.IO.File.ReadAllBytes(path);
            int Offset(ulong id)
            {
                for (var i = 0; i < index.FileCount; i++)
                    if (BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(92 + i * 28)) == Id(id).Low)
                    {
                        var name = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(92 + i * 28 + 8));
                        return BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(92 + index.FileCount * 28 + name * 17 + 4));
                    }
                throw new InvalidDataException("测试文件身份缺失");
            }
#if QBOX_GLOBAL_NAME_SHARING
            Equal(Offset(19), Offset(6000));
#else
            True(Offset(19) != Offset(6000));
#endif
            Equal(2, VolumeIndexStore.Load(path).Candidates("shared-across").Count());
        }));
        checks.Run("强制整理已有 v2 不跳过名称共享且保留基础身份", () => WithDirectory(directory =>
        {
            var path = Path.Combine(directory, "existing-v2.qbi");
            var bytes = LegacyBytes(Snapshot(File(20, "same-name"), File(21, "same-name")), directory);
            BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(8), 2);
            System.Security.Cryptography.SHA256.HashData(bytes.AsSpan(0, bytes.Length - 32)).CopyTo(bytes, bytes.Length - 32);
            System.IO.File.WriteAllBytes(path, bytes);
            var original = VolumeIndexStore.Load(path);
            True(ReferenceEquals(original, original.ShareNameText()));
            var packed = original.ShareNameText(force: true);
            True(!ReferenceEquals(original, packed));
            Equal(original.CreatedAt, packed.CreatedAt); Equal(original.Checkpoint, packed.Checkpoint);
            var output = Path.Combine(directory, "repacked.qbi"); VolumeIndexStore.Save(output, packed);
            True(new FileInfo(output).Length < bytes.Length);
            Equal(2, VolumeIndexStore.Load(output).Candidates("same-name").Count());
        }));
        checks.Run("有效校验和中的非法名称边界仍拒绝并释放映射", () => WithDirectory(directory =>
        {
            var path = Path.Combine(directory, "volume.qbi");
            VolumeIndexStore.Save(path, Snapshot(File(20, "file")));
            var bytes = System.IO.File.ReadAllBytes(path);
            // v1/v2 的固定头在三字符卷根下均为 92 字节；名称偏移不得越过字符区。
            System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(92 + 2 * 28 + 4), int.MaxValue);
            System.Security.Cryptography.SHA256.HashData(bytes.AsSpan(0, bytes.Length - 32)).CopyTo(bytes, bytes.Length - 32);
            System.IO.File.WriteAllBytes(path, bytes);
            Throws<InvalidDataException>(() => VolumeIndexStore.Load(path));
            using var exclusive = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            Equal((long)bytes.Length, exclusive.Length);
        }));
        checks.Run("取消载入不遗留文件句柄", () => WithDirectory(directory =>
        {
            var path = Path.Combine(directory, "volume.qbi");
            VolumeIndexStore.Save(path, Snapshot(File(20, "file")));
            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();
            Throws<OperationCanceledException>(() => VolumeIndexStore.Load(path, cancellation.Token));
            using var exclusive = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            True(exclusive.Length > 0);
        }));
        checks.Run("快捷方式流式扫描保留取消信号", () =>
        {
            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();
            Throws<OperationCanceledException>(() => QBox.Windows.Shortcuts.ShortcutCatalog.Load(
                new[] { Snapshot(File(20, "sample.lnk")) }, cancellation.Token));
        });
        checks.Run("无引用映射由终结器释放", () => WithDirectory(directory =>
        {
            var path = Path.Combine(directory, "volume.qbi");
            VolumeIndexStore.Save(path, Snapshot(File(20, "file")));
            var weak = LoadWeak(path);
            GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
            True(!weak.IsAlive);
            using var exclusive = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            True(exclusive.Length > 0);
        }));
        checks.Run("取消保存保留旧索引且清理临时文件", () => WithDirectory(directory =>
        {
            var path = Path.Combine(directory, "volume.qbi");
            VolumeIndexStore.Save(path, Snapshot(File(20, "old")));
            var before = System.IO.File.ReadAllBytes(path);
            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();
            Throws<OperationCanceledException>(() => VolumeIndexStore.Save(path, Snapshot(File(20, "new")), cancellation.Token));
            True(before.SequenceEqual(System.IO.File.ReadAllBytes(path)));
            Equal(0, Directory.EnumerateFiles(directory, "*.tmp").Count());
        }));
        checks.Run("索引损坏拒绝读取且不会修复覆盖", () => WithDirectory(directory =>
        {
            var path = Path.Combine(directory, "volume.qbi");
            VolumeIndexStore.Save(path, Snapshot(File(20, "file")));
            var corrupt = System.IO.File.ReadAllBytes(path);
            corrupt[^40] ^= 1;
            System.IO.File.WriteAllBytes(path, corrupt);
            Throws<InvalidDataException>(() => VolumeIndexStore.Load(path));
            True(corrupt.SequenceEqual(System.IO.File.ReadAllBytes(path)));
            System.IO.File.WriteAllBytes(path, corrupt[..100]);
            Throws<InvalidDataException>(() => VolumeIndexStore.Load(path));
        }));
        checks.Run("索引只允许一个写入进程", () => WithDirectory(directory =>
        {
            var path = Path.Combine(directory, "volume.qbi");
            using (VolumeIndexStore.AcquireWriter(path)) Throws<IOException>(() => VolumeIndexStore.AcquireWriter(path));
            using var again = VolumeIndexStore.AcquireWriter(path);
        }));
        checks.Run("Windows 文件身份随改名保持，路径复用拒绝旧身份", () => WithDirectory(directory =>
        {
            if (!OperatingSystem.IsWindows()) return;
            var path = Path.Combine(directory, "before.txt");
            var renamed = Path.Combine(directory, "after.txt");
            System.IO.File.WriteAllText(path, "test");
            var before = NativeFileIdentity.Read(path);
            System.IO.File.Move(path, renamed);
            Equal(before, NativeFileIdentity.Read(renamed));
            using (NativeFileIdentity.Pin(renamed, before)) { }
            System.IO.File.Delete(renamed);
            System.IO.File.WriteAllText(renamed, "replacement");
            True(before != NativeFileIdentity.Read(renamed));
            Throws<IOException>(() => { if (OperatingSystem.IsWindows()) NativeFileIdentity.Pin(renamed, before); });
        }));
        checks.Run("快捷方式去掉扩展名参与首字母搜索，扩展名仍可查询", () =>
        {
            var index = Snapshot(File(20, "Neat Download Manager.lnk"));
            var friendly = index.Candidates("nd").Single();
            Equal("Neat Download Manager", friendly.Name);
            Equal(QBox.Core.Search.SearchItemKind.Shortcut, friendly.Kind);
            Equal(@"X:\Neat Download Manager.lnk", friendly.Target);
            var literal = index.Candidates(".lnk").Single();
            Equal("Neat Download Manager.lnk", literal.Name);
            Equal(friendly.Id, literal.Id);
        });
        checks.Run("DOS 短名作为长名别名匹配，不重复占位", () =>
        {
            var index = Snapshot(File(20, "long-filename.txt") with
            {
                Names = [new(Root, "long-filename.txt", 1, 0), new(Root, "LONGFI~1.TXT", 2, 0)]
            });
            var normal = index.Candidates("long").Single();
            var alias = index.Candidates("~1").Single();
            Equal("long-filename.txt", normal.Name);
            Equal("LONGFI~1.TXT", alias.Name);
            Equal(@"X:\long-filename.txt", alias.Target);
            Equal(normal.Id, alias.Id);
        });
        var generated = Enumerable.Range(20, 200).Select(i => File((ulong)i,
            i % 3 == 0 ? $"tool_{i % 9}_Alpha Beta.lnk" : i % 3 == 1 ? $"Alpha Beta_{i % 7}.txt" : $"目录_{i % 11}_Alpha.txt",
            directory: i % 5 == 0)).ToArray();
        var leftIndex = Snapshot(generated.Append(File(250, "long-file.txt") with
        {
            Names = [new(Root, "long-file.txt", 1, 0), new(Root, "LONGFI~1.TXT", 2, 0)]
        }).ToArray());
        var rightIndex = VolumeIndex.Create(Geometry with { Root = @"Y:\", Serial = Geometry.Serial + 1 },
            new(7, 100), leftIndex.ExportFiles());
        var history = new SelectionHistory();
        var allItems = leftIndex.Candidates("").ToArray();
        var epoch = DateTimeOffset.FromUnixTimeSeconds(1000000);
        history.RecordSelection(allItems[4].Id, "al", epoch);
        history.RecordSelection(allItems[7].Id, "alpha", epoch.AddSeconds(10));
        history.RecordSelection(allItems[11].Id, "other", epoch.AddSeconds(20));
        foreach (var query in new[] { "", "al", "Alpha", ".txt", "目录", "~1" })
            foreach (var limit in new[] { 0, 1, 9, 50 })
                checks.Run($"有界排序与完整排序一致 [{query}] / {limit}", () =>
                {
                    var expected = new SearchEngine(history).Search(query,
                        leftIndex.Candidates(query).Concat(rightIndex.Candidates(query)), limit);
                    var collector = new SearchCollector(query, limit);
                    var view = history.ForQuery(query);
                    leftIndex.SearchInto(collector, view);
                    rightIndex.SearchInto(collector, view);
                    var actual = collector.Finish();
                    Equal(expected.TotalMatches, actual.TotalMatches);
                    Equal(expected.Items.Count, actual.Items.Count);
                    for (var i = 0; i < expected.Items.Count; i++)
                    {
                        Equal(expected.Items[i].Candidate, actual.Items[i].Candidate);
                        Equal(expected.Items[i].Group, actual.Items[i].Group);
                        Equal(expected.Items[i].History, actual.Items[i].History);
                        Equal(expected.Items[i].Match.AlignmentScore, actual.Items[i].Match.AlignmentScore);
                        True(expected.Items[i].Match.Highlights.SequenceEqual(actual.Items[i].Match.Highlights));
                    }
                });
        checks.Run("大量命中只物化展示项且总数完整", () =>
        {
            var source = new CountingSource();
            var collector = new SearchCollector("same", 7);
            for (ulong i = 0; i < 10000; i++) collector.Add(source, i, "same".AsMemory(), SearchItemKind.File, 0, default);
            var result = collector.Finish();
            Equal(10000, result.TotalMatches);
            Equal(7, result.Items.Count);
            Equal(7, source.Materialized);
            Equal("id00000", result.Items[0].Candidate.Id);
            Equal("id00006", result.Items[6].Candidate.Id);
        });
        var presenceIndex = Snapshot(Enumerable.Range(20, 150).Select(i => File((ulong)i,
            i % 4 == 0 ? $"ſı_Alpha_Beta_{i}.txt" : i % 4 == 1 ? $"aXb_{i}.txt" : $"aa_ab_{i}.txt", directory: i % 3 == 0)).ToArray());
        foreach (var query in new[] { "si", "SI", "ı", "ſ", "aa", "aaa", "ab", "ba", "000", "§", "a_b" })
            foreach (var limit in new[] { 0, 1, 10, 17, 100 })
                checks.Run($"位图预筛选不改变完整匹配 [{query}] / {limit}", () =>
                {
                    var expected = new SearchEngine(new()).Search(query, presenceIndex.Candidates(query), limit);
                    var collector = new SearchCollector(query, limit);
                    presenceIndex.SearchInto(collector, new SelectionHistory().ForQuery(query));
                    var actual = collector.Finish();
                    Equal(expected.TotalMatches, actual.TotalMatches);
                    True(expected.Items.Select(hit => hit.Candidate).SequenceEqual(actual.Items.Select(hit => hit.Candidate)));
                });
        var unicodeIndex = Snapshot(new[] { "资料", "工作资料资料.txt", "料资.txt", "中文A文件.txt", "😀中文.txt", "𠀀𠮷资料.txt",
            "ſı_资料.txt", "İ资料.txt", "Ⱥⱥ资料.txt", "𐐀𐐨资料.txt", "e\u0301资料.txt" }
            .Select((name, i) => File((ulong)(100 + i), name, directory: i % 3 == 0))
            .Append(File(200, "long-report.txt") with { Names = [new(Root, "long-report.txt", 1, 0), new(Root, "中文~1.TXT", 2, 0)] }).ToArray());
        var sparseAscii = Snapshot(Enumerable.Range(100, 1024).Select(i => File((ulong)i, $"alpha_{i}.txt"))
            .Append(File(2000, "rare@资料!.txt")).Append(File(2001, "long-report.txt") with
            { Names = [new(Root, "long-report.txt", 1, 0), new(Root, "RARE@1.TXT", 2, 0)] }).ToArray());
        foreach (var query in new[] { "@", "!", "@资", "@a", "@1", "AL", "012", "a_a", "@!", "不存在@" })
            checks.Run("稀疏 ASCII 与完整匹配的全部结果字段一致：" + query, () =>
            {
                var reference = new SearchEngine(new()).Search(query, sparseAscii.Candidates(query), 2000);
                var collector = new SearchCollector(query, 2000);
                sparseAscii.SearchInto(collector, new SelectionHistory().ForQuery(query));
                var actual = collector.Finish();
                Equal(reference.TotalMatches, actual.TotalMatches); Equal(reference.Items.Count, actual.Items.Count);
                for (var i = 0; i < reference.Items.Count; i++)
                {
                    Equal(reference.Items[i].Candidate, actual.Items[i].Candidate);
                    Equal(reference.Items[i].Group, actual.Items[i].Group);
                    Equal(reference.Items[i].Match.AlignmentScore, actual.Items[i].Match.AlignmentScore);
                    True(reference.Items[i].Match.Highlights.SequenceEqual(actual.Items[i].Match.Highlights));
                }
            });
        foreach (var query in new[] { "资料", "资", "料资", "资料资料", "资料资", "中文A", "😀", "𠀀", "𠮷", "𠀀𠮷", "𐐨", "ı", "ſ", "İ", "ⱥ", "Ⱥ", "e\u0301", "中文~1", "无匹配探针" })
            foreach (var limit in new[] { 0, 1, 9, 100 })
                checks.Run($"Unicode 预筛与原完整遍历的结果及高亮一致 [{query}] / {limit}", () =>
                {
                    var reference = new SearchEngine(new()).Search(query, unicodeIndex.Candidates(query), limit);
                    var collector = new SearchCollector(query, limit);
                    unicodeIndex.SearchInto(collector, new SelectionHistory().ForQuery(query));
                    var actual = collector.Finish();
                    Equal(reference.TotalMatches, actual.TotalMatches); Equal(reference.Items.Count, actual.Items.Count);
                    for (var i = 0; i < reference.Items.Count; i++)
                    {
                        Equal(reference.Items[i].Candidate, actual.Items[i].Candidate);
                        Equal(reference.Items[i].Group, actual.Items[i].Group);
                        Equal(reference.Items[i].Match.AlignmentScore, actual.Items[i].Match.AlignmentScore);
                        True(reference.Items[i].Match.Highlights.SequenceEqual(actual.Items[i].Match.Highlights));
                    }
                });
        checks.Run("中文 DOS 别名跨位图边界时仍返回长名身份与路径", () =>
        {
            var index = Snapshot(Enumerable.Range(20, 63).Select(i => File((ulong)i, "filler" + i)).Append(
                File(83, "long-report.txt") with { Names = [new(Root, "long-report.txt", 1, 0), new(Root, "中文~1.TXT", 2, 0)] }).ToArray());
            var collector = new SearchCollector("中文"); index.SearchInto(collector, new SelectionHistory().ForQuery("中文"));
            var result = collector.Finish();
            Equal(1, result.TotalMatches); Equal(@"X:\long-report.txt", result.Items.Single().Candidate.Target);
            Equal(index.Candidates("中文").Single().Id, result.Items.Single().Candidate.Id);
        });
        checks.Run("中文实时覆盖层的新增改名仍使用相同检索结果", () =>
        {
            var old = new LiveVolumeIndex(Snapshot(File(20, "旧资料.txt")));
            var next = old.Apply([new(new(7, 110), [new(Id(20), File(20, "新工作资料.txt"))])]);
            var collector = new SearchCollector("工作资料"); next.SearchInto(collector, new SelectionHistory().ForQuery("工作资料"));
            Equal(@"X:\新工作资料.txt", collector.Finish().Items.Single().Candidate.Target);
        });
        checks.Run("取消 Unicode 索引构建后不缓存半成品", () =>
        {
            var index = Snapshot(File(20, "资料.txt"));
            Throws<OperationCanceledException>(() => index.PrepareSearch(new(true))); index.PrepareSearch();
            var collector = new SearchCollector("资料"); index.SearchInto(collector, new SelectionHistory().ForQuery("资料"));
            Equal(1, collector.Finish().TotalMatches);
        });
        checks.Run("取消位图构建后可以重新建立，不缓存半成品", () =>
        {
            var index = Snapshot(File(20, "Alpha"));
            Throws<OperationCanceledException>(() => index.PrepareSearch(new(true)));
            index.PrepareSearch();
            var collector = new SearchCollector("al");
            index.SearchInto(collector, new SelectionHistory().ForQuery("al"));
            Equal(1, collector.Finish().TotalMatches);
        });
        checks.Run("无快捷方式的便携 EXE 作为应用进入首屏，原始扩展名仍可查询", () =>
        {
            var index = Snapshot(File(20, "rtt", directory: true), File(21, "JLinkRTTViewer.exe"));
            var candidate = index.Candidates("rtt").Single(item => item.Kind == SearchItemKind.Application);
            Equal("JLinkRTTViewer", candidate.Name);
            True(candidate.IsApplicationEntry);
            var collector = new SearchCollector("rtt", 1);
            index.SearchInto(collector, new SelectionHistory().ForQuery("rtt"));
            Equal(candidate.Id, collector.Finish().Items[0].Candidate.Id);
            var literal = index.Candidates(".exe").Single();
            Equal("JLinkRTTViewer.exe", literal.Name);
            Equal(candidate.Id, literal.Id);
            True(!literal.IsApplicationEntry);
        });
        checks.Run("包目录 EXE 保留文件入口，不替代系统包激活", () =>
        {
            var index = Snapshot(File(20, "Program Files", directory: true), File(21, "WindowsApps", Id(20), true),
                File(22, "Example.Package", Id(21), true), File(23, "Atlas.exe", Id(22)));
            var candidate = index.Candidates("atlas").Single();
            Equal(SearchItemKind.File, candidate.Kind);
            True(!candidate.IsApplicationEntry);
        });
        checks.Run("已有匹配快捷方式的 EXE 保留文件入口，避免重复应用占位", () =>
        {
            var index = Snapshot(File(20, "Atlas.exe"));
            IReadOnlySet<string> represented = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { @"X:\Atlas.exe" };
            var candidate = index.Candidates("atlas", representedExecutables: represented).Single();
            Equal(SearchItemKind.File, candidate.Kind);
            var collector = new SearchCollector("atlas");
            index.SearchInto(collector, new SelectionHistory().ForQuery("atlas"), representedExecutables: represented);
            var result = collector.Finish();
            Equal(1, result.TotalMatches);
            Equal(candidate, result.Items[0].Candidate);
        });
        var exeIndex = Snapshot(File(20, "rtt", directory: true), File(21, "JLinkRTTViewer.exe"),
            File(22, "JLinkRTTLogger.exe"), File(23, "rXtt.exe"));
        foreach (var query in new[] { "rtt", ".exe", "exe", "JLink", "" })
            foreach (var limit in new[] { 1, 9 })
                checks.Run($"EXE 分类后的有界收集与完整排序一致 {query} / {limit}", () =>
                {
                    var expected = new SearchEngine(new()).Search(query, exeIndex.Candidates(query), limit);
                    var collector = new SearchCollector(query, limit);
                    exeIndex.SearchInto(collector, new SelectionHistory().ForQuery(query));
                    var actual = collector.Finish();
                    Equal(expected.TotalMatches, actual.TotalMatches);
                    True(expected.Items.Select(hit => hit.Candidate).SequenceEqual(actual.Items.Select(hit => hit.Candidate)));
                });
    }

    private static void RunLive(CheckSuite checks)
    {
        static SearchResponse Search(LiveVolumeIndex view, string query)
        {
            var collector = new SearchCollector(query, 100);
            view.SearchInto(collector, new SelectionHistory().ForQuery(query));
            return collector.Finish();
        }
        var original = Snapshot(File(20, "folder", directory: true), File(21, "alpha.txt", Id(20)), File(22, "other", directory: true));
        foreach (var query in new[] { "doubao", "db", "douba", "dbaogz" })
            checks.Run($"文件及目录拼音补充 {query}", () =>
            {
                var index = Snapshot(File(20, "豆包工作", directory: true), File(21, "豆包工作.txt"), File(22, "豆包工作.exe"));
                var found = Search(new(index), query);
                Equal(3, found.TotalMatches);
                True(found.Items.All(hit => hit.Match.Highlights[0].Start == 0));
            });
        checks.Run("实时新增中文名称可拼音检索且无重复字面项", () =>
        {
            var next = new LiveVolumeIndex(original).Apply([new(new(7, 110), [new(Id(30), File(30, "豆包资料"))])]);
            Equal(1, Search(next, "doubao").TotalMatches);
            Equal(1, Search(next, "豆包").TotalMatches);
        });
        checks.Run("拼音文件选择使用原文件身份并进入历史首位", () =>
        {
            var index = new LiveVolumeIndex(Snapshot(File(20, "豆包工作", directory: true), File(21, "dXb.txt")));
            var selected = Search(index, "db").Items.Single(hit => hit.Candidate.Name == "豆包工作").Candidate;
            var history = new SelectionHistory(); history.RecordSelection(selected.Id, "db");
            var collector = new SearchCollector("DB", 1); index.SearchInto(collector, history.ForQuery("DB"));
            Equal(selected.Id, collector.Finish().Items.Single().Candidate.Id);
        });
        checks.Run("同一 FRN 单名称改名移动可以迁移历史", () =>
        {
            var old = new LiveVolumeIndex(original);
            var selected = Search(old, "alpha").Items.Single().Candidate;
            var history = new SelectionHistory(); history.RecordSelection(selected.Id, "al");
            var next = old.Apply([new(new(7, 110), [new(Id(21), File(21, "alphabet.txt", Id(22)))])]);
            var mapping = IndexHistoryMigration.Plan(history.Snapshot, [old], [next]);
            Equal(1, mapping.Count); Equal(1, history.ReassignTargets(mapping));
            var renamed = Search(next, "alphabet").Items.Single().Candidate;
            True(history.ForQuery("al").Get(renamed.Id).ExactQuery != null);
            True(history.ForQuery("al").Get(selected.Id).ExactQuery == null);
        });
        checks.Run("纯检查点推进复用检索内容，真实改名和基础换代必须失效", () =>
        {
            var first = new LiveVolumeIndex(original);
            var cursorOnly = first.Apply([new(new(7, 110), [])]);
            True(first.HasSameSearchContent(cursorOnly));
            Equal(110L, cursorOnly.Checkpoint.NextUsn);
            var renamed = cursorOnly.Apply([new(new(7, 120), [new(Id(21), File(21, "renamed.txt"))])]);
            True(!cursorOnly.HasSameSearchContent(renamed));
            True(renamed.HasSameSearchContent(renamed.Apply([new(new(7, 130), [])])));
            True(!first.HasSameSearchContent(new LiveVolumeIndex(Snapshot(File(21, "alpha.txt")))));
        });
        checks.Run("记录复用或硬链接歧义不会迁移历史", () =>
        {
            var old = new LiveVolumeIndex(original); var history = new SelectionHistory();
            history.RecordSelection(Search(old, "alpha").Items.Single().Candidate.Id, "al");
            var reused = File(21, "alphabet.txt") with { File = Id(21, 8) };
            var next = old.Apply([new(new(7, 110), [new(reused.File, reused)])]);
            Equal(0, IndexHistoryMigration.Plan(history.Snapshot, [old], [next]).Count);
            var linked = File(21, "alphabet.txt") with { LinkCount = 2 };
            next = old.Apply([new(new(7, 110), [new(linked.File, linked)])]);
            Equal(0, IndexHistoryMigration.Plan(history.Snapshot, [old], [next]).Count);
        });
        checks.Run("DOS 别名的链接计数不妨碍单路径历史迁移", () =>
        {
            var originalFile = File(21, "original-name.txt") with { LinkCount = 2,
                Names = [new(Root, "original-name.txt", 1, 0), new(Root, "ORIGIN~1.TXT", 2, 0)] };
            var old = new LiveVolumeIndex(Snapshot(originalFile));
            var history = new SelectionHistory(); history.RecordSelection(Search(old, "original-name").Items.Single().Candidate.Id, "name");
            var renamed = originalFile with { Names = [new(Root, "renamed-name.txt", 1, 0), new(Root, "RENAME~1.TXT", 2, 0)] };
            var next = old.Apply([new(new(7, 110), [new(renamed.File, renamed)])]);
            Equal(1, IndexHistoryMigration.Plan(history.Snapshot, [old], [next]).Count);
        });
        checks.Run("实时视图覆盖改名并保持旧查询快照不变", () =>
        {
            var old = new LiveVolumeIndex(original);
            var next = old.Apply([new(new(7, 110), [new(Id(21), File(21, "beta.txt", Id(20)))])]);
            Equal(0, Search(next, "alpha").TotalMatches);
            Equal(@"X:\folder\beta.txt", Search(next, "beta").Items.Single().Candidate.Target);
            Equal(1, Search(old, "alpha").TotalMatches);
        });
        checks.Run("实时目录移动直接更新未修改子文件路径", () =>
        {
            var next = new LiveVolumeIndex(original).Apply([new(new(7, 110), [new(Id(20), File(20, "renamed", Id(22), true))])]);
            Equal(@"X:\other\renamed\alpha.txt", Search(next, "alpha").Items.Single().Candidate.Target);
        });
        checks.Run("空闲回收清除增量路径缓存后仍能解析路径", () =>
        {
            var original = Snapshot(File(20, "folder", directory: true), File(21, "readme.txt", Id(20)));
            var live = new LiveVolumeIndex(original).Apply([new(new(7, 110), [new(Id(20), File(20, "folder-renamed", directory: true))])]);
            Equal(@"X:\folder-renamed\readme.txt", Search(live, "readme").Items.Single().Candidate.Target);
            live.ReleaseIdleCaches();
            Equal(@"X:\folder-renamed\readme.txt", Search(live, "readme").Items.Single().Candidate.Target);
        });
        checks.Run("显示会触发预热，隐藏会取消", () =>
        {
            using var started = new ManualResetEventSlim();
            using var warmup = new SearchHotPathWarmup();
            warmup.Begin(token =>
            {
                started.Set();
                token.WaitHandle.WaitOne(TimeSpan.FromSeconds(5));
                token.ThrowIfCancellationRequested();
            });
            True(started.Wait(TimeSpan.FromSeconds(2)));
            True(warmup.IsRunning);
            warmup.Cancel();
            True(warmup.Completion.Wait(TimeSpan.FromSeconds(2)));
            True(!warmup.IsRunning);
        });
        checks.Run("回收后再预热，路径解析与搜索匹配仍正确", () =>
        {
            var original = Snapshot(File(20, "folder", directory: true), File(21, "readme.txt", Id(20)), File(22, "windows.txt"));
            var live = new LiveVolumeIndex(original).Apply([new(new(7, 110), [new(Id(20), File(20, "folder-renamed", directory: true))])]);
            live.Base.PrepareSearch();
            Equal(@"X:\folder-renamed\readme.txt", Search(live, "readme").Items.Single().Candidate.Target);
            Equal(1, Search(live, "windows").TotalMatches);
            live.ReleaseIdleCaches();
            Equal(0, live.PathCacheCount);
            live.PrefetchSearchHotPath();
            True(live.PathCacheCount > 0);
            Equal(@"X:\folder-renamed\readme.txt", Search(live, "readme").Items.Single().Candidate.Target);
            Equal(@"X:\windows.txt", Search(live, "windows").Items.Single().Candidate.Target);
        });
        checks.Run("预热与真实搜索并发时匹配结果不变", () =>
        {
            var live = new LiveVolumeIndex(Snapshot(File(20, "folder", directory: true), File(21, "alpha.txt", Id(20)), File(22, "alphabet.txt")));
            live.Base.PrepareSearch();
            var expected = Search(live, "al");
            using var warmup = new SearchHotPathWarmup();
            warmup.Begin([live]);
            var actual = Search(live, "al");
            Equal(expected.TotalMatches, actual.TotalMatches);
            True(expected.Items.Select(hit => hit.Candidate).SequenceEqual(actual.Items.Select(hit => hit.Candidate)));
            True(warmup.Completion.Wait(TimeSpan.FromSeconds(2)));
            var after = Search(live, "al");
            Equal(expected.TotalMatches, after.TotalMatches);
            True(expected.Items.Select(hit => hit.Candidate).SequenceEqual(after.Items.Select(hit => hit.Candidate)));
        });
        checks.Run("映射索引回收后再预热仍能正确匹配", () => WithDirectory(directory =>
        {
            var path = Path.Combine(directory, "X.qbi");
            VolumeIndexStore.Save(path, Snapshot(File(20, "folder", directory: true), File(21, "readme.txt", Id(20))));
            var loaded = new LiveVolumeIndex(VolumeIndexStore.Load(path));
            loaded.Base.PrepareSearch();
            loaded = loaded.Apply([new(new(7, 110), [new(Id(20), File(20, "folder-renamed", directory: true))])]);
            Equal(@"X:\folder-renamed\readme.txt", Search(loaded, "readme").Items.Single().Candidate.Target);
            loaded.ReleaseIdleCaches();
            loaded.PrefetchSearchHotPath();
            Equal(@"X:\folder-renamed\readme.txt", Search(loaded, "readme").Items.Single().Candidate.Target);
        }));
        checks.Run("含汉字名称回收后再预热不得中断且匹配不变", () =>
        {
            var original = Snapshot(File(20, "资料", directory: true), File(21, "豆包工作.txt", Id(20)), File(22, "readme.txt", Id(20)));
            var live = new LiveVolumeIndex(original).Apply([new(new(7, 110), [new(Id(20), File(20, "中文资料", directory: true))])]);
            live.Base.PrepareSearch();
            var expectedDoubao = Search(live, "doubao");
            var expectedHan = Search(live, "豆包");
            Equal(@"X:\中文资料\豆包工作.txt", expectedDoubao.Items.Single().Candidate.Target);
            Equal(expectedDoubao.TotalMatches, expectedHan.TotalMatches);
            live.ReleaseIdleCaches();
            Equal(0, live.PathCacheCount);
            live.PrefetchSearchHotPath();
            True(live.PathCacheCount > 0);
            var afterDoubao = Search(live, "doubao");
            var afterHan = Search(live, "豆包");
            Equal(expectedDoubao.TotalMatches, afterDoubao.TotalMatches);
            Equal(expectedHan.TotalMatches, afterHan.TotalMatches);
            Equal(@"X:\中文资料\豆包工作.txt", afterDoubao.Items.Single().Candidate.Target);
            Equal(@"X:\中文资料\豆包工作.txt", afterHan.Items.Single().Candidate.Target);
            Equal(@"X:\中文资料\readme.txt", Search(live, "readme").Items.Single().Candidate.Target);
        });
        checks.Run("映射的汉字索引预热不得抛异常且后续步骤仍执行", () => WithDirectory(directory =>
        {
            var path = Path.Combine(directory, "X.qbi");
            VolumeIndexStore.Save(path, Snapshot(File(20, "资料", directory: true), File(21, "豆包工作.txt", Id(20))));
            var loaded = new LiveVolumeIndex(VolumeIndexStore.Load(path));
            loaded.Base.PrepareSearch();
            loaded = loaded.Apply([new(new(7, 110), [new(Id(20), File(20, "中文资料", directory: true))])]);
            Equal(@"X:\中文资料\豆包工作.txt", Search(loaded, "doubao").Items.Single().Candidate.Target);
            loaded.ReleaseIdleCaches();
            Equal(0, loaded.PathCacheCount);
            loaded.PrefetchSearchHotPath();
            True(loaded.PathCacheCount > 0);
            Equal(@"X:\中文资料\豆包工作.txt", Search(loaded, "doubao").Items.Single().Candidate.Target);
            Equal(@"X:\中文资料\豆包工作.txt", Search(loaded, "豆包").Items.Single().Candidate.Target);
        }));
        checks.Run("实时删除父目录不返回子项旧路径", () =>
        {
            var next = new LiveVolumeIndex(original).Apply([new(new(7, 110), [new(Id(20), null)])]);
            Equal(0, Search(next, "alpha").TotalMatches);
        });
        checks.Run("目录可达缓存不跨删除恢复污染新旧视图", () =>
        {
            var first = new LiveVolumeIndex(original).Apply([new(new(7, 110), [new(Id(20), File(20, "first", directory: true))])]);
            Equal(@"X:\first\alpha.txt", Search(first, "alpha").Items.Single().Candidate.Target);
            var deleted = first.Apply([new(new(7, 120), [new(Id(20), null)])]);
            Equal(0, Search(deleted, "alpha").TotalMatches);
            var restored = deleted.Apply([new(new(7, 130), [new(Id(20), File(20, "restored", directory: true))])]);
            Equal(@"X:\restored\alpha.txt", Search(restored, "alpha").Items.Single().Candidate.Target);
            Equal(@"X:\first\alpha.txt", Search(first, "alpha").Items.Single().Candidate.Target);
            Equal(0, Search(deleted, "alpha").TotalMatches);
        });
        checks.Run("实时新目录与新文件可以同一批入索引", () =>
        {
            var next = new LiveVolumeIndex(original).Apply([new(new(7, 110), [new(Id(30), File(30, "newdir", directory: true)),
                new(Id(31), File(31, "newfile.txt", Id(30)))])]);
            Equal(@"X:\newdir\newfile.txt", Search(next, "newfile").Items.Single().Candidate.Target);
        });
        checks.Run("实时记录复用不保留旧文件或继承旧身份", () =>
        {
            var newer = File(21, "reused.txt") with { File = Id(21, 8) };
            var next = new LiveVolumeIndex(original).Apply([new(new(7, 110), [new(newer.File, newer)])]);
            Equal(0, Search(next, "alpha").TotalMatches);
            True(Search(next, "reused").Items.Single().Candidate.Id.Contains($":{newer.File.Low:x16}:"));
        });
        checks.Run("实时硬链接删除一条仍保留其他路径", () =>
        {
            var linked = File(21, "alpha.txt", Id(20)) with { LinkCount = 2,
                Names = [new(Id(20), "alpha.txt", 1, 0), new(Id(22), "alpha-link.txt", 1, 0)] };
            var next = new LiveVolumeIndex(original).Apply([new(new(7, 110), [new(Id(21), linked), new(Id(20), null)])]);
            var found = Search(next, "alpha");
            Equal(1, found.TotalMatches); Equal(@"X:\other\alpha-link.txt", found.Items.Single().Candidate.Target);
        });
        checks.Run("实时查询无变化时逐项保持原有结果和分值", () =>
        {
            var baseCollector = new SearchCollector("al", 9);
            original.SearchInto(baseCollector, new SelectionHistory().ForQuery("al"));
            var expected = baseCollector.Finish(); var actual = Search(new(original), "al");
            True(expected.Items.Select(h => h.Candidate).SequenceEqual(actual.Items.Select(h => h.Candidate)));
            Equal(expected.TotalMatches, actual.TotalMatches);
        });
        checks.Run("实时 USN 只写数据不产生名称变更", () =>
        {
            using var volume = new FakeVolume();
            volume.Current[21] = Record(21, "alpha.txt", Id(20)) with { LastUsn = 105, LinkCount = 1 };
            volume.Bounds = _ => new(7, 1, 1, 110);
            volume.Batches[100] = new(110, [Change(21, 105, UsnReason.DataOverwrite)]);
            var batch = new NtfsChangeReader(volume).Read(new(original));
            Equal(0, batch.Changes.Length); Equal(110L, batch.Checkpoint.NextUsn);
        });
        checks.Run("实时 USN 删除通知读取当前复用序列", () =>
        {
            using var volume = new FakeVolume();
            volume.Current[21] = Record(21, "new.txt") with { File = Id(21, 8), LinkCount = 1 };
            volume.Bounds = _ => new(7, 1, 1, 110);
            volume.Batches[100] = new(110, [Change(21, 105, UsnReason.FileDelete)]);
            var batch = new NtfsChangeReader(volume).Read(new(original));
            Equal(Id(21, 8), batch.Changes.Single().File);
            Equal("new.txt", batch.Changes.Single().Value!.Names.Single().Name);
        });
        checks.Run("实时 USN 断档拒绝继续追加", () =>
        {
            using var volume = new FakeVolume(); volume.Bounds = _ => new(8, 1, 1, 110);
            Throws<JournalGapException>(() => new NtfsChangeReader(volume).Read(new(original)));
        });
        checks.Run("实时 USN 属性扩展事件刷新所属主文件", () =>
        {
            using var volume = new FakeVolume();
            volume.Current[21] = Record(21, "renamed.txt") with { LinkCount = 1 };
            volume.Current[30] = Record(30, "unused") with { BaseFile = Id(21) };
            volume.Bounds = _ => new(7, 1, 1, 110);
            volume.Batches[100] = new(110, [Change(30, 105, UsnReason.DataOverwrite)]);
            var batch = new NtfsChangeReader(volume).Read(new(original));
            Equal(Id(21), batch.Changes.Single().File);
            Equal("renamed.txt", batch.Changes.Single().Value!.Names.Single().Name);
        });
        checks.Run("增量日志跨重启恢复并忽略半写尾帧", () => WithDirectory(directory =>
        {
            var path = Path.Combine(directory, "X.qbd"); IndexChangeLog.Reset(path, original);
            IndexChangeLog.Append(path, new(new(7, 110), [new(Id(21), File(21, "beta.txt", Id(20)))]));
            long cursor = 0;
            var view = new LiveVolumeIndex(original).Apply(IndexChangeLog.Read(path, original, ref cursor));
            Equal(1, Search(view, "beta").TotalMatches);
            var complete = cursor;
            using (var file = new FileStream(path, FileMode.Append)) file.Write(new byte[] { 100, 0, 0, 0, 1, 2, 3 });
            Equal(0, IndexChangeLog.Read(path, original, ref cursor).Count); Equal(complete, cursor);
            IndexChangeLog.Read(path, original, ref cursor, repairTail: true); Equal(complete, new FileInfo(path).Length);
        }));
        checks.Run("增量日志完整帧损坏拒绝修复跳过", () => WithDirectory(directory =>
        {
            var path = Path.Combine(directory, "X.qbd"); IndexChangeLog.Reset(path, original);
            IndexChangeLog.Append(path, new(new(7, 110), [new(Id(21), null)]));
            var bytes = System.IO.File.ReadAllBytes(path); bytes[^1] ^= 1; System.IO.File.WriteAllBytes(path, bytes);
            long cursor = 0;
            Throws<InvalidDataException>(() => IndexChangeLog.Read(path, original, ref cursor, true));
            True(bytes.SequenceEqual(System.IO.File.ReadAllBytes(path)));
        }));
        checks.Run("重载坏增量日志保留已载入的最新视图", () => WithDirectory(directory =>
        {
            var path = Path.Combine(directory, "X.qbi"); VolumeIndexStore.Save(path, original);
            var log = IndexChangeLog.PathFor(path); IndexChangeLog.Reset(log, original);
            IndexChangeLog.Append(log, new(new(7, 110), [new(Id(21), File(21, "latest.txt", Id(20)))]));
            var saved = LiveIndexFile.Load(path);
            var bytes = System.IO.File.ReadAllBytes(log); bytes[^1] ^= 1; System.IO.File.WriteAllBytes(log, bytes);
            var reloaded = LiveIndexFile.Load(path, lastGood: saved);
            True(ReferenceEquals(saved.View, reloaded.View)); True(reloaded.Warning != null);
            Equal(1, Search(reloaded.View, "latest").TotalMatches);
        }));
        checks.Run("增量日志不会应用到另一份基础索引", () => WithDirectory(directory =>
        {
            var path = Path.Combine(directory, "X.qbd"); IndexChangeLog.Reset(path, original); long cursor = 0;
            var newer = Snapshot(File(30, "different"));
            Throws<IndexBaseChangedException>(() => IndexChangeLog.Read(path, newer, ref cursor));
        }));
        checks.Run("实时增量检查点回退被拒绝", () =>
            Throws<InvalidDataException>(() => new LiveVolumeIndex(original).Apply([new(new(7, 99), [])])));
    }

    // 独立栈帧退出后不再留下 JIT 临时强引用，测量真实的映射所有者回收。
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static byte[] LegacyBytes(VolumeIndex index, string directory)
    {
        var path = Path.Combine(directory, "legacy-fixture.tmp");
        VolumeIndexStore.Save(path, index);
        var bytes = System.IO.File.ReadAllBytes(path);
        var namesAt = 92 + index.FileCount * 28;
        var textAt = namesAt + index.NameCount * 17;
        using var body = new MemoryStream();
        body.Write(bytes.AsSpan(0, textAt));
        var offset = 0;
        for (var i = 0; i < index.NameCount; i++)
        {
            var position = namesAt + i * 17;
            var source = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(position + 4));
            var length = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(position + 8));
            body.Write(bytes.AsSpan(textAt + source * 2, length * 2));
            // 显式构造旧格式的连续独占文本，避免用新读写器互验冒充 v1 兼容性。
            BinaryPrimitives.WriteInt32LittleEndian(body.GetBuffer().AsSpan(position + 4), offset);
            offset += length;
        }
        BinaryPrimitives.WriteInt32LittleEndian(body.GetBuffer().AsSpan(8), 1);
        BinaryPrimitives.WriteInt32LittleEndian(body.GetBuffer().AsSpan(88), offset);
        var content = body.ToArray();
        return [.. content, .. System.Security.Cryptography.SHA256.HashData(content)];
    }

    private static WeakReference LoadWeak(string path) => new(VolumeIndexStore.Load(path));

    private static FileReference Id(ulong number, ushort sequence = 7) => new(number | ((ulong)sequence << 48));
    private sealed class CountingSource : IUniqueSearchSource
    {
        public int Materialized { get; private set; }
        public SearchCandidate Materialize(ulong key, ReadOnlyMemory<char> name, SearchItemKind kind, ulong priority)
        {
            Materialized++;
            return new($"id{key:D5}", name.ToString(), "unused", kind, NormalPriority: priority);
        }
        public int CompareIdentity(ulong key, IUniqueSearchSource other, ulong otherKey) => key.CompareTo(otherKey);
        public string Identity(ulong key) => $"id{key:D5}";
    }
    private static MftName Name(string name, FileReference? parent = null) => new(parent ?? Root, name, 1, 0, 0);
    private static MftRecord Record(ulong number, string name, FileReference? parent = null, bool directory = false) =>
        new(number == 5 ? Root : Id(number), default, directory, [Name(name, parent)], [], false);
    private static IndexedFile File(ulong number, string name, FileReference? parent = null, bool directory = false) =>
        new(number == 5 ? Root : Id(number), directory, 50, 1, [new(parent ?? Root, name, 1, 0)]);
    private static VolumeIndex Snapshot(params IndexedFile[] files) =>
        VolumeIndex.Create(Geometry, new(7, 100), new[] { File(5, ".", directory: true) }.Concat(files));
    private static UsnChange Change(ulong number, long usn, UsnReason reason, ushort sequence = 7) =>
        new(Id(number, sequence), Root, usn, 0, reason, 0, "event");
    private static byte[] Raw(string name, bool directory = false)
    {
        var bytes = NtfsParserChecks.Mft(name);
        U16(bytes, 22, directory ? (ushort)3 : (ushort)1);
        return bytes;
    }
    private static byte[] AttributeEntry(uint type, FileReference reference, ushort instance, ulong vcn = 0)
    {
        var bytes = new byte[32];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, type);
        U16(bytes, 4, 32);
        BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(8), vcn);
        BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(16), reference.Low);
        U16(bytes, 24, instance);
        return bytes;
    }
    private static void Restore(byte[] bytes)
    {
        U16(bytes, 510, BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(50)));
        U16(bytes, 1022, BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(52)));
    }
    private static void U16(byte[] bytes, int offset, ushort value) => BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(offset), value);
    private static void WithDirectory(Action<string> action)
    {
        var directory = Path.Combine(Path.GetTempPath(), "qbox-index-check-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try { action(directory); }
        finally { Directory.Delete(directory, recursive: true); }
    }

    private sealed class FakeVolume : INtfsVolume
    {
        public NtfsGeometry Geometry { get; }
        public byte[] Disk { get; } = new byte[1024 * 1024];
        public Dictionary<ulong, MftRecord> Current { get; } = [];
        public Dictionary<long, UsnBatch> Batches { get; } = [];
        public Func<int, JournalBounds> Bounds { get; set; } = _ => new(7, 1, 1, 100);
        public int JournalReads { get; private set; }
        private int _boundsReads;

        public FakeVolume(NtfsGeometry? geometry = null)
        {
            Geometry = geometry ?? NtfsIndexChecks.Geometry;
            var mft = NtfsParserChecks.MftData(out var offset);
            BinaryPrimitives.WriteUInt64LittleEndian(mft.AsSpan(offset + 24), 15);
            BinaryPrimitives.WriteUInt64LittleEndian(mft.AsSpan(offset + 48), 65536);
            mft[offset + 65] = 16;
            PutRaw(0, mft);
            var root = Raw(".", true);
            U16(root, 16, 9);
            PutRaw(5, root);
        }
        public void PutRaw(ulong number, byte[] bytes)
        {
            bytes.CopyTo(Disk, checked((int)(100UL * (uint)Geometry.ClusterSize + number * 1024)));
            Current[number] = MftRecordParser.Parse(bytes, number)!;
        }
        public void ReadExactly(long offset, Span<byte> destination, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Disk.AsSpan(checked((int)offset), destination.Length).CopyTo(destination);
        }
        public MftRecord? ReadRecord(ulong number, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Current.GetValueOrDefault(number);
        }
        public JournalBounds ReadJournalBounds() => Bounds(++_boundsReads);
        public UsnBatch ReadJournal(JournalCheckpoint checkpoint, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            JournalReads++;
            return Batches[checkpoint.NextUsn];
        }
        public IEnumerable<UsnChange> EnumerateRecords(CancellationToken cancellationToken = default) =>
            Current.Values.Where(r => r.BaseFile == default && r.Names.Count > 0).Select(r =>
                new UsnChange(r.File, r.Names[0].Parent, r.LastUsn ?? 0, 0, 0, 0, r.Names[0].Name)).ToArray();
        public void Dispose() { }
    }
}
