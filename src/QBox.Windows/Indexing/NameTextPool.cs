using System.Text;
using System.Runtime.InteropServices;

namespace QBox.Windows.Indexing;

// 键直接引用输入文本，Ordinal 比较原始 UTF-16；哈希碰撞仍逐代码单元核对，不合并文件身份。
internal sealed class NameTextPool(int capacity)
{
    private const int MaximumReuseDistance = 131072;
    private Dictionary<ReadOnlyMemory<char>, int>? _offsets;
    private Dictionary<string, int>? _strings;
    private readonly StringBuilder _text = new();
    private int _sampled;
    private bool _uniqueOnly;
    public bool Shared { get; private set; }
    public int Add(string value)
    {
        if (_uniqueOnly)
        {
            var direct = _text.Length; _text.Append(value); return direct;
        }
        _strings ??= new(Math.Min(capacity, 4096), StringComparer.Ordinal);
        ref var offset = ref CollectionsMarshal.GetValueRefOrAddDefault(_strings, value, out var found);
        // 限制回指在 256 KiB 文本窗口内，保留宽查询的局部性，避免全局去重的远距离随机读取。
        if (found && _text.Length - offset <= MaximumReuseDistance) Shared = true;
        else { offset = _text.Length; _text.Append(value); }
        var result = offset;
        if (++_sampled == 4096)
        {
            // 全唯一工作负载不持续支付全盘哈希成本；显式格式转换仍用下方的完整共享路径。
            if (!Shared) { _strings = null; _uniqueOnly = true; }
            else _strings.EnsureCapacity(capacity);
        }
        return result;
    }
    public int Add(ReadOnlyMemory<char> value)
    {
        _offsets ??= new(capacity, new TextComparer());
        ref var offset = ref CollectionsMarshal.GetValueRefOrAddDefault(_offsets, value, out var found);
        if (found && _text.Length - offset <= MaximumReuseDistance) { Shared = true; return offset; }
        offset = _text.Length;
        _text.Append(value.Span);
        return offset;
    }
    public override string ToString() => _text.ToString();

    private sealed class TextComparer : IEqualityComparer<ReadOnlyMemory<char>>
    {
        public bool Equals(ReadOnlyMemory<char> left, ReadOnlyMemory<char> right) => left.Span.SequenceEqual(right.Span);
        public int GetHashCode(ReadOnlyMemory<char> value) => string.GetHashCode(value.Span, StringComparison.Ordinal);
    }
}
