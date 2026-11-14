using System.Buffers;
using System.Numerics;
using System.Text;

namespace QBox.Windows.Indexing;

internal sealed class NamePresenceIndex
{
    private readonly ulong[]?[] _ascii = new ulong[128][];
    private readonly int[] _owners;
    private readonly int _words;
    private readonly VolumeIndex _volume;

    public NamePresenceIndex(VolumeIndex volume, CancellationToken cancellationToken)
    {
        _volume = volume;
        _words = (volume.Names.Length + 63) / 64;
        _owners = new int[volume.Names.Length];
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
                    if (upper >= 128) continue;
                    var bit = 1UL << (upper & 63);
                    ref var seen = ref (upper < 64 ? ref seenLow : ref seenHigh);
                    if ((seen & bit) != 0) continue;
                    seen |= bit;
                    var bitmap = _ascii[upper] ??= new ulong[_words];
                    bitmap[index >> 6] |= 1UL << (index & 63);
                }
            }
        }
    }

    public int Owner(int name) => _owners[name];

    public IEnumerable<int> Select(string query, CancellationToken cancellationToken)
    {
        var letters = query.Select(letter => char.ToUpperInvariant(letter)).Distinct().ToArray();
        var maps = letters.Select(letter => _ascii[letter]).ToArray();
        if (maps.Any(map => map == null)) yield break;
        var selected = ArrayPool<ulong>.Shared.Rent(_words);
        try
        {
            // 字符交集只提供必要条件，重复字符数量和先后关系仍由完整匹配器核对。
            for (var word = 0; word < _words; word++)
            {
                if ((word & 4095) == 0) cancellationToken.ThrowIfCancellationRequested();
                var bits = ulong.MaxValue;
                foreach (var map in maps) bits &= map![word];
                selected[word] = bits;
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
