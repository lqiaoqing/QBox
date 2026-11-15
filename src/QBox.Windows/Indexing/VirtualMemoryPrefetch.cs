using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace QBox.Windows.Indexing;

// 按块预取已映射或已钉住的页，供窗口唤起时恢复搜索热路径；失败时改为逐页轻触。
internal static unsafe class VirtualMemoryPrefetch
{
    public const int ChunkBytes = 2 * 1024 * 1024;
    private const int PageSize = 4096;

    public static void Range(nint address, long length, CancellationToken cancellationToken)
    {
        if (address == 0 || length <= 0) return;
        for (long offset = 0; offset < length; offset += ChunkBytes)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var size = (nuint)Math.Min(ChunkBytes, length - offset);
            var start = address + (nint)offset;
            if (!TryPrefetch(start, size)) Touch(start, size);
        }
    }

    public static void Array<T>(T[]? data, CancellationToken cancellationToken) where T : unmanaged
    {
        if (data is not { Length: > 0 }) return;
        var handle = GCHandle.Alloc(data, GCHandleType.Pinned);
        try { Range(handle.AddrOfPinnedObject(), (long)data.Length * Unsafe.SizeOf<T>(), cancellationToken); }
        finally { handle.Free(); }
    }

    private static bool TryPrefetch(nint address, nuint size)
    {
        try
        {
            var entry = new MemoryRangeEntry { VirtualAddress = address, NumberOfBytes = size };
            return PrefetchVirtualMemory(GetCurrentProcess(), 1, entry, 0);
        }
        catch (Exception error) when (error is EntryPointNotFoundException or DllNotFoundException)
        {
            return false;
        }
    }

    private static void Touch(nint address, nuint size)
    {
        var p = (byte*)address;
        var end = p + size;
        byte acc = 0;
        for (var cursor = p; cursor < end; cursor += PageSize)
            acc ^= Volatile.Read(ref *cursor);
        if (size != 0) acc ^= Volatile.Read(ref end[-1]);
        if (acc == 0x7F && size == ulong.MaxValue) Volatile.Write(ref *p, acc);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryRangeEntry
    {
        public nint VirtualAddress;
        public nuint NumberOfBytes;
    }

    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PrefetchVirtualMemory(nint process, nuint entries, in MemoryRangeEntry ranges, uint flags);

    [DllImport("kernel32.dll", ExactSpelling = true)]
    private static extern nint GetCurrentProcess();
}