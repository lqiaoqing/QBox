using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using QBox.Core.Search;
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
        if (args.Length == 1 && args[0] == "--resources-only")
        {
            // 只加载真实 App.xaml 后退出，不运行 Dispatcher，排队的正式 Startup 不会执行。
            var resources = new App(); resources.InitializeComponent();
            foreach (var key in new[] { "AppIcon", "FolderIcon", "FileIcon" })
                if (!resources.Resources.Contains(key)) throw new InvalidDataException("应用资源缺失：" + key);
            Console.WriteLine("Application resources passed");
            return 0;
        }
        var defaults = DesktopOptions.Parse([]);
#if QBOX_GLOBAL_NAME_SHARING
        if (!defaults.StateDirectory.EndsWith("QBox.GlobalTrial", StringComparison.OrdinalIgnoreCase) ||
            new DesktopSettings().AutoUpdate || new DesktopSettings().Hotkey != "Ctrl+Alt+Space" ||
            typeof(MainWindow).Assembly.GetName().Name != "QBox.GlobalTrial")
            throw new InvalidDataException("全局试用的进程或默认状态未隔离");
#else
        if (!defaults.StateDirectory.EndsWith("QBox", StringComparison.OrdinalIgnoreCase) ||
            !new DesktopSettings().AutoUpdate || new DesktopSettings().Hotkey != "DoubleCtrl")
            throw new InvalidDataException("标准版默认设置发生变化");
#endif
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
#if QBOX_GLOBAL_NAME_SHARING
        if (!window.Title.Contains("全局共享试用", StringComparison.Ordinal)) throw new InvalidDataException("试用窗口未标识构建类型");
        var resolved = (string)store.GetType().GetMethod("ResolveIndexDirectory")!.Invoke(store,
            [new DesktopOptions(root, null, false, null), new DesktopSettings()])!;
        if (resolved != Path.Combine(root, "indexes")) throw new InvalidDataException("试用缓存未默认隔离");
#endif
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
        Check(query.Text.Length == 0 && (string)Get("_resultQuery") == "", "hide did not clear query");
        Check(((SearchChromeState)Get("_chrome")).Kind == SearchChromeKind.Compact, "hide did not return to compact chrome");
        var hiddenVersion = Version(); Invoke("SearchAsync").GetAwaiter().GetResult();
        Check(Version() == hiddenVersion, "hidden refresh started search");
        window.Show(); Wait(() => !(bool)Get("_searching") && query.Text.Length == 0 && Rows() == 0);
        Check(((SearchChromeState)Get("_chrome")).Kind == SearchChromeKind.Compact, "reshow did not stay compact");
        window.Hide();
        hiddenVersion = Version();
        var changed = new LiveVolumeIndex(index).Apply([new(new(7, 110), [new(Id(20), File(20, "qboxupdatedprobe74.txt"))])]);
        Set("_liveIndexes", new[] { new LiveIndexFile("probe", changed, 1, 0, default) });
        query.Text = "qboxupdatedprobe74"; Check(Version() == hiddenVersion, "hidden changed input started search");
        timer.Restart(); window.Show(); Wait(() => !(bool)Get("_searching") && Has("qboxupdatedprobe74.txt"));
        var changedShow = timer.Elapsed.TotalMilliseconds;
        Check(!Has("qboxfreshprobe42.txt"), "stale result survived re-show");
        var times = new List<double>();
        for (var i = 0; i < 10; i++)
        {
            window.Hide();
            Check(query.Text.Length == 0, "hide did not clear query");
            // 再次显示前写入查询，只测量带查询唤起；用户隐藏后再唤起时输入框应为空。
            query.Text = "qboxupdatedprobe74";
            timer.Restart(); window.Show(); Wait(() => !(bool)Get("_searching") && Rows() == 1);
            times.Add(timer.Elapsed.TotalMilliseconds);
        }
        var wide = VolumeIndex.Create(geometry, new(7, 100), new[] { File(5, ".", true) }
            .Concat(Enumerable.Range(20, 235).Select(n => File((ulong)n, $"qboxworkbench{n:D4}.txt"))));
        void Capture(string name)
        {
            window.UpdateLayout();
            var content = (FrameworkElement)window.Content;
            var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap((int)Math.Ceiling(content.ActualWidth),
                (int)Math.Ceiling(content.ActualHeight), 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
            bitmap.Render(content);
            var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
            encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
            using var output = System.IO.File.Create(Path.Combine(root, "..", name + ".png"));
            encoder.Save(output);
        }
        Set("_indexes", new[] { wide });
        Set("_liveIndexes", new[] { new LiveIndexFile("workbench-probe", new(wide), 0, 0, default) });
        query.Text = "qboxworkbench";
        Wait(() => !(bool)Get("_searching") && Rows() == SearchChromeLayout.QuickResultCap);
        var results = (ListBox)window.FindName("Results");
        results.SelectedIndex = 5;
        string SelectedId() => ((SearchHit)results.SelectedItem.GetType().GetProperty("Hit")!.GetValue(results.SelectedItem)!).Candidate.Id;
        var selectedId = SelectedId();
        Capture("quick-search");
        typeof(MainWindow).GetMethod("ExpandToFull", Private)!.Invoke(window, null);
        Wait(() => !(bool)Get("_searching") && Rows() == 235);
        Check(query.Text == "qboxworkbench" && SelectedId() == selectedId, "workbench expansion or auto-fill lost selection");
        Capture("workbench-search");
        var setPage = typeof(MainWindow).GetMethod("SetPage", Private)!;
        setPage.Invoke(window, ["Clipboard"]);
        version = Version();
        Invoke("SearchAsync").GetAwaiter().GetResult();
        Check(Version() == version, "non-search page started a foreground query");
        typeof(MainWindow).GetMethod("OpenPreview", Private)!.Invoke(window, ["Clipboard"]);
        Capture("clipboard-preview");
        setPage.Invoke(window, ["Settings"]);
        Capture("workbench-settings");
        setPage.Invoke(window, ["Search"]);
        Wait(() => !(bool)Get("_searching") && Rows() == 235);
        Check(SelectedId() == selectedId, "return to search lost selection");
        window.ShowSearch();
        Wait(() => !(bool)Get("_searching") && Rows() == SearchChromeLayout.QuickResultCap);
        Check(SelectedId() == selectedId, "return to quick search lost visible selection");
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
            hideInvalidatedResults = true, hideClearedQuery = true, reshowStartedEmpty = true,
            reshowUsedLatestIndex = true, staleIconQueueClearedAndReusable = true,
            workbenchExpansionAndFillPreservedSelection = true, nonSearchPageSkippedForegroundQuery = true,
            quickSearchRestoredResultCap = true,
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
