using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Runtime.Versioning;
using System.Text;
using QBox.Core.Search;
using QBox.Windows.Ntfs;
using System.Globalization;

namespace QBox.Windows.Shortcuts;

public sealed record ShortcutAction(string Target, string Arguments, string WorkingDirectory, int ShowCommand, uint Flags);

[SupportedOSPlatform("windows")]
internal static class ShellLinkReader
{
    public static ShortcutAction? Read(SearchCandidate candidate)
    {
        var fields = candidate.Id.Split(':');
        if (fields.Length != 5 || fields[0] != "ntfs") return null;
        using var pinned = NativeFileIdentity.Pin(candidate.Target, new(
            ulong.Parse(fields[1], NumberStyles.HexNumber, CultureInfo.InvariantCulture),
            new(ulong.Parse(fields[2], NumberStyles.HexNumber, CultureInfo.InvariantCulture))));
        var instance = Activator.CreateInstance(Type.GetTypeFromCLSID(new("00021401-0000-0000-C000-000000000046"), true)!)!;
        try
        {
            // 只读 Load 和读取已保存字段；不调用 Resolve，避免修改快捷方式或搜索丢失目标。
            ((IPersistFile)instance).Load(candidate.Target, 0);
            var link = (IShellLinkW)instance;
            var target = new StringBuilder(32768); var arguments = new StringBuilder(32768); var working = new StringBuilder(32768);
            Marshal.ThrowExceptionForHR(link.GetPath(target, target.Capacity, IntPtr.Zero, 4));
            Marshal.ThrowExceptionForHR(link.GetArguments(arguments, arguments.Capacity));
            Marshal.ThrowExceptionForHR(link.GetWorkingDirectory(working, working.Capacity));
            Marshal.ThrowExceptionForHR(link.GetShowCmd(out var show));
            Marshal.ThrowExceptionForHR(((IShellLinkDataList)instance).GetFlags(out var flags));
            // GetPath 的接口文档仍限定 MAX_PATH；疑似截断、虚拟目标和不明确字段均不参与合并。
            if (target.Length == 0 || target.Length >= 259 || arguments.Length >= arguments.Capacity - 1 || working.Length >= working.Capacity - 1)
                return null;
            var path = Environment.ExpandEnvironmentVariables(target.ToString());
            if (!Path.IsPathFullyQualified(path) || path.StartsWith(@"\\", StringComparison.Ordinal) ||
                !(File.Exists(path) || Directory.Exists(path))) return null;
            return new(Path.GetFullPath(path), arguments.ToString(), Environment.ExpandEnvironmentVariables(working.ToString()), show, flags);
        }
        finally { Marshal.FinalReleaseComObject(instance); }
    }

    [ComImport, Guid("000214F9-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellLinkW
    {
        [PreserveSig] int GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder path, int count, IntPtr findData, uint flags);
        void GetIDList(out IntPtr pidl);
        void SetIDList(IntPtr pidl);
        void GetDescription([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder text, int count);
        void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string text);
        [PreserveSig] int GetWorkingDirectory([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder directory, int count);
        void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string directory);
        [PreserveSig] int GetArguments([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder arguments, int count);
        void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string arguments);
        void GetHotkey(out ushort hotkey);
        void SetHotkey(ushort hotkey);
        [PreserveSig] int GetShowCmd(out int command);
    }

    [ComImport, Guid("45E2B4AE-B1C3-11D0-B92F-00A0C90312E1"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellLinkDataList
    {
        void AddDataBlock(IntPtr data);
        void CopyDataBlock(uint signature, out IntPtr data);
        void RemoveDataBlock(uint signature);
        [PreserveSig] int GetFlags(out uint flags);
    }
}
