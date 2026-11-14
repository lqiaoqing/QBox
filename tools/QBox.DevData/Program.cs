using System.Text;
using QBox.Core.History;
using QBox.Core.Search;
using QBox.Windows.Indexing;
using QBox.Windows.Ntfs;

if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("开发快照需要 Windows");
if (args.Length != 1) throw new ArgumentException("用法：QBox.DevData <开发数据目录>");
var output = Path.GetFullPath(args[0]);
var root = Path.GetPathRoot(output)!.ToUpperInvariant();
if (root.Length != 3 || new DriveInfo(root).DriveFormat != "NTFS")
    throw new NotSupportedException("开发数据需要本地 NTFS 分区；其他分区可使用 CLI 示例目录数据");
var filesDirectory = Path.Combine(output, "files", "QBox Demo");
Directory.CreateDirectory(filesDirectory);
Directory.CreateDirectory(Path.Combine(filesDirectory, "资料"));
Directory.CreateDirectory(Path.Combine(filesDirectory, "工作"));
foreach (var name in new[] { "Alpha Beta.txt", "中文资料.txt", "豆包示例.txt", "重复资料资料.txt", "😀示例.txt" })
{
    var path = Path.Combine(filesDirectory, name);
    // 重复启动只补齐缺失样例，不覆盖开发者对样例内容的修改。
    if (File.Exists(path)) continue;
    using var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write);
    file.Write(Encoding.UTF8.GetBytes("QBox 开发样例。可用于验证查询和打开行为。\n"));
}
var paths = Directory.EnumerateFileSystemEntries(filesDirectory).Append(filesDirectory).ToHashSet(StringComparer.OrdinalIgnoreCase);
foreach (var path in paths.ToArray())
{
    for (var parent = Directory.GetParent(path); parent != null; parent = parent.Parent) paths.Add(parent.FullName);
}
// 仅读取样例及其父目录的普通文件身份，不打开原始卷或读取 MFT/USN。
var identities = paths.ToDictionary(path => path, NativeFileIdentity.Read, StringComparer.OrdinalIgnoreCase);
var volume = identities[root].VolumeSerial;
var records = paths.Select(path =>
{
    var identity = identities[path];
    if (identity.VolumeSerial != volume || identity.File.High != 0) throw new NotSupportedException("样例路径不能跨卷");
    var isRoot = path.Equals(root, StringComparison.OrdinalIgnoreCase);
    var attributes = File.GetAttributes(path);
    var parent = isRoot ? identity.File : identities[Directory.GetParent(path)!.FullName].File;
    var name = isRoot ? "." : Path.GetFileName(Path.TrimEndingDirectorySeparator(path));
    return new IndexedFile(identity.File, (attributes & FileAttributes.Directory) != 0, null, 1, [new(parent, name, 1, (uint)attributes)]);
}).ToArray();
// 子集快照不包含卷布局或有效 USN 检查点；开发实例必须关闭自动全盘更新。
var index = VolumeIndex.Create(new(root, volume, 0, 0, 0, 0, 0, 0, 0), new(0, 0), records);
var indexDirectory = Path.Combine(output, "indexes");
Directory.CreateDirectory(indexDirectory);
var indexPath = Path.Combine(indexDirectory, root[0] + ".qbi");
using (VolumeIndexStore.AcquireWriter(indexPath)) VolumeIndexStore.Save(indexPath, index);
var loaded = VolumeIndexStore.Load(indexPath);
var collector = new SearchCollector("中文资料");
loaded.SearchInto(collector, new SelectionHistory().ForQuery("中文资料"));
if (!collector.Finish().Items.Single().Candidate.Target.Equals(Path.Combine(filesDirectory, "中文资料.txt"), StringComparison.OrdinalIgnoreCase))
    throw new InvalidDataException("开发快照查询不正确");
Console.WriteLine($"开发快照：{indexPath}（{loaded.FileCount} 项，含路径所需父目录）");
