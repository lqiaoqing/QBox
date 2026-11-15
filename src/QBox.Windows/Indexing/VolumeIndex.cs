using System.Text;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using QBox.Core.Matching;
using QBox.Core.Search;
using QBox.Windows.Ntfs;
using QBox.Core.History;
using System.Globalization;
using System.Numerics;

namespace QBox.Windows.Indexing;

public sealed record IndexedName(FileReference Parent, string Name, byte Namespace, uint Attributes);
public sealed record IndexedFile(FileReference File, bool IsDirectory, long? LastUsn, ushort LinkCount,
    IReadOnlyList<IndexedName> Names);
// 字段偏移与 v1 磁盘布局一致，映射读取直接取值，避免每个候选重建 Nullable 等中间字段。
[StructLayout(LayoutKind.Explicit, Pack = 1, Size = 28)]
internal readonly struct IndexNode
{
    [FieldOffset(0)] public readonly ulong File;
    [FieldOffset(8)] public readonly int FirstName;
    [FieldOffset(12)] public readonly int NameCount;
    [FieldOffset(16)] private readonly byte _directory;
    [FieldOffset(17)] private readonly byte _hasUsn;
    [FieldOffset(18)] private readonly long _usn;
    [FieldOffset(26)] public readonly ushort LinkCount;
    public bool IsDirectory => _directory != 0;
    public long? LastUsn => _hasUsn != 0 ? _usn : null;
    public IndexNode(ulong file, int firstName, int nameCount, bool isDirectory, long? lastUsn, ushort linkCount)
    {
        File = file; FirstName = firstName; NameCount = nameCount;
        _directory = isDirectory ? (byte)1 : (byte)0; _hasUsn = lastUsn.HasValue ? (byte)1 : (byte)0;
        _usn = lastUsn ?? 0; LinkCount = linkCount;
    }
}

[StructLayout(LayoutKind.Explicit, Pack = 1, Size = 17)]
internal readonly struct IndexName
{
    [FieldOffset(0)] public readonly int Parent;
    [FieldOffset(4)] public readonly int Offset;
    [FieldOffset(8)] public readonly int Length;
    [FieldOffset(12)] public readonly byte Namespace;
    [FieldOffset(13)] public readonly uint Attributes;
    public IndexName(int parent, int offset, int length, byte nameSpace, uint attributes)
    { Parent = parent; Offset = offset; Length = length; Namespace = nameSpace; Attributes = attributes; }
}

public sealed class VolumeIndex
{
    internal IndexNodeTable Nodes { get; }
    internal IndexNameTable Names { get; }
    internal ReadOnlyMemory<char> Text { get; }
    internal int RootNode { get; }
    internal int FormatVersion { get; }
    public NtfsGeometry Geometry { get; }
    public JournalCheckpoint Checkpoint { get; }
    public DateTimeOffset CreatedAt { get; }
    public int FileCount => Nodes.Length;
    public int NameCount => Names.Length;
    private readonly object _presenceLock = new();
    private NamePresenceIndex? _presence;
    private PhoneticNameIndex? _phonetic;

    public void PrepareSearch(CancellationToken cancellationToken = default)
    {
        using var textLease = Text.Pin();
        lock (_presenceLock)
        {
            _presence ??= new NamePresenceIndex(this, cancellationToken);
            _phonetic ??= new PhoneticNameIndex(this, cancellationToken);
        }
    }

    private IEnumerable<int> SearchNames(string query, CancellationToken cancellationToken)
    {
        PrepareSearch(cancellationToken);
        if (query.Length == 0) return Enumerable.Range(0, Names.Length);
        return _presence!.Select(query, cancellationToken);
    }

    internal VolumeIndex(NtfsGeometry geometry, JournalCheckpoint checkpoint, DateTimeOffset createdAt,
        IndexNode[] nodes, IndexName[] names, string text)
        : this(geometry, checkpoint, createdAt, new IndexNodeTable(nodes), new IndexNameTable(names), text.AsMemory(), formatVersion: 2) { }

