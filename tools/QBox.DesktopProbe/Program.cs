using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using QBox.Desktop;
using QBox.Windows.Applications;
using QBox.Windows.Indexing;
using QBox.Windows.Ntfs;

internal static class Program
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    [STAThread]
    public static int Main(string[] args)
    {
        var app = new ProbeApp();
        var icon = new System.Windows.Media.DrawingImage(new System.Windows.Media.GeometryDrawing(
            System.Windows.Media.Brushes.Blue, null, new System.Windows.Media.RectangleGeometry(new Rect(0, 0, 32, 32))));
        icon.Freeze();
        foreach (var key in new[] { "AppIcon", "FolderIcon", "FileIcon" }) app.Resources[key] = icon;
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
        if (args.Length > 1) throw new ArgumentException("用法：QBox.DesktopProbe [独立状态目录]");
        var root = Path.GetFullPath(args.Length == 1 ? args[0] : "work/desktop-lifecycle/state");
        Directory.CreateDirectory(root);
        var store = Activator.CreateInstance(typeof(MainWindow).Assembly.GetType("QBox.Desktop.DesktopStore")!,
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, [root], null)!;
        var window = (MainWindow)Activator.CreateInstance(typeof(MainWindow), Private, null,
            [new DesktopOptions(root, root, true, null), store, new DesktopSettings(AutoUpdate: false)], null)!;
        void Set(string key, object value) => typeof(MainWindow).GetField(key, Private)!.SetValue(window, value);
        object Get(string key) => typeof(MainWindow).GetField(key, Private)!.GetValue(window)!;
        Task Invoke(string name) => (Task)typeof(MainWindow).GetMethod(name, Private)!.Invoke(window, name == "ReloadAsync" ? [false] : null)!;
        long Version() => (long)Get("_inputVersion");
        int Rows() => ((Array)Get("_rows")).Length;
        bool Has(string name) => ((System.Collections.IEnumerable)Get("_rows")).Cast<object>().Any(row =>
            ((QBox.Core.Search.SearchHit)row.GetType().GetProperty("Hit")!.GetValue(row)!).Candidate.Name == name);
        void Check(bool condition, string label) { if (!condition) throw new InvalidDataException(label); }
        FileReference Id(ulong number) => new(number | (7UL << 48));
        var geometry = new NtfsGeometry(@"X:\", 9, 512, 4096, 1024, 65536, 0, 3, 1);
        IndexedFile File(ulong number, string name, bool directory = false) => new(Id(number), directory, 100, 1, [new(Id(5), name, 1, 0)]);
        var index = VolumeIndex.Create(geometry, new(7, 100), [File(5, ".", true), File(20, "qboxfreshprobe42.txt")]);
        Set("_indexes", new[] { index }); Set("_liveIndexes", new[] { new LiveIndexFile("probe", new(index), 0, 0, default) });
        Set("_applications", PackagedApplicationCatalog.Load());
        Set("_loaded", true); // 跳过托盘、全局热键及后台启动；只驱动真实窗口的前台行为。
        var query = (TextBox)window.FindName("QueryBox");
        query.Text = "qboxfreshprobe42";
        Check(Version() == 0, "hidden input started query");
        window.ShowActivated = false; window.Left = -20000; window.Top = -20000;
        var timer = Stopwatch.StartNew(); window.Show();
        Wait(() => !(bool)Get("_searching") && Has("qboxfreshprobe42.txt"));
        var firstShow = timer.Elapsed.TotalMilliseconds;
        var rows = Get("_rows"); var version = Version();
        var refresh = Invoke("RefreshCatalogsAsync"); Wait(() => refresh.IsCompleted); refresh.GetAwaiter().GetResult();
        Check(Version() == version && ReferenceEquals(rows, Get("_rows")), "unchanged catalog rebuilt rows");
        window.Hide(); Check(Rows() == 0 && Version() > version, "hide did not invalidate rows");
        var hiddenVersion = Version(); Invoke("SearchAsync").GetAwaiter().GetResult();
        Check(Version() == hiddenVersion, "hidden refresh started search");
        var changed = new LiveVolumeIndex(index).Apply([new(new(7, 110), [new(Id(20), File(20, "qboxupdatedprobe74.txt"))])]);
        Set("_liveIndexes", new[] { new LiveIndexFile("probe", changed, 1, 0, default) });
        query.Text = "qboxupdatedprobe74"; Check(Version() == hiddenVersion, "hidden changed input started search");
        timer.Restart(); window.Show(); Wait(() => !(bool)Get("_searching") && Has("qboxupdatedprobe74.txt"));
        var changedShow = timer.Elapsed.TotalMilliseconds;
        Check(!Has("qboxfreshprobe42.txt"), "stale result survived re-show");
        var times = new List<double>();
        for (var i = 0; i < 10; i++)
        {
            window.Hide(); timer.Restart(); window.Show(); Wait(() => !(bool)Get("_searching") && Rows() == 1);
            times.Add(timer.Elapsed.TotalMilliseconds);
        }
        var icons = Get("_icons"); var iconType = icons.GetType(); var completed = 0;
        Action<System.Windows.Media.ImageSource?> callback = _ => Interlocked.Increment(ref completed);
        for (var i = 0; i < 1000; i++) iconType.GetMethod("Load")!.Invoke(icons, ["X:\\qbox-icon-probe-" + i + ".txt", callback]);
        iconType.GetMethod("CancelPending")!.Invoke(icons, null);
        var pending = iconType.GetField("_pending", Private)!.GetValue(icons)!;
        Check((int)pending.GetType().GetProperty("Count")!.GetValue(pending)! == 0, "old icons remained queued");
        var latestIcon = 0;
        Action<System.Windows.Media.ImageSource?> latestCallback = _ => Interlocked.Exchange(ref latestIcon, 1);
        iconType.GetMethod("Load")!.Invoke(icons, ["X:\\qbox-current-icon.txt", latestCallback]);
        Wait(() => Volatile.Read(ref latestIcon) == 1);
        window.Hide();
        System.IO.File.WriteAllText(Path.Combine(root, "../report.json"), JsonSerializer.Serialize(new
        {
            unchangedCatalogPreservedRows = true, hiddenInputAndRefreshSkipped = true,
            hideInvalidatedResults = true, reshowUsedLatestIndex = true, staleIconQueueClearedAndReusable = true,
            firstShowMs = firstShow, changedShowMs = changedShow, repeatedShowQueryMs = times,
            scope = "Offscreen STA window with placeholder icons, isolated state, no global hotkeys/worker; not physical input or end-to-end activation timing"
        }, new JsonSerializerOptions { WriteIndented = true }));
        typeof(MainWindow).GetMethod("Quit", Private)!.Invoke(window, null);
        Console.WriteLine("Desktop lifecycle probe passed");
        return 0;
    }
    private static void Wait(Func<bool> done)
    {
        var timer = Stopwatch.StartNew();
        while (!done())
        {
            if (timer.Elapsed > TimeSpan.FromSeconds(20)) throw new TimeoutException("Desktop probe did not finish");
            var frame = new DispatcherFrame();
            Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() => frame.Continue = false));
            Dispatcher.PushFrame(frame); Thread.Sleep(1);
        }
    }
}

// 应用构造会排队 Startup；探针只加载资源，禁止进入正式状态、托盘或后台入口。
internal sealed class ProbeApp : System.Windows.Application
{
    protected override void OnStartup(StartupEventArgs e) { }
}
