using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using QBox.Core.Input;

namespace QBox.Desktop;

internal sealed class HotkeyRegistration : IDisposable
{
    private readonly nint _handle;
    private readonly HwndSource _source;
    private readonly Action _toggle;
    private int _id;
    private int _nextId = 0x5140;
    private nint _keyboardHook;
    private DoubleControlDetector _doubleControl = new();
    private readonly KeyboardHookCallback _keyboardCallback;
    public string Display { get; private set; } = "未注册快捷键";
    public string Setting { get; private set; } = "";
    public int LastError { get; private set; }

    public HotkeyRegistration(Window window, Action toggle)
    {
        _handle = new WindowInteropHelper(window).Handle;
        _source = HwndSource.FromHwnd(_handle)!;
        _toggle = toggle;
        _keyboardCallback = OnKeyboard;
        _source.AddHook(Hook);
    }

    public bool Set(string setting)
    {
        if (setting == "DoubleCtrl")
        {
            if (_keyboardHook != 0) return true;
            var hook = SetWindowsHookExW(13, _keyboardCallback, GetModuleHandleW(null), 0);
            if (hook == 0) { LastError = Marshal.GetLastPInvokeError(); return false; }
            if (_id != 0) { UnregisterHotKey(_handle, _id); _id = 0; }
            _keyboardHook = hook;
            _doubleControl = new();
            Setting = setting;
            Display = "双击 Ctrl";
            LastError = 0;
            return true;
        }
        setting = setting is "Ctrl+Space" or "Ctrl+Shift+Space" ? setting : "Ctrl+Alt+Space";
        if (_id != 0 && Setting == setting) return true;
        var modifiers = setting switch { "Ctrl+Space" => 2U, "Ctrl+Shift+Space" => 6U, _ => 3U };
        var id = ++_nextId;
        if (!RegisterHotKey(_handle, id, modifiers | 0x4000, 0x20)) { LastError = Marshal.GetLastPInvokeError(); return false; }
        LastError = 0;
        // 新组合注册成功后再释放旧组合，注册失败时仍保留可用的唤起入口。
        if (_id != 0) UnregisterHotKey(_handle, _id);
        if (_keyboardHook != 0) { UnhookWindowsHookEx(_keyboardHook); _keyboardHook = 0; }
        _id = id;
        Setting = setting;
        Display = setting switch { "Ctrl+Space" => "Ctrl + 空格", "Ctrl+Shift+Space" => "Ctrl + Shift + 空格", _ => "Ctrl + Alt + 空格" };
        return true;
    }

    private nint OnKeyboard(int code, nint message, nint data)
    {
        if (code >= 0 && (message == 0x100 || message == 0x101 || message == 0x104 || message == 0x105))
        {
            var key = unchecked((uint)Marshal.ReadInt32(data));
            var time = unchecked((uint)Marshal.ReadInt32(data, 12));
            if (_doubleControl.OnKey(key, message == 0x100 || message == 0x104, time) && !_source.Dispatcher.HasShutdownStarted)
                _source.Dispatcher.BeginInvoke(_toggle);
        }
        // 不拦截或保存用户按键，界面切换排到消息队列，保持全局钩子回调短小。
        return CallNextHookEx(_keyboardHook, code, message, data);
    }

    private nint Hook(nint hwnd, int message, nint wParam, nint lParam, ref bool handled)
    {
        if (message == 0x0312 && wParam == _id) { handled = true; _toggle(); }
        return 0;
    }

    public void Dispose()
    {
        if (_id != 0) { UnregisterHotKey(_handle, _id); _id = 0; }
        if (_keyboardHook != 0) { UnhookWindowsHookEx(_keyboardHook); _keyboardHook = 0; }
        _source.RemoveHook(Hook);
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RegisterHotKey(nint handle, int id, uint modifiers, uint key);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnregisterHotKey(nint handle, int id);
    private delegate nint KeyboardHookCallback(int code, nint message, nint data);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint SetWindowsHookExW(int kind, KeyboardHookCallback callback, nint module, uint thread);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnhookWindowsHookEx(nint hook);
    [DllImport("user32.dll")]
    private static extern nint CallNextHookEx(nint hook, int code, nint message, nint data);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern nint GetModuleHandleW(string? name);
}
