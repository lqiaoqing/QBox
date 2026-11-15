using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace QBox.Desktop;

internal sealed class ShellIconLoader : IDisposable
{
    private readonly BlockingCollection<(string Path, Action<ImageSource?> Done, long Version)> _pending = new();
    private readonly object _gate = new();
    private long _version;
    private readonly Dictionary<string, ImageSource?> _cache = new(StringComparer.Ordinal);
    public ShellIconLoader()
    {
        var thread = new Thread(Work) { IsBackground = true, Name = "QBox icons" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
    }
    public void Load(string path, Action<ImageSource?> done)
    {
        lock (_gate)
            if (!_pending.IsAddingCompleted) _pending.Add((path, done, _version));
    }
    public void CancelPending()
    {
        lock (_gate)
        {
            // 先清过期队列再接收新批次；已进入 Shell 的调用仅丢弃回调，不强行中断 COM。
            Interlocked.Increment(ref _version);
            while (_pending.TryTake(out _)) { }
        }
    }
    public void ReleaseIdleCache()
    {
        lock (_gate)
        {
            Interlocked.Increment(ref _version);
            while (_pending.TryTake(out _)) { }
            _cache.Clear();
        }
    }
    private void Work()
    {
        var initialized = CoInitializeEx(IntPtr.Zero, 2) >= 0;
        try
        {
            foreach (var request in _pending.GetConsumingEnumerable())
            {
                if (request.Version != Volatile.Read(ref _version)) continue;
                ImageSource? image;
                var cached = false;
                lock (_gate) cached = _cache.TryGetValue(request.Path, out image);
                if (cached)
                {
                    if (request.Version == Volatile.Read(ref _version)) request.Done(image);
                    continue;
                }
                image = Read(request.Path);
                lock (_gate)
                {
                    if (_cache.Count >= 512) _cache.Clear();
                    _cache[request.Path] = image;
                }
                if (request.Version == Volatile.Read(ref _version)) request.Done(image);
            }
        }
        finally { if (initialized) CoUninitialize(); }
    }
    private static ImageSource? Read(string path)
    {
        var info = new ShellFileInfo();
        nint item = 0;
        try
        {
            nint result;
            if (path.StartsWith(@"shell:AppsFolder\", StringComparison.Ordinal))
            {
                // 注册应用属于 Shell 虚拟目录，图标通过其 PIDL 获取，不能当作普通文件路径。
                if (SHParseDisplayName(path, 0, out item, 0, out _) < 0 || item == 0) return null;
                result = SHGetFileInfoItem(item, 0, ref info, (uint)Marshal.SizeOf<ShellFileInfo>(), 0x109);
            }
            else result = SHGetFileInfoW(path, 0, ref info, (uint)Marshal.SizeOf<ShellFileInfo>(), 0x101);
            if (result == 0 || info.Icon == 0) return null;
            var source = Imaging.CreateBitmapSourceFromHIcon(info.Icon, Int32Rect.Empty, BitmapSizeOptions.FromWidthAndHeight(32, 32));
            source.Freeze();
            return source;
        }
        catch (Exception error) when (error is ArgumentException or System.Runtime.InteropServices.COMException) { return null; }
        finally
        {
            if (info.Icon != 0) DestroyIcon(info.Icon);
            if (item != 0) Marshal.FreeCoTaskMem(item);
        }
    }
    public void Dispose()
    {
        lock (_gate) { CancelPending(); _pending.CompleteAdding(); }
    }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ShellFileInfo
    {
        public nint Icon;
        public int IconIndex;
        public uint Attributes;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string DisplayName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)] public string TypeName;
    }
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern nint SHGetFileInfoW(string path, uint attributes, ref ShellFileInfo info, uint size, uint flags);
    [DllImport("shell32.dll", EntryPoint = "SHGetFileInfoW", CharSet = CharSet.Unicode)]
    private static extern nint SHGetFileInfoItem(nint item, uint attributes, ref ShellFileInfo info, uint size, uint flags);
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHParseDisplayName(string name, nint context, out nint item, uint attributesIn, out uint attributesOut);
    [DllImport("ole32.dll")] private static extern int CoInitializeEx(nint reserved, uint flags);
    [DllImport("ole32.dll")] private static extern void CoUninitialize();
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(nint icon);
}
