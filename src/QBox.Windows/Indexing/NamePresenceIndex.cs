using System.Buffers;
using System.Numerics;
using System.Text;

namespace QBox.Windows.Indexing;

internal sealed class NamePresenceIndex
{
    private readonly ulong[]?[] _ascii = new ulong[128][];
    private readonly Dictionary<int, int[]> _unicode;
    private readonly int[] _owners;
    private readonly int _words;
    private readonly VolumeIndex _volume;

    public NamePresenceIndex(VolumeIndex volume, CancellationToken cancellationToken)
    {
        _volume = volume;
        _words = (volume.Names.Length + 63) / 64;
        _owners = new int[volume.Names.Length];
        var unicode = new Dictionary<int, List<int>>();
        for (var nodeIndex = 0; nodeIndex < volume.Nodes.Length; nodeIndex++)
        {
            if ((nodeIndex & 4095) == 0) cancellationToken.ThrowIfCancellationRequested();
            var node = volume.Nodes[nodeIndex];
            for (var index = node.FirstName; index < node.FirstName + node.NameCount; index++)
            {
                _owners[index] = nodeIndex;
                var name = volume.Names[index];
                ulong seenLow = 0, seenHigh = 0;
                foreach (var rune in volume.Text.AsSpan(name.Offset, name.Length).EnumerateRunes())
                {
                    // 与匹配器使用同一 Rune 大写映射，保留长 s、无点 i 等可匹配 ASCII 的名称。
                    var upper = Rune.ToUpperInvariant(rune).Value;
                    if (upper >= 128)
                    {
                        // 非 ASCII 字符只保存有序名称编号，避免每个汉字各占一张全盘位图。
                        if (!unicode.TryGetValue(upper, out var names)) unicode[upper] = names = [];
                        if (names.Count == 0 || names[^1] != index) names.Add(index);
                        continue;
                    }
                    var bit = 1UL << (upper & 63);
                    ref var seen = ref (upper < 64 ? ref seenLow : ref seenHigh);
                    if ((seen & bit) != 0) continue;
                    seen |= bit;
                    var bitmap = _ascii[upper] ??= new ulong[_words];
                    bitmap[index >> 6] |= 1UL << (index & 63);
                }
            }
        }
        cancellationToken.ThrowIfCancellationRequested();
        _unicode = unicode.ToDictionary(pair => pair.Key, pair => pair.Value.ToArray());
    }

    public int Owner(int name) => _owners[name];

    public IEnumerable<int> Select(string query, CancellationToken cancellationToken)
    {
        // 与 CanMatch 一样按 Rune 折叠大小写，补充平面字符不能拆成两个代理项筛选。
        var letters = query.EnumerateRunes().Select(rune => Rune.ToUpperInvariant(rune).Value).Distinct().ToArray();
        var maps = new List<ulong[]>();
        var sparse = new List<int[]>();
        foreach (var letter in letters)
        {
            if (letter < 128)
            {
                if (_ascii[letter] is not { } map) yield break;
                maps.Add(map);
            }
            else
            {
                if (!_unicode.TryGetValue(letter, out var names)) yield break;
                sparse.Add(names);
            }
        }
        var dense = maps.ToArray();
        var selected = ArrayPool<ulong>.Shared.Rent(_words);
        try
        {
            if (sparse.Count == 0)
            {
                for (var word = 0; word < _words; word++)
                {
                    if ((word & 4095) == 0) cancellationToken.ThrowIfCancellationRequested();
                    var bits = ulong.MaxValue;
                    foreach (var map in dense) bits &= map[word];
                    selected[word] = bits;
                }
            }
            else
            {
                selected.AsSpan(0, _words).Clear();
                var smallest = sparse.MinBy(names => names.Length)!;
                var scanned = 0;
                foreach (var name in smallest)
                {
                    if ((scanned++ & 4095) == 0) cancellationToken.ThrowIfCancellationRequested();
                    var bit = 1UL << (name & 63);
                    if (dense.Any(map => (map[name >> 6] & bit) == 0) ||
                        sparse.Any(names => !ReferenceEquals(names, smallest) && Array.BinarySearch(names, name) < 0)) continue;
                    // 交集只排除缺字名称；字序、重复次数和匹配分值仍由原匹配器确定。
                    selected[name >> 6] |= bit;
                }
            }
            for (var word = 0; word < _words; word++)
            {
                if ((word & 4095) == 0) cancellationToken.ThrowIfCancellationRequested();
                var bits = selected[word];
                while (bits != 0)
                {
                    var index = checked(word * 64 + BitOperations.TrailingZeroCount(bits));
                    bits &= bits - 1;
                    if (index >= _owners.Length) continue;
                    var name = _volume.Names[index];
                    if (name.Namespace != 2) continue;
                    var node = _volume.Nodes[_owners[index]];
                    var hasLong = false;
                    for (var other = node.FirstName; other < node.FirstName + node.NameCount; other++)
                    {
                        if (_volume.Names[other].Parent != name.Parent || _volume.Names[other].Namespace == 2) continue;
                        // 短名命中必须转交长名入口，不能因长名缺少查询字符而被位图排除。
                        selected[other >> 6] |= 1UL << (other & 63);
                        hasLong = true;
                    }
                    if (hasLong) selected[word] &= ~(1UL << (index & 63));
                }
            }
            for (var word = 0; word < _words; word++)
            {
                if ((word & 4095) == 0) cancellationToken.ThrowIfCancellationRequested();
                var bits = selected[word];
                while (bits != 0)
                {
                    var index = checked(word * 64 + BitOperations.TrailingZeroCount(bits));
                    bits &= bits - 1;
                    if (index < _owners.Length) yield return index;
                }
            }
        }
        finally { ArrayPool<ulong>.Shared.Return(selected); }
    }
}
