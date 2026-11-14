using System.ComponentModel;
using System.Runtime.InteropServices;
using QBox.Core.Search;

namespace QBox.Windows.Applications;

public static class PackagedApplicationActivation
{
    public static void Validate(SearchCandidate candidate)
    {
        if (!PackagedApplicationCatalog.IsPackagedId(candidate.AppUserModelId) ||
            candidate.Id != PackagedApplicationCatalog.Identity(candidate.AppUserModelId!) ||
            candidate.Target != PackagedApplicationCatalog.ParsingName(candidate.AppUserModelId!) || candidate.Arguments != null)
            throw new IOException("应用入口身份与启动目标不一致");
    }

    public static bool CanResolve(string id) => ShellApartment.Run(() =>
    {
        var result = SHParseDisplayName(PackagedApplicationCatalog.ParsingName(id), IntPtr.Zero, out var item, 0, out _);
        var valid = result >= 0 && item != IntPtr.Zero;
        if (item != IntPtr.Zero) Marshal.FreeCoTaskMem(item);
        return valid;
    });

    public static bool Open(SearchCandidate candidate, CancellationToken cancellationToken = default)
    {
        Validate(candidate);
        return ShellApartment.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            var result = SHParseDisplayName(candidate.Target, IntPtr.Zero, out var item, 0, out _);
            if (result < 0 || item == IntPtr.Zero) throw new IOException("应用已卸载或注册入口不可用", Marshal.GetExceptionForHR(result));
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                // 以系统虚拟对象提交启动，保留包激活语义；不直接运行 WindowsApps 下的 EXE。
                var execute = new ShellExecuteInfo { Size = (uint)Marshal.SizeOf<ShellExecuteInfo>(), Mask = 0x50c,
                    ItemIdList = item, Show = 1 };
                if (!ShellExecuteExW(ref execute)) throw new Win32Exception(Marshal.GetLastPInvokeError());
                return true;
            }
            finally { Marshal.FreeCoTaskMem(item); }
        });
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ShellExecuteInfo
    {
        public uint Size, Mask;
        public nint Window;
        [MarshalAs(UnmanagedType.LPWStr)] public string? Verb;
        [MarshalAs(UnmanagedType.LPWStr)] public string? File;
        [MarshalAs(UnmanagedType.LPWStr)] public string? Parameters;
        [MarshalAs(UnmanagedType.LPWStr)] public string? Directory;
        public int Show;
        public nint Instance, ItemIdList;
        [MarshalAs(UnmanagedType.LPWStr)] public string? Class;
        public nint ClassKey;
        public uint Hotkey;
        public nint Icon, Process;
    }
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHParseDisplayName(string name, nint context, out nint item, uint attributesIn, out uint attributesOut);
    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool ShellExecuteExW(ref ShellExecuteInfo info);
}
