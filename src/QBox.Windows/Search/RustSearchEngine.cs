using System.Buffers.Binary;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using QBox.Core.History;
using QBox.Core.Matching;
using QBox.Core.Search;

namespace QBox.Windows.Search;

public enum RustRefreshResult { Unchanged, Refreshed, BaseChanged }

// 进程内 sidecar 薄包装：结果仍是现有 SearchResponse 类型，调用方无需理解二进制协议。
public sealed class RustSearchEngine : IDisposable
{
    private const string LibraryName = "qbox_engine";
    private readonly object _gate = new();
    private IntPtr _handle;
    private int _activeSearches;
    private long _requestId;
    private bool _disposed;

    private RustSearchEngine(IntPtr handle) => _handle = handle;

    public static RustSearchEngine Open(string indexDirectory)
    {
        QboxEngineNative.EnsureResolver();
        indexDirectory = Path.GetFullPath(indexDirectory);
        var error = new char[1024];
        int status;
        IntPtr handle;
        unsafe
        {
            fixed (char* directory = indexDirectory)
            fixed (char* errorText = error)
                status = QboxEngineNative.Open(directory, (nuint)indexDirectory.Length, 0, out handle, errorText, (nuint)error.Length);
        }
        if (status != 0)
        {
            var length = Array.IndexOf(error, '\0');
            throw new InvalidOperationException(length <= 0 ? "Rust 搜索引擎加载失败" : new string(error, 0, length));
        }
        var engine = new RustSearchEngine(handle);
        status = QboxEngineNative.Prepare(handle);
        if (status != 0)
        {
            engine.Dispose();
            throw new InvalidOperationException("Rust 搜索引擎准备失败");
        }
        return engine;
    }