    internal VolumeIndex(NtfsGeometry geometry, JournalCheckpoint checkpoint, DateTimeOffset createdAt,
        IndexNodeTable nodes, IndexNameTable names, ReadOnlyMemory<char> text, CancellationToken cancellationToken = default, int formatVersion = 1)
    {
        Geometry = geometry;
        Checkpoint = checkpoint;
        CreatedAt = createdAt;
        Nodes = nodes;
        Names = names;
        Text = text;
        FormatVersion = formatVersion;
        using var textLease = Text.Pin();
        RootNode = -1;
        for (var i = 0; i < nodes.Length; i++)
            if ((nodes[i].File & 0x0000ffffffffffffUL) == 5) { RootNode = i; break; }
        Validate(cancellationToken);
    }

    public static VolumeIndex Create(NtfsGeometry geometry, JournalCheckpoint checkpoint,
        IEnumerable<IndexedFile> files, CancellationToken cancellationToken = default)
        => Create(geometry, checkpoint, files, cancellationToken, true);

    internal static VolumeIndex Create(NtfsGeometry geometry, JournalCheckpoint checkpoint,
        IEnumerable<IndexedFile> files, CancellationToken cancellationToken, bool shareNameText)
    {
        var source = files.OrderBy(f => f.File.Low).ToArray();
        var identities = new Dictionary<FileReference, int>(source.Length);
        for (var i = 0; i < source.Length; i++)
        {
            // 物理编号唯一性统一由发布前 ValidatePhysicalRecords 完整检查，避免重复的大 HashSet。
            if (source[i].File.High != 0 || !identities.TryAdd(source[i].File, i))
                throw new InvalidDataException("索引包含重复记录或不支持的文件身份");
        }
        var nodes = new IndexNode[source.Length];
        var names = new List<IndexName>();
        var pool = shareNameText ? new NameTextPool(source.Length) : null;
        var direct = pool == null ? new StringBuilder() : null;
        for (var i = 0; i < source.Length; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var file = source[i];
            var first = names.Count;
            // 长名称优先用于父目录路径；DOS 短名仍保留为可查询入口。
            foreach (var name in file.Names.OrderBy(n => n.Namespace == 2).ThenBy(n => n.Parent.Low)
                         .ThenBy(n => n.Name, StringComparer.Ordinal).DistinctBy(n => (n.Parent, n.Name)))
            {
                if (!identities.TryGetValue(name.Parent, out var parent))
                    throw new InvalidDataException($"文件 {file.File.Low:x16} 的父目录 {name.Parent.Low:x16} 缺失，索引尚未追齐");
                var offset = pool?.Add(name.Name) ?? direct!.Length;
                direct?.Append(name.Name);
                names.Add(new(parent, offset, name.Name.Length, name.Namespace, name.Attributes));
            }
            nodes[i] = new(file.File.Low, first, names.Count - first, file.IsDirectory, file.LastUsn, file.LinkCount);
        }
        return new(geometry, checkpoint, DateTimeOffset.UtcNow, new IndexNodeTable(nodes), new IndexNameTable(names.ToArray()),
            (pool?.ToString() ?? direct!.ToString()).AsMemory(), cancellationToken, pool?.Shared == true ? 2 : 1);
    }

    public VolumeIndex ShareNameText(CancellationToken cancellationToken = default, bool force = false)
    {
        // 试用准备需把已有局部共享 v2 重新整理成全局池，不能只按格式号跳过。
        if (FormatVersion == 2 && !force) return this;
        using var textLease = Text.Pin();
        var pool = new NameTextPool(Nodes.Length);
        var names = new IndexName[Names.Length];
        for (var i = 0; i < names.Length; i++)
        {
            if ((i & 4095) == 0) cancellationToken.ThrowIfCancellationRequested();
            var name = Names[i];
            names[i] = new(name.Parent, pool.Add(Text.Slice(name.Offset, name.Length)), name.Length, name.Namespace, name.Attributes);
        }
        // 格式转换不改变基础身份，已有增量日志、历史键和持有旧映射的查询仍然有效。
        return new(Geometry, Checkpoint, CreatedAt, Nodes, new IndexNameTable(names), pool.ToString().AsMemory(), cancellationToken, 2);
    }

