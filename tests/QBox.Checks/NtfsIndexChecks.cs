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
