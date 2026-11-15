using QBox.Core.Matching;

namespace QBox.Windows.Indexing;

// 只为含读音表汉字的名称准备拼音，不为数百万个 ASCII 名称建立额外对象。
internal sealed class PhoneticNameIndex
{
    internal readonly record struct Entry(int Node, int Name, PreparedApplicationName Prepared);
    private readonly Entry[] _entries;
    public PhoneticNameIndex(VolumeIndex index, CancellationToken cancellationToken)
    {
        var entries = new List<Entry>();
        var shared = new Dictionary<string, PreparedApplicationName>(StringComparer.Ordinal);
        for (var nodeIndex = 0; nodeIndex < index.Nodes.Length; nodeIndex++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var node = index.Nodes[nodeIndex];
            for (var i = node.FirstName; i < node.FirstName + node.NameCount; i++)
            {
                var name = index.Names[i];
                var raw = index.Text.Span.Slice(name.Offset, name.Length);
                if (!PreparedApplicationName.CanContainPhonetic(raw)) continue;
                if (!node.IsDirectory && (raw.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase) || raw.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))) raw = raw[..^4];
                var text = raw.ToString();
                if (!shared.TryGetValue(text, out var prepared)) shared[text] = prepared = new(text);
                entries.Add(new(nodeIndex, i, prepared));
            }
        }
        _entries = entries.ToArray();
    }

    public IEnumerable<Entry> Select(string query, CancellationToken cancellationToken)
    {
        if (query.Length is 0 or > 128 || !query.All(char.IsAsciiLetterOrDigit)) yield break;
        ulong mask = 0;
        foreach (var letter in query)
        {
            var lower = char.ToLowerInvariant(letter);
            if (lower is >= 'a' and <= 'z') mask |= 1UL << (lower - 'a');
            else mask |= 1UL << (26 + lower - '0');
        }
        foreach (var entry in _entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if ((entry.Prepared.PhoneticLetters & mask) == mask) yield return entry;
        }
    }

    internal void Prefetch(CancellationToken cancellationToken)
    {
        if (_entries.Length == 0) return;
        var handle = System.Runtime.InteropServices.GCHandle.Alloc(_entries, System.Runtime.InteropServices.GCHandleType.Pinned);
        try
        {
            VirtualMemoryPrefetch.Range(handle.AddrOfPinnedObject(),
                (long)_entries.Length * System.Runtime.CompilerServices.Unsafe.SizeOf<Entry>(), cancellationToken);
        }
        finally { handle.Free(); }
        // Entry 含引用类型，再轻触 Prepared 把拼音热对象拉回工作集。
        for (var i = 0; i < _entries.Length; i++)
        {
            if ((i & 4095) == 0) cancellationToken.ThrowIfCancellationRequested();
            _ = _entries[i].Prepared.PhoneticLetters;
        }
    }
}