    public RustRefreshResult Refresh(EngineCatalogSnapshot? catalogs)
    {
        var snapshot = catalogs?.Serialize() ?? [];
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            ThrowIfClosed();
            while (_activeSearches > 0) Monitor.Wait(_gate);
            int status;
            unsafe
            {
                fixed (byte* bytes = snapshot)
                    status = QboxEngineNative.Refresh(_handle, bytes, (nuint)snapshot.Length);
            }
            return status switch
            {
                0 => snapshot.Length == 0 ? RustRefreshResult.Unchanged : RustRefreshResult.Refreshed,
                2 => RustRefreshResult.BaseChanged,
                _ => throw new InvalidOperationException(LastError()),
            };
        }
    }

    public SearchResponse Search(string query, int limit, HistoryView history, CancellationToken cancellationToken = default)
    {
        var historyBlob = EngineHistoryBlob.Serialize(history);
        IntPtr handle;
        long requestId;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            ThrowIfClosed();
            handle = _handle;
            requestId = ++_requestId;
            _activeSearches++;
        }
        var registration = cancellationToken.Register(static state =>
        {
            var (handle, request) = ((IntPtr Handle, long Request))state!;
            QboxEngineNative.Cancel(handle, request);
        }, (handle, requestId));
        try
        {
            int status;
            QboxEngineNative.NativeBuffer buffer;
            unsafe
            {
                fixed (char* queryText = query)
                fixed (byte* historyBytes = historyBlob)
                    status = QboxEngineNative.Search(handle, requestId, queryText, (nuint)query.Length, limit,
                        historyBytes, (nuint)historyBlob.Length, out buffer);
            }
            // 原生搜索成功后才到达的取消也会走取消分支，释放缓冲区必须覆盖取消与错误路径。
            try
            {
                if (status == 1 || cancellationToken.IsCancellationRequested)
                    throw new OperationCanceledException(cancellationToken);
                if (status != 0) throw new InvalidOperationException(LastError());
                return ParseResponse(buffer);
            }
            finally { QboxEngineNative.FreeBuffer(buffer); }
        }
        finally
        {
            // 先注销回调（并等待已在执行的回调结束）再减少活跃计数：计数归零后 Dispose/Refresh
            // 会立即释放原生句柄，此时尚未注销的迟到取消回调将访问已释放内存。
            registration.Dispose();
            lock (_gate)
            {
                _activeSearches--;
                if (_activeSearches == 0) Monitor.PulseAll(_gate);
            }
        }
    }

    private string LastError()
    {
        var buffer = new char[1024];
        int length;
        unsafe
        {
            fixed (char* text = buffer)
                length = QboxEngineNative.LastError(_handle, text, (nuint)buffer.Length);
        }
        return length <= 0 ? "Rust 搜索引擎调用失败" : new string(buffer, 0, length);
    }

    private void ThrowIfClosed()
    {
        if (_handle == IntPtr.Zero) throw new InvalidOperationException("Rust 搜索引擎尚未打开");
    }

    internal static SearchResponse ParseResponse(QboxEngineNative.NativeBuffer buffer)
    {
        if (buffer.Pointer == IntPtr.Zero || buffer.Length < 24) throw new InvalidDataException("Rust 引擎结果为空");
        var bytes = new byte[(int)buffer.Length];
        Marshal.Copy(buffer.Pointer, bytes, 0, bytes.Length);
        if (!bytes.AsSpan(0, 8).SequenceEqual("QBXRES01"u8)) throw new InvalidDataException("Rust 引擎结果标识无效");
        var cursor = 8;
        var version = ReadUInt32(bytes, ref cursor);
        if (version != 1) throw new InvalidDataException("Rust 引擎结果版本无效");
        var total = ReadInt32(bytes, ref cursor);
        var count = checked((int)ReadUInt32(bytes, ref cursor));
        var query = ReadString(bytes, ref cursor, checked((int)ReadUInt32(bytes, ref cursor)));
        var items = new SearchHit[count];
        for (var i = 0; i < count; i++)
        {
            var kind = (SearchItemKind)ReadByte(bytes, ref cursor);
            var group = (RankGroup)ReadByte(bytes, ref cursor);
            var flags = ReadByte(bytes, ref cursor);
            var historyFlags = ReadByte(bytes, ref cursor);
            var score = ReadInt32(bytes, ref cursor);
            var priority = ReadUInt64(bytes, ref cursor);
            var highlightCount = checked((int)ReadUInt32(bytes, ref cursor));
            var id = ReadString(bytes, ref cursor, checked((int)ReadUInt32(bytes, ref cursor)));
            var name = ReadString(bytes, ref cursor, checked((int)ReadUInt32(bytes, ref cursor)));
            var target = ReadString(bytes, ref cursor, checked((int)ReadUInt32(bytes, ref cursor)));
            // Rust 按「长度→内容」交错写出，必须读完一个字段的内容再读下一个长度。
            var argumentsLength = ReadUInt32(bytes, ref cursor);
            var arguments = argumentsLength == uint.MaxValue ? null : ReadString(bytes, ref cursor, checked((int)argumentsLength));
            var aumidLength = ReadUInt32(bytes, ref cursor);
            var aumid = aumidLength == uint.MaxValue ? null : ReadString(bytes, ref cursor, checked((int)aumidLength));
            var exact = (historyFlags & 1) != 0 ? ReadInt64(bytes, ref cursor) : (long?)null;
            var prefix = (historyFlags & 2) != 0 ? ReadInt64(bytes, ref cursor) : (long?)null;
            var last = (historyFlags & 4) != 0 ? ReadInt64(bytes, ref cursor) : (long?)null;
            var highlights = new TextRange[highlightCount];
            for (var highlight = 0; highlight < highlightCount; highlight++)
                highlights[highlight] = new TextRange(ReadInt32(bytes, ref cursor), ReadInt32(bytes, ref cursor));
            items[i] = new SearchHit(
                new SearchCandidate(id, name, target, kind, arguments, priority, (flags & 1) != 0, aumid, (flags & 2) != 0),
                new MatchResult((flags & 4) != 0, score, highlights), group,
                new HistorySignals(ToTime(exact), ToTime(prefix), ToTime(last)));
        }
        return new SearchResponse(query, total, items);
    }

    private static DateTimeOffset? ToTime(long? ticks) =>
        ticks is { } value ? new DateTimeOffset(value, TimeSpan.Zero) : null;

    private static byte ReadByte(byte[] bytes, ref int cursor)
    {
        if (cursor >= bytes.Length) throw new InvalidDataException("Rust 引擎结果被截断");
        return bytes[cursor++];
    }

    private static int ReadInt32(byte[] bytes, ref int cursor)
    {
        if (cursor + 4 > bytes.Length) throw new InvalidDataException("Rust 引擎结果被截断");
        var value = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(cursor));
        cursor += 4;
        return value;
    }

    private static uint ReadUInt32(byte[] bytes, ref int cursor)
    {
        if (cursor + 4 > bytes.Length) throw new InvalidDataException("Rust 引擎结果被截断");
        var value = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(cursor));
        cursor += 4;
        return value;
    }

    private static ulong ReadUInt64(byte[] bytes, ref int cursor)
    {
        if (cursor + 8 > bytes.Length) throw new InvalidDataException("Rust 引擎结果被截断");
        var value = BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(cursor));
        cursor += 8;
        return value;
    }

    private static long ReadInt64(byte[] bytes, ref int cursor)
    {
        if (cursor + 8 > bytes.Length) throw new InvalidDataException("Rust 引擎结果被截断");
        var value = BinaryPrimitives.ReadInt64LittleEndian(bytes.AsSpan(cursor));
        cursor += 8;
        return value;
    }

    private static string ReadString(byte[] bytes, ref int cursor, int length)
    {
        var bytesLength = checked(length * 2);
        if (cursor + bytesLength > bytes.Length) throw new InvalidDataException("Rust 引擎结果被截断");
        var value = Encoding.Unicode.GetString(bytes, cursor, bytesLength);
        cursor += bytesLength;
        return value;
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            while (_activeSearches > 0) Monitor.Wait(_gate);
            if (_handle != IntPtr.Zero)
            {
                QboxEngineNative.Close(_handle);
                _handle = IntPtr.Zero;
            }
        }
    }
}

