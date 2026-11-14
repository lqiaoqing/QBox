using System.Globalization;
using QBox.Core.History;
using QBox.Windows.Ntfs;

namespace QBox.Windows.Indexing;

public static class IndexHistoryMigration
{
    public static IReadOnlyDictionary<string, string> Plan(HistorySnapshot history,
        IReadOnlyList<LiveVolumeIndex> before, IReadOnlyList<LiveVolumeIndex> after)
    {
        var mapping = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var target in history.Targets)
        {
            var fields = target.TargetId.Split(':');
            if (fields.Length != 5 || fields[0] != "ntfs" ||
                !ulong.TryParse(fields[1], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var serial) ||
                !ulong.TryParse(fields[2], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var file) ||
                !ulong.TryParse(fields[3], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var parent)) continue;
            var old = before.FirstOrDefault(view => view.Base.Geometry.Serial == serial)?.FindFile(new(file));
            var next = after.FirstOrDefault(view => view.Base.Geometry.Serial == serial)?.FindFile(new(file));
            // 只迁移前后均为单一长名称的同一完整 FRN，硬链接增删和序列复用不能猜测归属。
            if (old == null || next == null || old.LinkCount != 1 || next.LinkCount != 1) continue;
            var oldNames = old.Names.Where(name => name.Namespace != 2).ToArray();
            var newNames = next.Names.Where(name => name.Namespace != 2).ToArray();
            if (oldNames.Length != 1 || newNames.Length != 1 || oldNames[0].Parent.Low != parent ||
                VolumeIndex.NameIdentity(oldNames[0].Name) != fields[4]) continue;
            var renamed = newNames[0];
            var identity = $"ntfs:{serial:x16}:{file:x16}:{renamed.Parent.Low:x16}:{VolumeIndex.NameIdentity(renamed.Name)}";
            if (identity != target.TargetId) mapping[target.TargetId] = identity;
        }
        return mapping;
    }
}
