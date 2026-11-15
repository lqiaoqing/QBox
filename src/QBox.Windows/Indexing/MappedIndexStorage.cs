using System.Buffers;
using System.IO.MemoryMappedFiles;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace QBox.Windows.Indexing;

// 同一基础快照由 GUI 和工作进程只读映射；沿用 v1 的 28/17 字节记录，不复制到各自的大数组。
internal sealed unsafe class MappedIndexStorage : SafeHandle
{
    private readonly MemoryMappedFile _mapping;
    private readonly MemoryMappedViewAccessor _view;
    private readonly byte* _acquired;
    private readonly long _nodesOffset, _namesOffset, _textOffset;
    public int NodeCount { get; }
    public int NameCount { get; }
    public ReadOnlyMemory<char> Text { get; }
    public override bool IsInvalid => handle == IntPtr.Zero || handle == new IntPtr(-1);

    public MappedIndexStorage(FileStream file, long nodesOffset, int nodes, int names, int characters) : base(IntPtr.Zero, true)
    {
        if (!BitConverter.IsLittleEndian) throw new PlatformNotSupportedException("索引映射需要小端平台");
        _mapping = MemoryMappedFile.CreateFromFile(file, null, 0, MemoryMappedFileAccess.Read,
            HandleInheritability.None, leaveOpen: true);
        try { _view = _mapping.CreateViewAccessor(0, 0, MemoryMappedFileAccess.Read); }
        catch { _mapping.Dispose(); throw; }
        try
        {
            byte* pointer = null;
            _view.SafeMemoryMappedViewHandle.AcquirePointer(ref pointer);
            _acquired = pointer;
            SetHandle((IntPtr)(pointer + _view.PointerOffset));
            _nodesOffset = nodesOffset;
            _namesOffset = nodesOffset + nodes * 28L;
            _textOffset = _namesOffset + names * 17L;
            NodeCount = nodes; NameCount = names;
            Text = new MappedText(this, characters).Memory;
        }
        catch
        {
            if (IsInvalid) { _view.Dispose(); _mapping.Dispose(); }
            else Dispose();
            throw;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public IndexNode ReadNode(int index)
    {
        var p = (byte*)handle + _nodesOffset + index * 28L;
        var value = Unsafe.ReadUnaligned<IndexNode>(p);
        GC.KeepAlive(this);
        return value;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public IndexName ReadName(int index)
    {
        var p = (byte*)handle + _namesOffset + index * 17L;
        var value = Unsafe.ReadUnaligned<IndexName>(p);
        GC.KeepAlive(this);
        return value;
    }

    public ReadOnlySpan<IndexNode> NodeSpan => new((byte*)handle + _nodesOffset, NodeCount);
    public ReadOnlySpan<IndexName> NameSpan => new((byte*)handle + _namesOffset, NameCount);

    internal void PrefetchNodes(CancellationToken cancellationToken) =>
        PrefetchRegion(_nodesOffset, NodeCount * 28L, cancellationToken);

    internal void PrefetchNames(CancellationToken cancellationToken) =>
        PrefetchRegion(_namesOffset, NameCount * 17L, cancellationToken);

    private void PrefetchRegion(long offset, long length, CancellationToken cancellationToken)
    {
        if (IsInvalid || IsClosed || length <= 0) return;
        try { VirtualMemoryPrefetch.Range(handle + (nint)offset, length, cancellationToken); }
        finally { GC.KeepAlive(this); }
    }

    protected override bool ReleaseHandle()
    {
        // SafeHandle 的关键终结器兜底回收；搜索结果中的 Memory 会继续持有映射，不受换版影响。
        if (_acquired != null) _view.SafeMemoryMappedViewHandle.ReleasePointer();
        _view.Dispose(); _mapping.Dispose();
        return true;
    }

    private sealed class MappedText(MappedIndexStorage owner, int length) : MemoryManager<char>
    {
        public override Span<char> GetSpan() => new((byte*)owner.handle + owner._textOffset, length);
        public override MemoryHandle Pin(int elementIndex = 0)
        {
            if ((uint)elementIndex > (uint)length) throw new ArgumentOutOfRangeException(nameof(elementIndex));
            return new((char*)((byte*)owner.handle + owner._textOffset) + elementIndex, pinnable: this);
        }
        public override void Unpin() { }
        // 生命周期归 SafeHandle 所有；不允许一个名称切片提前关闭其他查询仍在读取的映射。
        protected override void Dispose(bool disposing) { }
    }
}

internal readonly struct IndexNodeTable
{
    private readonly IndexNode[]? _owned;
    private readonly MappedIndexStorage? _mapped;
    public int Length { get; }
    public IndexNodeTable(IndexNode[] nodes) { _owned = nodes; _mapped = null; Length = nodes.Length; }
    public IndexNodeTable(MappedIndexStorage mapped) { _owned = null; _mapped = mapped; Length = mapped.NodeCount; }
    public ReadOnlySpan<IndexNode> AsSpan() => _owned == null ? _mapped!.NodeSpan : _owned;
    public IndexNode this[int index]
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get
        {
            if ((uint)index >= (uint)Length) throw new IndexOutOfRangeException();
            return _owned == null ? _mapped!.ReadNode(index) : _owned[index];
        }
    }
    public Enumerator GetEnumerator() => new(this);
    internal void Prefetch(CancellationToken cancellationToken)
    {
        if (_owned != null) VirtualMemoryPrefetch.Array(_owned, cancellationToken);
        else _mapped?.PrefetchNodes(cancellationToken);
    }
    public struct Enumerator(IndexNodeTable table)
    {
        private int _index = -1;
        public IndexNode Current => table[_index];
        public bool MoveNext() => ++_index < table.Length;
    }
}

internal readonly struct IndexNameTable
{
    private readonly IndexName[]? _owned;
    private readonly MappedIndexStorage? _mapped;
    public int Length { get; }
    public IndexNameTable(IndexName[] names) { _owned = names; _mapped = null; Length = names.Length; }
    public IndexNameTable(MappedIndexStorage mapped) { _owned = null; _mapped = mapped; Length = mapped.NameCount; }
    public ReadOnlySpan<IndexName> AsSpan() => _owned == null ? _mapped!.NameSpan : _owned;
    public IndexName this[int index]
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get
        {
            if ((uint)index >= (uint)Length) throw new IndexOutOfRangeException();
            return _owned == null ? _mapped!.ReadName(index) : _owned[index];
        }
    }
    public Enumerator GetEnumerator() => new(this);
    internal void Prefetch(CancellationToken cancellationToken)
    {
        if (_owned != null) VirtualMemoryPrefetch.Array(_owned, cancellationToken);
        else _mapped?.PrefetchNames(cancellationToken);
    }
    public struct Enumerator(IndexNameTable table)
    {
        private int _index = -1;
        public IndexName Current => table[_index];
        public bool MoveNext() => ++_index < table.Length;
    }
}