internal static unsafe partial class QboxEngineNative
{
    private const string LibraryName = "qbox_engine";
    private static readonly object ResolverGate = new();
    private static bool _resolverSet;

    [StructLayout(LayoutKind.Sequential)]
    internal struct NativeBuffer
    {
        public IntPtr Pointer;
        public nuint Length;
    }

    public static void EnsureResolver()
    {
        lock (ResolverGate)
        {
            if (_resolverSet) return;
            NativeLibrary.SetDllImportResolver(typeof(QboxEngineNative).Assembly, Resolve);
            _resolverSet = true;
        }
    }

    private static IntPtr Resolve(string name, Assembly assembly, DllImportSearchPath? searchPath)
    {
        if (!name.Equals("qbox_engine", StringComparison.OrdinalIgnoreCase)) return IntPtr.Zero;
        foreach (var candidate in Candidates())
        {
            try
            {
                if (File.Exists(candidate) && NativeLibrary.TryLoad(candidate, out var handle)) return handle;
            }
            catch (Exception error) when (error is BadImageFormatException or IOException or UnauthorizedAccessException) { }
        }
        return IntPtr.Zero;
    }

    private static IEnumerable<string> Candidates()
    {
        var configured = Environment.GetEnvironmentVariable("QBOX_ENGINE_DLL");
        if (!string.IsNullOrWhiteSpace(configured)) yield return configured;
        yield return Path.Combine(AppContext.BaseDirectory, "qbox_engine.dll");
        yield return Path.Combine(Environment.CurrentDirectory, "work", "rust-target", "release", "qbox_engine.dll");
    }

    [LibraryImport(LibraryName, EntryPoint = "qbox_engine_open")]
    internal static partial int Open(char* indexDirectory, nuint indexDirectoryLength, uint flags,
        out IntPtr handle, char* error, nuint errorCapacity);

    [LibraryImport(LibraryName, EntryPoint = "qbox_engine_prepare")]
    internal static partial int Prepare(IntPtr handle);

    [LibraryImport(LibraryName, EntryPoint = "qbox_engine_refresh")]
    internal static partial int Refresh(IntPtr handle, byte* catalogJson, nuint catalogLength);

    [LibraryImport(LibraryName, EntryPoint = "qbox_engine_search")]
    internal static partial int Search(IntPtr handle, long requestId, char* query, nuint queryLength, int limit,
        byte* history, nuint historyLength, out NativeBuffer result);

    [LibraryImport(LibraryName, EntryPoint = "qbox_engine_cancel")]
    internal static partial int Cancel(IntPtr handle, long requestId);

    [LibraryImport(LibraryName, EntryPoint = "qbox_engine_free_buffer")]
    internal static partial void FreeBuffer(NativeBuffer buffer);

    [LibraryImport(LibraryName, EntryPoint = "qbox_engine_last_error")]
    internal static partial int LastError(IntPtr handle, char* buffer, nuint capacity);

    [LibraryImport(LibraryName, EntryPoint = "qbox_engine_close")]
    internal static partial void Close(IntPtr handle);
}
