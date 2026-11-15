using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using QBox.Core.History;
using QBox.Core.Search;
using QBox.Windows.Indexing;
using QBox.Windows.Ntfs;

internal static class IndexingBenchmark
{
    public static void Run(string output, bool remap, bool repeatedNames = false)
    {
        Directory.CreateDirectory(output);
        var collectBetweenRounds = Environment.GetEnvironmentVariable("QBOX_PROBE_INDEXING_COLLECT") == "1";
        var runs = new List<object>();
        for (var pass = 0; pass < 5; pass++)
        {
            // 可选轮间回收只用于隔离上一轮暂存图，收集阶段不计入各阶段计时，产品不启用此行为。
            if (collectBetweenRounds) { GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect(); }
            using var volume = new ReplayVolume(repeatedNames);
            var builder = new NtfsIndexBuilder(volume);
            var path = Path.Combine(output, $"pass-{pass}.qbi");
            var timer = Stopwatch.StartNew();
            var built = builder.Build();
            var buildMs = timer.Elapsed.TotalMilliseconds;
            timer.Restart();
            VolumeIndexStore.Save(path, built);
            if (remap) built = VolumeIndexStore.Load(path);
            var saveMs = timer.Elapsed.TotalMilliseconds;
            timer.Restart();
            var loaded = VolumeIndexStore.Load(path);
            var loadMs = timer.Elapsed.TotalMilliseconds;
            var liveBase = new LiveVolumeIndex(loaded);
            var live = liveBase;
            volume.Change();
            IndexChangeSet changes = null!;
            var incrementalSamples = new List<double>();
            var readSamples = new List<double>();
            var applySamples = new List<double>();
            // 多个相同批次从同一检查点重放，避免单次十毫秒级采样被一次 GC 或磁盘刷新主导。
            for (var sample = 0; sample < 20; sample++)
            {
                timer.Restart();
                changes = new NtfsChangeReader(volume).Read(liveBase);
                var read = timer.Elapsed.TotalMilliseconds;
                live = liveBase.Apply([changes]);
                var elapsed = timer.Elapsed.TotalMilliseconds;
                readSamples.Add(read); applySamples.Add(elapsed - read); incrementalSamples.Add(elapsed);
            }
            var incrementalMs = incrementalSamples.Order().ElementAt(incrementalSamples.Count / 2);
            IndexChangeLog.Reset(IndexChangeLog.PathFor(path), loaded);
            timer.Restart();
            IndexChangeLog.Append(IndexChangeLog.PathFor(path), changes);
            var appendMs = timer.Elapsed.TotalMilliseconds;
            timer.Restart();
            var compacted = builder.Synchronize(loaded);
            VolumeIndexStore.Save(path, compacted);
            if (remap) compacted = VolumeIndexStore.Load(path);
            var compactMs = timer.Elapsed.TotalMilliseconds;
            var collector = new SearchCollector("updated", 9);
            compacted.SearchInto(collector, new SelectionHistory().ForQuery("updated"));
            var result = collector.Finish();
            if (built.FileCount != 201001 || changes.Changes.Length != 2000 || result.TotalMatches != 2000 ||
                compacted.Checkpoint.NextUsn != 2100) throw new InvalidDataException("索引重放结果不完整");
            var responseSha256 = Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(result)));
            runs.Add(new { pass, buildMs, saveMs, loadMs, incrementalMs, incrementalSamples, readSamples, applySamples, appendMs, compactMs,
                fileCount = compacted.FileCount, changedRecords = changes.Changes.Length, responseSha256 });
            Console.WriteLine($"pass {pass}: build={buildMs:F1}, save={saveMs:F1}, load={loadMs:F1}, USN={incrementalMs:F1}, append={appendMs:F1}, compact={compactMs:F1} ms");
            GC.KeepAlive(built); GC.KeepAlive(live); GC.KeepAlive(compacted);
        }
        File.WriteAllText(Path.Combine(output, "report.json"), JsonSerializer.Serialize(new
        {
            fixture = "201001 records from deterministic cache reconciliation; 2000 USN renames",
            physicalDiskThroughputMeasured = false, remapAfterSave = remap, repeatedNames, collectBetweenRounds, runs
        }, new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true }));
    }

    // 相同的缓存枚举与 USN 输入重放到真实建库/增量/合并代码，不把内存设备吞吐当作物理磁盘速度。
    private sealed class ReplayVolume : INtfsVolume
    {
        public NtfsGeometry Geometry { get; } = new(@"X:\", 9, 512, 4096, 1024, 65536, 0, 3, 1);
        private static FileReference Root => Id(5);
        private readonly Dictionary<ulong, MftRecord> _records = [];
        private UsnChange[] _changes = [];
        private long _next = 100;
        private static FileReference Id(ulong number) => new(number | (7UL << 48));
        public ReplayVolume(bool repeatedNames)
        {
            _records[0] = new(Id(0), default, false, [], [new(0, 15, 65536, [new(0, 16, 0)])], false);
            _records[5] = Record(5, ".", Root, true);
            for (ulong i = 0; i < 1000; i++) _records[20 + i] = Record(20 + i, $"folder-{i}", Root, true);
            for (ulong i = 0; i < 200000; i++)
                // 重复名称夹具跨目录共享同名；同一目录内仍保持名称唯一。
                _records[2000 + i] = Record(2000 + i, $"Alpha-{(repeatedNames ? i / 1000 : i):D6}-资料.txt", Id(20 + i % 1000), false);
        }
        private static MftRecord Record(ulong id, string name, FileReference parent, bool directory) =>
            new(Id(id), default, directory, [new(parent, name, 1, 0, 0)], [], false, LastUsn: 100);
        public void Change()
        {
            _changes = new UsnChange[2000];
            for (ulong i = 0; i < 2000; i++)
            {
                var number = 2000 + i;
                _records[number] = Record(number, $"updated-{i:D6}-资料.txt", Id(20 + i % 1000), false);
                _changes[i] = new(Id(number), Id(20 + i % 1000), 100 + (long)i, 0, UsnReason.RenameNewName, 0, "updated");
            }
            _next = 2100;
        }
        public MftRecord? ReadRecord(ulong recordNumber, CancellationToken cancellationToken = default) => _records.GetValueOrDefault(recordNumber);
        public JournalBounds ReadJournalBounds() => new(7, 1, 1, _next);
        public UsnBatch ReadJournal(JournalCheckpoint checkpoint, CancellationToken cancellationToken = default) => new(_next, _changes);
        public IEnumerable<UsnChange> EnumerateRecords(CancellationToken cancellationToken = default) =>
            _records.Values.Where(record => record.Names.Count != 0).Select(record =>
                new UsnChange(record.File, record.Names[0].Parent, record.LastUsn ?? 0, 0, 0, 0, record.Names[0].Name));
        public void ReadExactly(long offset, Span<byte> destination, CancellationToken cancellationToken = default) => destination.Clear();
        public void Dispose() { }
    }
}
