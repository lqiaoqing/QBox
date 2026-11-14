using System.Runtime.InteropServices;

namespace QBox.Windows.Applications;

internal static class ShellApartment
{
    public static T Run<T>(Func<T> work)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("应用入口仅支持 Windows");
        var completed = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            var initialized = false;
            try
            {
                var result = CoInitializeEx(IntPtr.Zero, 2);
                if (result < 0) Marshal.ThrowExceptionForHR(result);
                initialized = true;
                completed.SetResult(work());
            }
            catch (Exception error) { completed.SetException(error); }
            finally { if (initialized) CoUninitialize(); }
        }) { IsBackground = true, Name = "QBox Shell applications" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return completed.Task.GetAwaiter().GetResult();
    }
    [DllImport("ole32.dll")] private static extern int CoInitializeEx(nint reserved, uint flags);
    [DllImport("ole32.dll")] private static extern void CoUninitialize();
}
