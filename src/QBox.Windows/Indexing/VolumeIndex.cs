using System.Text;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using QBox.Core.Matching;
using QBox.Core.Search;
using QBox.Windows.Ntfs;
using QBox.Core.History;
using System.Globalization;

namespace QBox.Windows.Indexing;

public sealed record IndexedName(FileReference Parent, string Name, byte Namespace, uint Attributes);
public sealed record IndexedFile(FileReference File, bool IsDirectory, long? LastUsn, ushort LinkCount,
    IReadOnlyList<IndexedName> Names);
internal readonly record struct IndexNode(ulong File, int FirstName, int NameCount, bool IsDirectory, long? LastUsn, ushort LinkCount);
internal readonly record struct IndexName(int Parent, int Offset, int Length, byte Namespace, uint Attributes);

public sealed class VolumeIndex
{
    internal IndexNode[] Nodes { get; }
    internal IndexName[] Names { get; }
    internal string Text { get; }
    internal int RootNode { get; }
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
    {
        Geometry = geometry;
        Checkpoint = checkpoint;
        CreatedAt = createdAt;
        Nodes = nodes;
        Names = names;
        Text = text;
        RootNode = Array.FindIndex(nodes, n => (n.File & 0x0000ffffffffffffUL) == 5);
        Validate();
    }