    public IEnumerable<IndexedFile> ExportFiles()
    {
        using var textLease = Text.Pin();
        foreach (var node in Nodes)
        {
            var names = new IndexedName[node.NameCount];
            for (var i = 0; i < names.Length; i++)
            {
                var name = Names[node.FirstName + i];
                names[i] = new(new(Nodes[name.Parent].File), Text.Slice(name.Offset, name.Length).ToString(), name.Namespace, name.Attributes);
            }
            yield return new(new(node.File), node.IsDirectory, node.LastUsn, node.LinkCount, names);
        }
    }

    public IndexedFile? FindFile(FileReference id)
    {
        using var textLease = Text.Pin();
        if (id.High != 0) return null;
        var low = 0; var high = Nodes.Length - 1;
        while (low <= high)
        {
            var at = low + (high - low) / 2;
            var node = Nodes[at];
            if (node.File < id.Low) { low = at + 1; continue; }
            if (node.File > id.Low) { high = at - 1; continue; }
            var names = new IndexedName[node.NameCount];
            for (var i = 0; i < names.Length; i++)
            {
                var name = Names[node.FirstName + i];
                names[i] = new(new(Nodes[name.Parent].File), Text.Slice(name.Offset, name.Length).ToString(), name.Namespace, name.Attributes);
            }
            return new(id, node.IsDirectory, node.LastUsn, node.LinkCount, names);
        }
        return null;
    }

