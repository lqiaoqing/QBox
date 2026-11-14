using System.Buffers.Binary;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32.SafeHandles;

namespace QBox.Windows.Ntfs;

public readonly record struct NtfsPathIdentity(ulong VolumeSerial, FileReference File);

[SupportedOSPlatform("windows")]
public static class NativeFileIdentity
{
    public static NtfsPathIdentity Read(string path)
    {
        using var handle = Open(path, share: 7);
        return Read(handle);
    }

    public static SafeFileHandle Pin(string path, NtfsPathIdentity expected)
    {
        var handle = Open(path, share: 3);
        try
        {
            if (Read(handle) != expected) throw new IOException("路径已对应其他文件，请同步索引后重试");
            return handle;
        }
        catch { handle.Dispose(); throw; }
    }

    private static SafeFileHandle Open(string path, uint share)
    {
        var absolute = Path.GetFullPath(path);
        var extended = absolute.StartsWith(@"\\?\", StringComparison.Ordinal) ? absolute : @"\\?\" + absolute;
        // 查询链接本身的身份；否则目录联接会被解析成目标，无法与索引的链接记录核对。
        var handle = CreateFileW(extended, 0, share, 0, 3, 0x02200000, 0);
        if (!handle.IsInvalid) return handle;
        var error = Marshal.GetLastPInvokeError();
        handle.Dispose();
        throw new Win32Exception(error, $"无法核对目标身份（Windows 错误 {error}）");
    }

    private static NtfsPathIdentity Read(SafeFileHandle handle)
    {
        var bytes = new byte[24];
        if (!GetFileInformationByHandleEx(handle, 18, bytes, (uint)bytes.Length))
            throw new Win32Exception(Marshal.GetLastPInvokeError(), "读取目标文件身份失败");
        return new(BinaryPrimitives.ReadUInt64LittleEndian(bytes), new(
            BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(8)), BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(16))));
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(string path, uint access, uint share, nint security, uint creation, uint flags, nint template);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandleEx(SafeFileHandle handle, int informationClass, [Out] byte[] information, uint size);
}
