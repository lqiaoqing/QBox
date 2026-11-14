using QBox.Core.History;
using QBox.Core.Search;
using QBox.Windows.Ntfs;

namespace QBox.Windows.Indexing;

public sealed record IndexChange(FileReference File, IndexedFile? Value);
public sealed record IndexChangeSet(JournalCheckpoint Checkpoint, IndexChange[] Changes);

// 基础索引保持不可变，查询只叠加发生过变化的记录；目录改名时子项按新父链生成路径。
public sealed class LiveVolumeIndex
{
    private readonly Dictionary<ulong, IndexChange> _changes;
    private readonly VolumeIndex? _overlay;
    private readonly bool _changedDirectories;
    private readonly System.Collections.Concurrent.ConcurrentDictionary<ulong, string> _paths = new();
    public VolumeIndex Base { get; }
    public JournalCheckpoint Checkpoint { get; }
    public int ChangedRecords => _changes.Count;
    public IEnumerable<IndexChange> Changes => _changes.Values;

    public LiveVolumeIndex(VolumeIndex index) : this(index, index.Checkpoint, []) { }

    private LiveVolumeIndex(LiveVolumeIndex saved, JournalCheckpoint checkpoint)
    {
        Base = saved.Base; Checkpoint = checkpoint; _changes = saved._changes;
        _overlay = saved._overlay; _changedDirectories = saved._changedDirectories; _paths = saved._paths;
    }

    private LiveVolumeIndex(VolumeIndex index, JournalCheckpoint checkpoint, Dictionary<ulong, IndexChange> changes)
    {
        Base = index;
        Checkpoint = checkpoint;
        _changes = changes;
        _changedDirectories = changes.Values.Any(c => c.Value?.IsDirectory == true || index.FindFile(c.File)?.IsDirectory == true);
        if (changes.Count == 0) return;
        var files = new Dictionary<FileReference, IndexedFile>();
        void Include(IndexedFile file)
        {
            if (!files.TryAdd(file.File, file)) return;
            foreach (var name in file.Names)
                if (FindFile(name.Parent) is { IsDirectory: true } parent) Include(parent);
        }
        foreach (var change in changes.Values)
            if (change.Value is { } file) Include(file);
        // 删除或复用的父记录不能留下伪路径，其他仍有效的硬链接继续参与搜索。
        foreach (var file in files.Values.ToArray())
        {
            var names = file.Names.Where(n => DirectoryPath(n.Parent, []) != null).ToArray();
            if (names.Length == 0) files.Remove(file.File);
            else if (names.Length != file.Names.Count) files[file.File] = file with { Names = names };
        }
        if (files.Count != 0) _overlay = VolumeIndex.Create(index.Geometry, checkpoint, files.Values);
    }

    public IndexedFile? FindFile(FileReference id)
    {
        if (_changes.TryGetValue(id.RecordNumber, out var change)) return change.File == id ? change.Value : null;
        return Base.FindFile(id);
    }

    public LiveVolumeIndex Apply(IEnumerable<IndexChangeSet> batches)
    {
        Dictionary<ulong, IndexChange>? changes = null;
        var checkpoint = Checkpoint;
        foreach (var batch in batches)
        {
            if (batch == null || batch.Changes == null) throw new InvalidDataException("增量日志缺少变更集合");
            if (batch.Checkpoint.JournalId != checkpoint.JournalId || batch.Checkpoint.NextUsn < checkpoint.NextUsn)
                throw new InvalidDataException("增量日志的检查点发生回退");
            foreach (var change in batch.Changes)
            {
                changes ??= new(_changes);
                if (change == null || change.File.High != 0 || change.File.RecordNumber == 5 && change.Value?.IsDirectory != true ||
                    change.Value is { } value && (value.File != change.File || value.Names == null || value.Names.Count == 0 || value.Names.Any(n =>
                        n == null || n.Parent.High != 0 || n.Namespace > 3 || string.IsNullOrEmpty(n.Name) || n.Name.AsSpan().ContainsAny('\\', '/', '\0') || n.Name == "..")))
                    throw new InvalidDataException("增量记录或名称无效");
                changes[change.File.RecordNumber] = change;
            }
            checkpoint = batch.Checkpoint;
        }
        return changes == null ? new(this, checkpoint) : new(Base, checkpoint, changes);
    }

    internal bool IncludesBase(ulong file) => !_changes.ContainsKey(file & 0x0000ffffffffffffUL);
    internal bool IncludesOverlay(ulong file) => _changes.TryGetValue(file & 0x0000ffffffffffffUL, out var change) && change.Value != null && change.File.Low == file;

    internal string? PathFor(VolumeIndex index, int nameIndex)
    {
        var name = index.Names[nameIndex];
        var parent = DirectoryPath(new(index.Nodes[name.Parent].File), []);
        return parent == null ? null : Path.Combine(parent, index.Text.Substring(name.Offset, name.Length));
    }

    private string? DirectoryPath(FileReference id, HashSet<FileReference> visited)
    {
        if (id.Low == Base.Nodes[Base.RootNode].File) return Base.Geometry.Root;
        if (_paths.TryGetValue(id.Low, out var cached)) return cached.Length == 0 ? null : cached;
        if (visited.Count >= 1024) throw new InvalidDataException("增量目录层级超过安全范围");
        if (!visited.Add(id)) throw new InvalidDataException("增量目录关系存在循环");
        string? path = null;
        if (FindFile(id) is { IsDirectory: true } file)
        {
            foreach (var name in file.Names.OrderBy(n => n.Namespace == 2))
            {
                var parent = DirectoryPath(name.Parent, visited);
                if (parent != null) { path = Path.Combine(parent, name.Name); break; }
            }
        }
        visited.Remove(id);
        _paths.TryAdd(id.Low, path ?? "");
        return path;
    }

    public void SearchInto(SearchCollector collector, HistoryView history, CancellationToken cancellationToken = default,
        ISet<string>? replacedShortcutIds = null, IReadOnlySet<string>? representedExecutables = null)
    {
        Base.SearchInto(collector, history, cancellationToken, replacedShortcutIds, representedExecutables,
            this, false, _changedDirectories);
        _overlay?.SearchInto(collector, history, cancellationToken, replacedShortcutIds, representedExecutables,
            this, true, true);
    }
}