    public IEnumerable<SearchCandidate> Candidates(string query, CancellationToken cancellationToken = default, IReadOnlySet<string>? representedExecutables = null)
    {
        using var textLease = Text.Pin();
        var prepared = new FuzzyMatcher().Prepare(query);
        for (var nodeIndex = 0; nodeIndex < Nodes.Length; nodeIndex++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (nodeIndex == RootNode) continue;
            var node = Nodes[nodeIndex];
            for (var i = node.FirstName; i < node.FirstName + node.NameCount; i++)
            {
                var name = Names[i];
                if (name.Namespace == 2 && HasLongName(node, name.Parent)) continue;
                var raw = Text.Span.Slice(name.Offset, name.Length);
                var shortcut = !node.IsDirectory && raw.Length > 4 && raw.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase);
                var executable = !node.IsDirectory && raw.Length > 4 && raw.EndsWith(".exe", StringComparison.OrdinalIgnoreCase);
                var friendlyLength = shortcut || executable ? name.Length - 4 : name.Length;
                var friendlyMatch = prepared.CanMatch(raw[..friendlyLength], cancellationToken);
                string display;
                if (friendlyMatch || ((shortcut || executable) && prepared.CanMatch(raw, cancellationToken)))
                    display = Text.Slice(name.Offset, friendlyMatch ? friendlyLength : name.Length).ToString();
                else
                {
                    var alias = MatchingDosAlias(node, name.Parent, prepared, cancellationToken);
                    if (alias < 0) continue;
                    display = Text.Slice(Names[alias].Offset, Names[alias].Length).ToString();
                }
                // 一个文件的不同硬链接保留为不同路径；文件序列号防止复用后的记录继承旧目标身份。
                var id = $"ntfs:{Geometry.Serial:x16}:{node.File:x16}:{Nodes[name.Parent].File:x16}:{NameIdentity(raw)}";
                var application = executable && friendlyMatch && !query.Contains(".exe", StringComparison.OrdinalIgnoreCase) &&
                    !InPackageDirectory(name.Parent) && prepared.Score(display, cancellationToken) > 1 &&
                    representedExecutables?.Contains(PathFor(i)) != true;
                yield return new(id, display, PathFor(i), node.IsDirectory ? SearchItemKind.Directory :
                    shortcut ? SearchItemKind.Shortcut : application ? SearchItemKind.Application : SearchItemKind.File,
                    IsApplicationEntry: application, IsDirectExecutable: application);
            }
        }
    }

    internal IEnumerable<SearchCandidate> ShortcutCandidates(CancellationToken cancellationToken, LiveVolumeIndex? live = null, bool overlay = false, bool resolvePaths = false)
    {
        using var textLease = Text.Pin();
        var query = new FuzzyMatcher().Prepare(".lnk");
        for (var nodeIndex = 0; nodeIndex < Nodes.Length; nodeIndex++)
        {
            if ((nodeIndex & 4095) == 0) cancellationToken.ThrowIfCancellationRequested();
            var node = Nodes[nodeIndex];
            if (node.IsDirectory || live != null && !(overlay ? live.IncludesOverlay(node.File) : live.IncludesBase(node.File))) continue;
            for (var i = node.FirstName; i < node.FirstName + node.NameCount; i++)
            {
                var name = Names[i];
                var raw = Text.Span.Slice(name.Offset, name.Length);
                if (!raw.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase) || name.Namespace == 2 && HasLongName(node, name.Parent)) continue;
                var path = resolvePaths ? live!.PathFor(this, i) : PathFor(i);
                if (path == null) continue;
                // 保留原 .lnk 查询对名称和身份的选择，但仅物化真正的快捷方式，不收集全盘模糊命中。
                var display = query.CanMatch(raw[..^4], cancellationToken) ? raw[..^4].ToString() : raw.ToString();
                var id = $"ntfs:{Geometry.Serial:x16}:{node.File:x16}:{Nodes[name.Parent].File:x16}:{NameIdentity(raw)}";
                yield return new(id, display, path, SearchItemKind.Shortcut);
            }
        }
    }

    private bool HasLongName(IndexNode node, int parent)
    {
        for (var i = node.FirstName; i < node.FirstName + node.NameCount; i++)
            if (Names[i].Parent == parent && Names[i].Namespace != 2) return true;
        return false;
    }

    private int MatchingDosAlias(IndexNode node, int parent, PreparedQuery query, CancellationToken cancellationToken)
    {
        // DOS 8.3 名称是同一目录项的兼容别名，仍可命中，但不挤占一个重复结果位置。
        for (var i = node.FirstName; i < node.FirstName + node.NameCount; i++)
        {
            var name = Names[i];
            if (name.Parent == parent && name.Namespace == 2 && query.CanMatch(Text.Span.Slice(name.Offset, name.Length), cancellationToken)) return i;
        }
        return -1;
    }

    internal static string NameIdentity(ReadOnlySpan<char> value)
    {
        Span<byte> hash = stackalloc byte[32];
        SHA256.HashData(MemoryMarshal.AsBytes(value), hash);
        return Convert.ToHexStringLower(hash);
    }

    public void SearchInto(SearchCollector collector, HistoryView history, CancellationToken cancellationToken = default,
        ISet<string>? replacedShortcutIds = null, IReadOnlySet<string>? representedExecutables = null)
        => SearchInto(collector, history, cancellationToken, replacedShortcutIds, representedExecutables, null, false, false);

    internal void SearchInto(SearchCollector collector, HistoryView history, CancellationToken cancellationToken,
        ISet<string>? replacedShortcutIds, IReadOnlySet<string>? representedExecutables,
        LiveVolumeIndex? live, bool overlay, bool resolvePaths)
    {
        using var textLease = Text.Pin();
        cancellationToken.ThrowIfCancellationRequested();
        var source = new IndexSearchSource(this, history, resolvePaths ? live : null);
        foreach (var i in SearchNames(collector.Query.Text, cancellationToken))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var nodeIndex = _presence!.Owner(i);
            if (nodeIndex == RootNode) continue;
            var node = Nodes[nodeIndex];
            if (live != null && !(overlay ? live.IncludesOverlay(node.File) : live.IncludesBase(node.File))) continue;
            var name = Names[i];
            if (name.Namespace == 2 && HasLongName(node, name.Parent)) continue;
            var raw = Text.Slice(name.Offset, name.Length);
            var shortcut = !node.IsDirectory && name.Length > 4 && raw.Span.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase);
            var executable = !node.IsDirectory && name.Length > 4 && raw.Span.EndsWith(".exe", StringComparison.OrdinalIgnoreCase);
            var friendly = shortcut || executable ? raw[..^4] : raw;
            ReadOnlyMemory<char> display;
            var friendlyMatch = collector.Query.CanMatch(friendly.Span, cancellationToken);
            if (friendlyMatch) display = friendly;
            else if ((shortcut || executable) && collector.Query.CanMatch(raw.Span, cancellationToken)) display = raw;
            else
            {
                var alias = MatchingDosAlias(node, name.Parent, collector.Query, cancellationToken);
                if (alias < 0) continue;
                display = Text.Slice(Names[alias].Offset, Names[alias].Length);
            }
            // 无开始菜单入口的便携 EXE 仍可直接启动；包目录中的 EXE 由注册入口负责激活。
            if (resolvePaths && !live!.HasPath(this, i)) continue;
            // 普通候选只验证父链；完整路径留给 EXE 来源判定和最终展示的少量结果。
            var livePath = resolvePaths && executable ? live!.PathFor(this, i) : null;
            var application = executable && friendlyMatch && !collector.Query.Text.Contains(".exe", StringComparison.OrdinalIgnoreCase) &&
                !(resolvePaths ? InPackagePath(livePath!) : InPackageDirectory(name.Parent)) && collector.Query.Score(display.Span, cancellationToken) > 1 &&
                representedExecutables?.Contains(livePath ?? PathFor(i)) != true;
            var kind = node.IsDirectory ? SearchItemKind.Directory : shortcut ? SearchItemKind.Shortcut :
                application ? SearchItemKind.Application : SearchItemKind.File;
            var key = ((ulong)(uint)nodeIndex << 32) | (uint)i;
            if (shortcut && replacedShortcutIds?.Contains(source.Identity(key)) == true) continue;
            collector.AddMatched(source, key, display, kind, 0, source.Signals(node, name), application, application);
        }
        foreach (var entry in _phonetic!.Select(collector.Query.Text, cancellationToken))
        {
            var node = Nodes[entry.Node]; var name = Names[entry.Name];
            if (entry.Node == RootNode || live != null && !(overlay ? live.IncludesOverlay(node.File) : live.IncludesBase(node.File))) continue;
            if (name.Namespace == 2 && HasLongName(node, name.Parent)) continue;
            var raw = Text.Slice(name.Offset, name.Length);
            var display = entry.Prepared.Text.AsMemory();
            // 与字面入口互斥，包含 DOS 别名的目标也只统计一次。
            if (collector.Query.CanMatch(display.Span, cancellationToken) || collector.Query.CanMatch(raw.Span, cancellationToken) ||
                MatchingDosAlias(node, name.Parent, collector.Query, cancellationToken) >= 0) continue;
            var match = entry.Prepared.MatchPhonetic(collector.Query.Text, true, cancellationToken);
            if (match.Score == 0) continue;
            var path = resolvePaths ? live!.PathFor(this, entry.Name) : PathFor(entry.Name);
            if (path == null) continue;
            var shortcut = !node.IsDirectory && raw.Span.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase);
            var executable = !node.IsDirectory && raw.Span.EndsWith(".exe", StringComparison.OrdinalIgnoreCase);
            var application = executable && !InPackagePath(path) && representedExecutables?.Contains(path) != true;
            var kind = node.IsDirectory ? SearchItemKind.Directory : shortcut ? SearchItemKind.Shortcut : application ? SearchItemKind.Application : SearchItemKind.File;
            var key = ((ulong)(uint)entry.Node << 32) | (uint)entry.Name;
            if (shortcut && replacedShortcutIds?.Contains(source.Identity(key)) == true) continue;
            collector.AddMatched(source, key, display, kind, 0, source.Signals(node, name), application, application,
                match.PhoneticMatch, supplemental: true);
        }
    }


    private bool InPackagePath(string path) => path.StartsWith(Geometry.Root + @"WindowsApps\", StringComparison.OrdinalIgnoreCase) ||
        path.StartsWith(Geometry.Root + @"Program Files\WindowsApps\", StringComparison.OrdinalIgnoreCase);

    private bool InPackageDirectory(int parent)
    {
        while (parent != RootNode)
        {
            var name = Names[Nodes[parent].FirstName];
            if (Text.Span.Slice(name.Offset, name.Length).Equals("WindowsApps", StringComparison.OrdinalIgnoreCase))
            {
                if (name.Parent == RootNode) return true;
                var container = Names[Nodes[name.Parent].FirstName];
                if (container.Parent == RootNode && Text.Span.Slice(container.Offset, container.Length).Equals("Program Files", StringComparison.OrdinalIgnoreCase)) return true;
            }
            parent = name.Parent;
        }
        return false;
    }

    private sealed class IndexSearchSource : IUniqueSearchSource
    {
        private readonly VolumeIndex _index;
        private readonly LiveVolumeIndex? _live;
        private readonly Dictionary<(ulong File, ulong Parent), List<(string Hash, HistorySignals Signals)>> _history = [];

        public IndexSearchSource(VolumeIndex index, HistoryView history, LiveVolumeIndex? live = null)
        {
            _index = index;
            _live = live;
            foreach (var entry in history.EnumerateSignals())
            {
                var fields = entry.TargetId.Split(':');
                if (fields.Length != 5 || fields[0] != "ntfs" ||
                    !ulong.TryParse(fields[1], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var serial) || serial != index.Geometry.Serial ||
                    !ulong.TryParse(fields[2], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var file) ||
                    !ulong.TryParse(fields[3], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var parent)) continue;
                if (!_history.TryGetValue((file, parent), out var list)) _history[(file, parent)] = list = [];
                list.Add((fields[4], entry.Signals));
            }
        }

        public HistorySignals Signals(IndexNode node, IndexName name)
        {
            if (!_history.TryGetValue((node.File, _index.Nodes[name.Parent].File), out var list)) return default;
            var hash = NameIdentity(_index.Text.Span.Slice(name.Offset, name.Length));
            foreach (var entry in list) if (entry.Hash == hash) return entry.Signals;
            return default;
        }

        public SearchCandidate Materialize(ulong key, ReadOnlyMemory<char> display, SearchItemKind kind, ulong priority)
        {
            var nameIndex = (int)(uint)key;
            return new(Identity(key), display.ToString(), _live?.PathFor(_index, nameIndex) ?? _index.PathFor(nameIndex), kind, NormalPriority: priority);
        }

        public string Identity(ulong key)
        {
            using var textLease = _index.Text.Pin();
            var node = _index.Nodes[(int)(key >> 32)];
            var name = _index.Names[(int)(uint)key];
            return $"ntfs:{_index.Geometry.Serial:x16}:{node.File:x16}:{_index.Nodes[name.Parent].File:x16}:" +
                NameIdentity(_index.Text.Span.Slice(name.Offset, name.Length));
        }

        public int CompareIdentity(ulong key, IUniqueSearchSource other, ulong otherKey)
        {
            if (other is not IndexSearchSource native) return StringComparer.Ordinal.Compare(Identity(key), other.Identity(otherKey));
            using var leftLease = _index.Text.Pin();
            using var rightLease = native._index.Text.Pin();
            var compare = _index.Geometry.Serial.CompareTo(native._index.Geometry.Serial);
            if (compare != 0) return compare;
            compare = _index.Nodes[(int)(key >> 32)].File.CompareTo(native._index.Nodes[(int)(otherKey >> 32)].File);
            if (compare != 0) return compare;
            var name = _index.Names[(int)(uint)key];
            var otherName = native._index.Names[(int)(uint)otherKey];
            compare = _index.Nodes[name.Parent].File.CompareTo(native._index.Nodes[otherName.Parent].File);
            if (compare != 0) return compare;
            return StringComparer.Ordinal.Compare(NameIdentity(_index.Text.Span.Slice(name.Offset, name.Length)),
                NameIdentity(native._index.Text.Span.Slice(otherName.Offset, otherName.Length)));
        }
    }

    private string PathFor(int nameIndex)
    {
        using var textLease = Text.Pin();
        var parts = new List<int> { nameIndex };
        var parent = Names[nameIndex].Parent;
        while (parent != RootNode)
        {
            var first = Nodes[parent].FirstName;
            parts.Add(first);
            parent = Names[first].Parent;
        }
        var result = new StringBuilder(Geometry.Root);
        for (var i = parts.Count - 1; i >= 0; i--)
        {
            var part = Names[parts[i]];
            result.Append(Text.Span.Slice(part.Offset, part.Length));
            if (i != 0) result.Append('\\');
        }
        return result.ToString();
    }

    private void Validate(CancellationToken cancellationToken)
    {
        if (Geometry.Root.Length != 3 || !char.IsAsciiLetterUpper(Geometry.Root[0]) || Geometry.Root[1..] != @":\" ||
            RootNode < 0 || !Nodes[RootNode].IsDirectory || Checkpoint.NextUsn < 0)
            throw new InvalidDataException("索引卷身份或根目录无效");
        // 校验物理编号仍然完整；紧凑排序数组替代数百万项 HashSet 的临时容量开销。
        ValidatePhysicalRecords(cancellationToken);
        var end = 0;
        ulong? previousFile = null;
        foreach (var node in Nodes)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (previousFile is { } previous && node.File <= previous)
                throw new InvalidDataException("索引文件身份顺序无效");
            previousFile = node.File;
            if (node.FirstName != end || node.NameCount <= 0 ||
                node.NameCount > Names.Length - end)
                throw new InvalidDataException("索引文件记录或名称范围无效");
            end += node.NameCount;
        }
        if (end != Names.Length) throw new InvalidDataException("索引存在未归属名称");
        if (FormatVersion == 1) ValidateSequentialText(cancellationToken);
        else
        {
            var textEnd = 0;
            // v2 的首次引用按顺序铺满名称池，后续引用必须指向完整的已有片段；位图仅在校验期间存在。
            var starts = new ulong[(int)((Text.Length + 63L) / 64)];
            for (var i = 0; i < Names.Length; i++)
            {
                if ((i & 4095) == 0) cancellationToken.ThrowIfCancellationRequested();
                var name = Names[i];
                if ((uint)name.Parent >= Nodes.Length || !Nodes[name.Parent].IsDirectory || name.Namespace > 3 ||
                    name.Offset < 0 || name.Offset > textEnd || name.Length <= 0 || name.Length > Text.Length - name.Offset)
                    throw new InvalidDataException("索引名称或父目录无效");
                if (name.Offset != textEnd)
                {
                    if ((starts[name.Offset >> 6] & (1UL << (name.Offset & 63))) == 0 ||
                        NextTextBoundary(starts, name.Offset, textEnd, cancellationToken) - name.Offset != name.Length)
                        throw new InvalidDataException("共享名称未指向完整文本片段");
                    continue;
                }
                var value = Text.Span.Slice(name.Offset, name.Length);
                if (value.ContainsAny('\\', '/', '\0') || value.SequenceEqual(".."))
                    throw new InvalidDataException("索引名称包含非法路径分量");
                starts[textEnd >> 6] |= 1UL << (textEnd & 63);
                textEnd += name.Length;
            }
            if (textEnd != Text.Length) throw new InvalidDataException("索引存在未归属名称字符");
        }
        // 每个名称均验证到根目录，重命名、父目录删除和记录复用不能留下可打开的伪路径。
        var state = new byte[Nodes.Length];
        state[RootNode] = 2;
        var chain = new List<int>();
        for (var i = 0; i < Nodes.Length; i++)
        {
            if ((i & 4095) == 0) cancellationToken.ThrowIfCancellationRequested();
            chain.Clear();
            var current = i;
            while (state[current] == 0)
            {
                state[current] = 1;
                chain.Add(current);
                current = Names[Nodes[current].FirstName].Parent;
            }
            if (state[current] == 1) throw new InvalidDataException("索引目录关系存在循环");
            foreach (var item in chain) state[item] = 2;
        }
        for (var i = 0; i < Nodes.Length; i++)
            for (var j = Nodes[i].FirstName; j < Nodes[i].FirstName + Nodes[i].NameCount; j++)
                if (i != RootNode && (Names[j].Parent == i || Text.Span.Slice(Names[j].Offset, Names[j].Length).SequenceEqual(".")))
                    throw new InvalidDataException("非根目录名称不能引用自身或使用点目录");
    }

    private void ValidateSequentialText(CancellationToken cancellationToken)
    {
        // v1 保持原连续校验热路径，不为不共享的快照支付 v2 偏移与边界判断成本。
        var end = 0;
        for (var i = 0; i < Names.Length; i++)
        {
            if ((i & 4095) == 0) cancellationToken.ThrowIfCancellationRequested();
            var name = Names[i];
            if ((uint)name.Parent >= Nodes.Length || !Nodes[name.Parent].IsDirectory || name.Namespace > 3 ||
                name.Offset != end || name.Length <= 0 || name.Length > Text.Length - end)
                throw new InvalidDataException("索引名称或父目录无效");
            var value = Text.Span.Slice(name.Offset, name.Length);
            if (value.ContainsAny('\\', '/', '\0') || value.SequenceEqual(".."))
                throw new InvalidDataException("索引名称包含非法路径分量");
            end += name.Length;
        }
        if (end != Text.Length) throw new InvalidDataException("索引存在未归属名称字符");
    }

    private static int NextTextBoundary(ulong[] starts, int offset, int end, CancellationToken token)
    {
        var next = offset + 1;
        var word = next >> 6;
        if (word >= starts.Length) return end;
        var bits = starts[word] & (ulong.MaxValue << (next & 63));
        while (bits == 0)
        {
            if ((word & 4095) == 0) token.ThrowIfCancellationRequested();
            if (++word >= starts.Length || word * 64L >= end) return end;
            bits = starts[word];
        }
        return Math.Min(end, word * 64 + BitOperations.TrailingZeroCount(bits));
    }

    private void ValidatePhysicalRecords(CancellationToken cancellationToken)
    {
        var physical = new ulong[Nodes.Length];
        for (var i = 0; i < physical.Length; i++)
        {
            if ((i & 4095) == 0) cancellationToken.ThrowIfCancellationRequested();
            physical[i] = Nodes[i].File & 0x0000ffffffffffffUL;
        }
        Array.Sort(physical);
        cancellationToken.ThrowIfCancellationRequested();
        for (var i = 1; i < physical.Length; i++)
            if (physical[i] == physical[i - 1]) throw new InvalidDataException("索引包含重复物理记录");
    }
}