    public static VolumeIndex Create(NtfsGeometry geometry, JournalCheckpoint checkpoint,
        IEnumerable<IndexedFile> files, CancellationToken cancellationToken = default)
    {
        var source = files.OrderBy(f => f.File.Low).ToArray();
        var identities = new Dictionary<FileReference, int>(source.Length);
        var recordNumbers = new HashSet<ulong>();
        for (var i = 0; i < source.Length; i++)
        {
            if (source[i].File.High != 0 || !identities.TryAdd(source[i].File, i) ||
                !recordNumbers.Add(source[i].File.Low & 0x0000ffffffffffffUL))
                throw new InvalidDataException("索引包含重复记录或不支持的文件身份");
        }
        var nodes = new IndexNode[source.Length];
        var names = new List<IndexName>();
        var text = new StringBuilder();
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
                names.Add(new(parent, text.Length, name.Name.Length, name.Namespace, name.Attributes));
                text.Append(name.Name);
            }
            nodes[i] = new(file.File.Low, first, names.Count - first, file.IsDirectory, file.LastUsn, file.LinkCount);
        }
        return new(geometry, checkpoint, DateTimeOffset.UtcNow, nodes, names.ToArray(), text.ToString());
    }

    public IEnumerable<IndexedFile> ExportFiles()
    {
        foreach (var node in Nodes)
        {
            var names = new IndexedName[node.NameCount];
            for (var i = 0; i < names.Length; i++)
            {
                var name = Names[node.FirstName + i];
                names[i] = new(new(Nodes[name.Parent].File), Text.Substring(name.Offset, name.Length), name.Namespace, name.Attributes);
            }
            yield return new(new(node.File), node.IsDirectory, node.LastUsn, node.LinkCount, names);
        }
    }

    public IndexedFile? FindFile(FileReference id)
    {
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
                names[i] = new(new(Nodes[name.Parent].File), Text.Substring(name.Offset, name.Length), name.Namespace, name.Attributes);
            }
            return new(id, node.IsDirectory, node.LastUsn, node.LinkCount, names);
        }
        return null;
    }

    public IEnumerable<SearchCandidate> Candidates(string query, CancellationToken cancellationToken = default, IReadOnlySet<string>? representedExecutables = null)
    {
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
                var raw = Text.AsSpan(name.Offset, name.Length);
                var shortcut = !node.IsDirectory && raw.Length > 4 && raw.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase);
                var executable = !node.IsDirectory && raw.Length > 4 && raw.EndsWith(".exe", StringComparison.OrdinalIgnoreCase);
                var friendlyLength = shortcut || executable ? name.Length - 4 : name.Length;
                var friendlyMatch = prepared.CanMatch(raw[..friendlyLength], cancellationToken);
                string display;
                if (friendlyMatch || ((shortcut || executable) && prepared.CanMatch(raw, cancellationToken)))
                    display = Text.Substring(name.Offset, friendlyMatch ? friendlyLength : name.Length);
                else
                {
                    var alias = MatchingDosAlias(node, name.Parent, prepared, cancellationToken);
                    if (alias < 0) continue;
                    display = Text.Substring(Names[alias].Offset, Names[alias].Length);
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
            if (name.Parent == parent && name.Namespace == 2 && query.CanMatch(Text.AsSpan(name.Offset, name.Length), cancellationToken)) return i;
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
            var raw = Text.AsMemory(name.Offset, name.Length);
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
                display = Text.AsMemory(Names[alias].Offset, Names[alias].Length);
            }
            // 无开始菜单入口的便携 EXE 仍可直接启动；包目录中的 EXE 由注册入口负责激活。
            var livePath = resolvePaths ? live!.PathFor(this, i) : null;
            if (resolvePaths && livePath == null) continue;
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
            var raw = Text.AsMemory(name.Offset, name.Length);
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
            if (Text.AsSpan(name.Offset, name.Length).Equals("WindowsApps", StringComparison.OrdinalIgnoreCase))
            {
                if (name.Parent == RootNode) return true;
                var container = Names[Nodes[name.Parent].FirstName];
                if (container.Parent == RootNode && Text.AsSpan(container.Offset, container.Length).Equals("Program Files", StringComparison.OrdinalIgnoreCase)) return true;
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
            var hash = NameIdentity(_index.Text.AsSpan(name.Offset, name.Length));
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
            var node = _index.Nodes[(int)(key >> 32)];
            var name = _index.Names[(int)(uint)key];
            return $"ntfs:{_index.Geometry.Serial:x16}:{node.File:x16}:{_index.Nodes[name.Parent].File:x16}:" +
                NameIdentity(_index.Text.AsSpan(name.Offset, name.Length));
        }

        public int CompareIdentity(ulong key, IUniqueSearchSource other, ulong otherKey)
        {
            if (other is not IndexSearchSource native) return StringComparer.Ordinal.Compare(Identity(key), other.Identity(otherKey));
            var compare = _index.Geometry.Serial.CompareTo(native._index.Geometry.Serial);
            if (compare != 0) return compare;
            compare = _index.Nodes[(int)(key >> 32)].File.CompareTo(native._index.Nodes[(int)(otherKey >> 32)].File);
            if (compare != 0) return compare;
            var name = _index.Names[(int)(uint)key];
            var otherName = native._index.Names[(int)(uint)otherKey];
            compare = _index.Nodes[name.Parent].File.CompareTo(native._index.Nodes[otherName.Parent].File);
            if (compare != 0) return compare;
            return StringComparer.Ordinal.Compare(NameIdentity(_index.Text.AsSpan(name.Offset, name.Length)),
                NameIdentity(native._index.Text.AsSpan(otherName.Offset, otherName.Length)));
        }
    }

    private string PathFor(int nameIndex)
    {
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
            result.Append(Text.AsSpan(part.Offset, part.Length));
            if (i != 0) result.Append('\\');
        }
        return result.ToString();
    }

    private void Validate()
    {
        if (Geometry.Root.Length != 3 || !char.IsAsciiLetterUpper(Geometry.Root[0]) || Geometry.Root[1..] != @":\" ||
            RootNode < 0 || !Nodes[RootNode].IsDirectory || Checkpoint.NextUsn < 0)
            throw new InvalidDataException("索引卷身份或根目录无效");
        var physical = new HashSet<ulong>();
        var end = 0;
        ulong? previousFile = null;
        foreach (var node in Nodes)
        {
            if (previousFile is { } previous && node.File <= previous)
                throw new InvalidDataException("索引文件身份顺序无效");
            previousFile = node.File;
            if (!physical.Add(node.File & 0x0000ffffffffffffUL) || node.FirstName != end || node.NameCount <= 0 ||
                node.NameCount > Names.Length - end)
                throw new InvalidDataException("索引文件记录或名称范围无效");
            end += node.NameCount;
        }
        if (end != Names.Length) throw new InvalidDataException("索引存在未归属名称");
        var textEnd = 0;
        for (var i = 0; i < Names.Length; i++)
        {
            var name = Names[i];
            if ((uint)name.Parent >= Nodes.Length || !Nodes[name.Parent].IsDirectory || name.Namespace > 3 ||
                name.Offset != textEnd || name.Length <= 0 || name.Length > Text.Length - textEnd)
                throw new InvalidDataException("索引名称或父目录无效");
            var value = Text.AsSpan(name.Offset, name.Length);
            if (value.ContainsAny('\\', '/', '\0') || value.SequenceEqual(".."))
                throw new InvalidDataException("索引名称包含非法路径分量");
            textEnd += name.Length;
        }
        if (textEnd != Text.Length) throw new InvalidDataException("索引存在未归属名称字符");
        // 每个名称均验证到根目录，重命名、父目录删除和记录复用不能留下可打开的伪路径。
        var state = new byte[Nodes.Length];
        state[RootNode] = 2;
        for (var i = 0; i < Nodes.Length; i++)
        {
            var chain = new List<int>();
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
                if (i != RootNode && (Names[j].Parent == i || Text.AsSpan(Names[j].Offset, Names[j].Length).SequenceEqual(".")))
                    throw new InvalidDataException("非根目录名称不能引用自身或使用点目录");
    }
}
