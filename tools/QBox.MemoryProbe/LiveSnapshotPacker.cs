using System.Security.Cryptography;
using System.Text.Json;
using QBox.Windows.Indexing;

internal static class LiveSnapshotPacker
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    public static void Run(string sourceDirectory, string outputDirectory)
    {
        var source = Path.GetFullPath(sourceDirectory);
        var output = Path.GetFullPath(outputDirectory);
        if (source.Equals(output, StringComparison.OrdinalIgnoreCase) || Directory.Exists(output) || File.Exists(output))
            throw new IOException("只允许写入尚不存在的独立索引目录");
        var files = Directory.GetFiles(source, "*.qbi").Order(StringComparer.Ordinal).ToArray();
        if (files.Length == 0) throw new InvalidDataException("源目录没有基础索引");
        var parent = Path.GetDirectoryName(output)!;
        Directory.CreateDirectory(parent);
        var staging = Path.Combine(parent, "." + Path.GetFileName(output) + ".preparing-" + Guid.NewGuid().ToString("N"));
        ValidateTarget(staging, parent); ValidateTarget(output, parent);
        Directory.CreateDirectory(staging);
        var reports = new List<object>();
        try
        {
            foreach (var path in files)
            {
                var captured = Capture(path);
                var packed = captured.Base.ShareNameText(force: true);
                var destination = Path.Combine(staging, Path.GetFileName(path));
                var reference = destination + ".source";
                // 持有已验证的源映射，标准版并发合并后也不会换到另一代文件进行对照。
                VolumeIndexStore.Save(reference, captured.Base);
                VolumeIndexStore.Save(destination, packed);
                var comparison = destination + ".validation.json";
                IndexSnapshotProbe.Compare(reference, destination, comparison);
                var log = IndexChangeLog.PathFor(destination);
                IndexChangeLog.Reset(log, packed);
                foreach (var batch in captured.Batches) IndexChangeLog.Append(log, batch);
                var restored = LiveIndexFile.Load(destination);
                var before = new LiveVolumeIndex(captured.Base).Apply(captured.Batches);
                if (restored.Warning != null || before.Checkpoint != restored.View.Checkpoint ||
                    before.ChangedRecords != restored.View.ChangedRecords || ChangesHash(before) != ChangesHash(restored.View))
                    throw new InvalidDataException("试用副本的增量视图与捕获来源不一致");
                var sourceBytes = new FileInfo(reference).Length;
                var packedBytes = new FileInfo(destination).Length;
                reports.Add(new { volume = Path.GetFileNameWithoutExtension(path), before.Base.FileCount, before.Base.NameCount,
                    sourceBytes, packedBytes, bytesSaved = sourceBytes - packedBytes, changedRecords = before.ChangedRecords,
                    baseStructureAndUtf16Equal = true, liveChangesEqual = true, checkpointPreserved = true });
                File.Delete(reference);
                Console.WriteLine($"{Path.GetFileName(path)}: {sourceBytes / 1048576.0:F2} -> {packedBytes / 1048576.0:F2} MiB; live={before.ChangedRecords}");
            }
#if QBOX_GLOBAL_NAME_SHARING
            const string mode = "global";
#else
            const string mode = "local-256KiB";
#endif
            File.WriteAllText(Path.Combine(staging, "preparation.json"), JsonSerializer.Serialize(new
            { mode, capturedAt = DateTimeOffset.UtcNow, sourceModified = false, volumes = reports }, Json));
            // 仅发布本次创建并全部校验完成的目录，失败或退出不能暴露一半增量状态。
            ValidateTarget(staging, parent); ValidateTarget(output, parent);
            Directory.Move(staging, output);
        }
        finally
        {
            if (Directory.Exists(staging)) { ValidateTarget(staging, parent); Directory.Delete(staging, true); }
        }
    }

    private static (VolumeIndex Base, IReadOnlyList<IndexChangeSet> Batches) Capture(string path)
    {
        for (var attempt = 0; attempt < 3; attempt++)
        {
            var stamp = new FileInfo(path);
            var length = stamp.Length; var written = stamp.LastWriteTimeUtc;
            var index = VolumeIndexStore.Load(path);
            long cursor = 0;
            try
            {
                var batches = IndexChangeLog.Read(IndexChangeLog.PathFor(path), index, ref cursor);
                stamp.Refresh();
                if (stamp.Exists && stamp.Length == length && stamp.LastWriteTimeUtc == written) return (index, batches);
            }
            catch (IndexBaseChangedException) { }
            // 只因实际检测到基础换代才重新捕获；不修复或改写标准版的日志尾部。
        }
        throw new IOException("源索引正在持续换代，请在合并结束后再准备副本");
    }

    private static string ChangesHash(LiveVolumeIndex view) => Convert.ToHexStringLower(SHA256.HashData(
        JsonSerializer.SerializeToUtf8Bytes(view.Changes.OrderBy(change => change.File.Low), Json)));

    private static void ValidateTarget(string path, string parent)
    {
        var prefix = Path.GetFullPath(parent).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!Path.GetFullPath(path).StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            throw new IOException("临时索引目录超出指定目标范围");
    }
}
