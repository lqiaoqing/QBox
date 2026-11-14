using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Windows;

namespace QBox.Desktop;

public partial class App : System.Windows.Application
{
    private Mutex? _instance;
    private EventWaitHandle? _show;
    private CancellationTokenSource? _watch;
    private bool _ownsMutex;
    private DesktopStore? _store;
    private bool _workerMode;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        try
        {
            var options = DesktopOptions.Parse(e.Args);
            _workerMode = options.WorkerJob != null;
            _store = new(options.StateDirectory);
            if (options.WorkerJob != null)
            {
                var code = await Task.Run(() => IndexUpdateJob.RunWorker(options));
                Shutdown(code);
                return;
            }
            var identity = WindowsIdentity.GetCurrent().User?.Value + options.StateDirectory.ToUpperInvariant();
            var suffix = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity)))[..24];
            _show = new(false, EventResetMode.AutoReset, @"Local\QBox.Show." + suffix);
            _instance = new(true, @"Local\QBox.Desktop." + suffix, out _ownsMutex);
            if (!_ownsMutex) { _show.Set(); Shutdown(); return; }
            var settings = _store.Load();
            var window = new MainWindow(options, _store, settings);
            MainWindow = window;
            window.Show();
            _watch = new();
            var token = _watch.Token;
            _ = Task.Run(() =>
            {
                var handles = new[] { _show, token.WaitHandle };
                while (WaitHandle.WaitAny(handles) == 0)
                    Dispatcher.BeginInvoke(window.ShowSearch);
            }, token);
        }
        catch (Exception error)
        {
            _store?.Log(error);
            if (!_workerMode)
                System.Windows.MessageBox.Show("QBox 无法启动：" + error.Message, "QBox", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(2);
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _watch?.Cancel();
        _show?.Set();
        if (_ownsMutex) _instance?.ReleaseMutex();
        _instance?.Dispose();
        base.OnExit(e);
    }
}
