using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;
using QBox.Core.History;
using QBox.Core.Search;
using QBox.Windows;
using QBox.Windows.Indexing;
using QBox.Windows.Shortcuts;
using QBox.Windows.Applications;

namespace QBox.Desktop;

public partial class MainWindow : Window
{
    private readonly DesktopOptions _options;
    private readonly DesktopStore _store;
    private DesktopSettings _settings;
    private string _indexDirectory;
    private readonly SelectionHistory _history;
    private readonly string? _historyWarning;
    private readonly LatestQueryGate _queries = new(), _loads = new();
    private readonly LatestQueryGate _catalogs = new();
    private readonly CancellationTokenSource _lifetime = new();
    private readonly ShellIconLoader _icons = new();
    private VolumeIndex[] _indexes = [];
    private LiveIndexFile[] _liveIndexes = [];
    private ShortcutCatalog _shortcuts = ShortcutCatalog.Empty;
    private PackagedApplicationCatalog _applications = PackagedApplicationCatalog.Empty;
    private ResultRow[] _rows = [];
    private HotkeyRegistration? _hotkey;
    private System.Windows.Forms.NotifyIcon? _tray;
    private IndexUpdateJob? _update;
    private string? _updateText, _notice, _workerWarning;
    private string _snapshotText = "", _resultQuery = "";
    private long _inputVersion, _resultVersion;
    private int _limit = 9, _total;
    private bool _loading, _searching, _loaded, _quitting, _opening;
    private bool _monitorRunning;
    private DateTimeOffset _catalogRefreshAt = DateTimeOffset.UtcNow;

    internal MainWindow(DesktopOptions options, DesktopStore store, DesktopSettings settings)
    {
        _options = options;
        _store = store;
        _settings = settings;
        _indexDirectory = store.ResolveIndexDirectory(options, settings);
        try { _history = new(new FileHistoryPersistence(Path.Combine(options.StateDirectory, "history.json"))); }
        catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            store.Log(error);
            _historyWarning = "历史无法读取，原文件已保留；当前选择不会保存。";
            _history = new(new UnavailableHistory(_historyWarning));
        }
        InitializeComponent();
        Icon = (ImageSource)FindResource("AppIcon");
        _notice = store.LoadError;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_loaded) return;
        _loaded = true;
        _hotkey = new(this, ToggleSearch);
        if (!_hotkey.Set(_settings.Hotkey) && !_hotkey.Set("Ctrl+Alt+Space") && !_hotkey.Set("Ctrl+Shift+Space"))
        {
            _notice = "快捷键暂不可用，可从托盘或双击程序打开。";
            _store.Log(new Win32Exception(_hotkey.LastError, "全局快捷键注册失败"));
        }
        HotkeyText.Text = _hotkey.Display;
        SetupTray();
        if (_options.StartHidden) Hide(); else ShowSearch();
        await ReloadAsync();
        _ = MonitorIndexesAsync();
        if (_settings.AutoUpdate) await UpdateAsync();
    }

    private void SetupTray()
    {
        using var bitmap = new System.Drawing.Bitmap(32, 32);
        using (var graphics = System.Drawing.Graphics.FromImage(bitmap))
        {
            graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            graphics.Clear(System.Drawing.Color.FromArgb(36, 102, 209));
            using var pen = new System.Drawing.Pen(System.Drawing.Color.White, 3);
            graphics.DrawEllipse(pen, 7, 6, 16, 16);
            graphics.DrawLine(pen, 18, 19, 25, 26);
        }
        var handle = bitmap.GetHicon();
        try
        {
            using var temporary = System.Drawing.Icon.FromHandle(handle);
            _tray = new() { Icon = (System.Drawing.Icon)temporary.Clone(), Text = "QBox · " + _hotkey!.Display, Visible = true };
        }
        finally { DestroyIcon(handle); }
        _tray.DoubleClick += (_, _) => Dispatcher.BeginInvoke(ShowSearch);
        var menu = new System.Windows.Forms.ContextMenuStrip();
        menu.Items.Add("打开搜索", null, (_, _) => Dispatcher.BeginInvoke(ShowSearch));
        menu.Items.Add("更新索引", null, (_, _) => Dispatcher.BeginInvoke(async () => await UpdateAsync()));
        menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());
        menu.Items.Add("退出 QBox", null, (_, _) => Dispatcher.BeginInvoke(Quit));
        _tray.ContextMenuStrip = menu;
    }

    public void ShowSearch()
    {
        if (_quitting) return;
        var work = SystemParameters.WorkArea;
        MaxHeight = Math.Max(300, work.Height - 40);
        Height = Math.Min(720, MaxHeight);
        Left = work.Left + Math.Max(0, (work.Width - Width) / 2);
        Top = work.Top + Math.Max(20, Math.Min(work.Height * 0.16, work.Height - Height - 20));
        Show();
        WindowState = WindowState.Normal;
        Activate();
        Topmost = true;
        Topmost = false;
        QueryBox.Focus();
        QueryBox.SelectAll();
    }
    private void ToggleSearch() { if (IsVisible && IsActive) Hide(); else ShowSearch(); }
    private void OnHide(object sender, RoutedEventArgs e) => Hide();
    private void OnFocus(object sender, RoutedEventArgs e) => QueryBox.Focus();
    private void OnDrag(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left) return;
        for (var item = e.OriginalSource as DependencyObject; item != null;
             item = item is Visual ? VisualTreeHelper.GetParent(item) : LogicalTreeHelper.GetParent(item))
            if (item is System.Windows.Controls.Primitives.ButtonBase) return;
        DragMove();
    }

    private async Task ReloadAsync(bool background = false)
    {
        using var request = _loads.Begin();
        _loading = !background || _indexes.Length == 0;
        if (_loading) Results.IsEnabled = false;
        RefreshStatus();
        if (_indexes.Length == 0) SetEmpty("正在载入索引", "载入完成后，输入即可搜索。", false);
        try
        {
            var directory = _indexDirectory;
            var snapshot = await Task.Run(() =>
            {
                var paths = Directory.Exists(directory) ? Directory.GetFiles(directory, "*.qbi").Order(StringComparer.Ordinal).ToArray() : [];
                var live = paths.Select(path => LiveIndexFile.Load(path, request.Token)).ToArray();
                var result = live.Select(source => source.View.Base).ToArray();
                if (result.Select(i => i.Geometry.Serial).Distinct().Count() != result.Length)
                    throw new InvalidDataException("同一个磁盘存在重复索引，请选择正确的索引目录");
                foreach (var index in result) index.PrepareSearch(request.Token);
                return (Indexes: result, Live: live, Shortcuts: ShortcutCatalog.Load(live.Select(source => source.View).ToArray(), request.Token), Applications: PackagedApplicationCatalog.Load(request.Token));
            }, request.Token);
            if (!_loads.IsCurrent(request)) return;
            var indexes = snapshot.Indexes;
            MigrateHistory(_liveIndexes.Length == 0 ? snapshot.Live.Select(source => new LiveVolumeIndex(source.View.Base)).ToArray() :
                _liveIndexes.Select(source => source.View).ToArray(), snapshot.Live.Select(source => source.View).ToArray());
            _indexes = indexes;
            _liveIndexes = snapshot.Live;
            _shortcuts = snapshot.Shortcuts;
            _catalogRefreshAt = DateTimeOffset.UtcNow;
            if (snapshot.Applications.Warning == null || snapshot.Applications.Count != 0) _applications = snapshot.Applications;
            _notice = snapshot.Applications.Warning ?? snapshot.Live.Select(source => source.Warning).FirstOrDefault(w => w != null);
            if (indexes.Length != 0)
            {
                var volumes = string.Join(" · ", indexes.Select(i => i.Geometry.Root[..2]));
                var oldest = indexes.Min(i => i.CreatedAt).ToLocalTime();
                _snapshotText = $"{volumes} · {indexes.Sum(i => (long)i.FileCount):N0} 项 · 快照 {oldest:MM-dd HH:mm}";
            }
            else _snapshotText = _applications.Count == 0 ? "尚未建立索引" : "已载入系统应用 · 尚未建立文件索引";
        }
        catch (OperationCanceledException) { return; }
        catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException)
        {
            if (!_loads.IsCurrent(request)) return;
            _store.Log(error);
            _notice = "索引载入失败：" + error.Message;
        }
        finally
        {
            if (_loads.IsCurrent(request))
            {
                _loading = false;
                if (_indexes.Length == 0 && _applications.Count == 0) SetEmpty("还没有可用索引", _notice ?? "点击建立索引，收录磁盘上的文件、文件夹和应用。", true);
                else await SearchAsync();
                RefreshStatus();
            }
        }
    }

    private async void OnQueryChanged(object sender, TextChangedEventArgs e)
    {
        if (!_loaded || _quitting) return;
        _limit = 9;
        await SearchAsync();
    }

    private async Task SearchAsync()
    {
        using var request = _queries.Begin();
        _inputVersion = request.Version;
        Results.IsEnabled = false;
        if (_loading || _indexes.Length == 0 && _applications.Count == 0) { _searching = false; return; }
        var query = QueryBox.Text;
        if (query.Length == 0)
        {
            _rows = [];
            _total = 0;
            Results.ItemsSource = _rows;
            _searching = false;
            MoreButton.Visibility = Visibility.Collapsed;
            SetEmpty("输入名称，开始搜索", "支持名称片段、首字母和大小写混合输入。", false);
            RefreshStatus();
            return;
        }
        _searching = true;
        RefreshStatus();
        var indexes = _liveIndexes;
        var shortcuts = _shortcuts;
        var applications = _applications;
        var limit = _limit;
        var history = _history.ForQuery(query);
        try
        {
            var response = await Task.Run(() =>
            {
                var collector = new SearchCollector(query, limit, request.Token);
                applications.SearchInto(collector, history, request.Token);
                shortcuts.SearchInto(collector, history, request.Token);
                var replaced = shortcuts.ReplacedIds(query);
                var representedExecutables = shortcuts.MatchedExecutableTargets(query);
                foreach (var index in indexes) index.View.SearchInto(collector, history, request.Token, replaced, representedExecutables);
                return collector.Finish();
            }, request.Token);
            // UI 线程上的版本复核同时约束展示和打开，取消来不及的旧查询也不能抢回结果。
            if (!_queries.IsCurrent(request) || _loading) return;
            _resultVersion = request.Version;
            _resultQuery = query;
            _total = response.TotalMatches;
            _rows = response.Items.Select((hit, i) => new ResultRow(hit, i,
                (ImageSource)FindResource(hit.Candidate.Kind == SearchItemKind.Directory ? "FolderIcon" :
                    hit.Candidate.Kind is SearchItemKind.Shortcut or SearchItemKind.Application ? "AppIcon" : "FileIcon"))).ToArray();
            Results.ItemsSource = _rows;
            Results.IsEnabled = true;
            Results.SelectedIndex = _rows.Length == 0 ? -1 : 0;
            EmptyPanel.Visibility = _rows.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
            if (_rows.Length == 0) SetEmpty("没有找到匹配项", "可以尝试名称片段，或在菜单中更新索引。", false);
            MoreButton.Visibility = _total > _rows.Length ? Visibility.Visible : Visibility.Collapsed;
            foreach (var row in _rows)
            {
                _icons.Load(row.Hit.Candidate.Target, image =>
                {
                    if (image == null || _lifetime.IsCancellationRequested) return;
                    Dispatcher.BeginInvoke(() => { if (_rows.Contains(row)) row.Icon = image; });
                });
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception error)
        {
            if (!_queries.IsCurrent(request)) return;
            _store.Log(error);
            _notice = "搜索未完成：" + error.Message;
            SetEmpty("搜索未完成", "请重新输入或重新载入索引。", false);
        }
        finally
        {
            if (_queries.IsCurrent(request)) { _searching = false; RefreshStatus(); }
        }
    }

    private bool CurrentResults => !_loading && !_searching && _resultVersion == _inputVersion && _resultQuery == QueryBox.Text;
    private async Task OpenAsync(int index)
    {
        if (_opening || !CurrentResults || (uint)index >= _rows.Length) return;
        var row = _rows[index];
        var query = _resultQuery;
        _opening = true;
        Hide();
        try
        {
            var result = await new SearchActions(_history).ActivateAsync(row.Hit.Candidate, query,
                (candidate, token) => Task.Run(() => ShellTargetLauncher.OpenIndexed(candidate, token), token), _lifetime.Token);
            if (!result.Opened)
            {
                ShowSearch();
                _notice = "打开失败：" + result.Error;
            }
            else
            {
                _notice = result.HistoryError == null ? null : "已打开，历史未保存：" + result.HistoryError;
                await SearchAsync();
                if (result.HistoryError != null) ShowSearch();
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception error) { _store.Log(error); ShowSearch(); _notice = "打开失败：" + error.Message; }
        finally { _opening = false; RefreshStatus(); }
    }

    private async void OnKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == Key.ImeProcessed) return;
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        var modifiers = Keyboard.Modifiers;
        if (key == Key.Escape) { Hide(); e.Handled = true; }
        else if (modifiers == ModifierKeys.Control && key == Key.R) { e.Handled = true; await ReloadAsync(); }
        else if ((modifiers == ModifierKeys.Alt || modifiers == ModifierKeys.Control) && key >= Key.D1 && key <= Key.D9)
        { e.Handled = true; await OpenAsync((int)key - (int)Key.D1); }
        else if (modifiers == (ModifierKeys.Control | ModifierKeys.Shift) && key == Key.C && CurrentResults && Results.SelectedItem is ResultRow selected)
        {
            try { System.Windows.Clipboard.SetText(selected.Hit.Candidate.Target); _notice = selected.Hit.Candidate.AppUserModelId == null ? "已复制路径" : "已复制应用入口"; RefreshStatus(); }
            catch (System.Runtime.InteropServices.COMException error) { _store.Log(error); _notice = "剪贴板暂时不可用"; RefreshStatus(); }
            e.Handled = true;
        }
        else if (key == Key.Enter && modifiers == ModifierKeys.None) { e.Handled = true; await OpenAsync(Results.SelectedIndex); }
        else if (key == Key.Down && modifiers == ModifierKeys.Alt) { e.Handled = true; await MoreAsync(); }
        else if ((key == Key.Down || key == Key.Up) && modifiers == ModifierKeys.None && CurrentResults && _rows.Length != 0)
        {
            Results.SelectedIndex = (Results.SelectedIndex + (key == Key.Down ? 1 : -1) + _rows.Length) % _rows.Length;
            Results.ScrollIntoView(Results.SelectedItem);
            e.Handled = true;
        }
    }
    private async void OnResultDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is DependencyObject source && ItemsControl.ContainerFromElement(Results, source) is ListBoxItem)
            await OpenAsync(Results.SelectedIndex);
    }
    private async void OnMore(object sender, RoutedEventArgs e) => await MoreAsync();
    private async Task MoreAsync()
    {
        if (_total <= _rows.Length || _searching) return;
        _limit = (int)Math.Min(_total, (long)_limit + 200);
        await SearchAsync();
        QueryBox.Focus();
    }

    private void SetEmpty(string title, string detail, bool canBuild)
    {
        _rows = [];
        Results.ItemsSource = _rows;
        MoreButton.Visibility = Visibility.Collapsed;
        EmptyTitle.Text = title;
        EmptyDetail.Text = detail;
        EmptyPanel.Visibility = Visibility.Visible;
        BuildButton.Visibility = canBuild ? Visibility.Visible : Visibility.Collapsed;
    }
    private void RefreshStatus()
    {
        if (!IsInitialized) return;
        StatusText.Text = _updateText ?? (_loading ? "正在载入索引…" : _notice ?? _historyWarning ?? _workerWarning ??
            (_searching ? "正在搜索…" : QueryBox.Text.Length == 0 ? _snapshotText : $"{_total:N0} 项 · 显示 {_rows.Length} · {_snapshotText}"));
        StatusText.ToolTip = StatusText.Text;
    }

    private void OnMenu(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu { PlacementTarget = MenuButton };
        void Item(string text, Action action, bool enabled = true)
        {
            var item = new MenuItem { Header = text, IsEnabled = enabled };
            item.Click += (_, _) => action();
            menu.Items.Add(item);
        }
        Item("更新索引", async () => await UpdateAsync(), _update == null);
        Item("取消索引更新", () => _update?.Cancel(), _update != null);
        Item(_settings.AutoUpdate ? "关闭启动时实时更新" : "开启启动时实时更新", () =>
        {
            _settings = _settings with { AutoUpdate = !_settings.AutoUpdate };
            SaveSettings();
            if (!_settings.AutoUpdate) _update?.Cancel();
            else _ = UpdateAsync();
        });
        Item("重新载入索引    Ctrl+R", async () => await ReloadAsync());
        Item("选择索引目录…", async () => await ChooseIndexDirectoryAsync(), _update == null);
        var keys = new MenuItem { Header = "唤起快捷键" };
        foreach (var setting in new[] { "DoubleCtrl", "Ctrl+Alt+Space", "Ctrl+Shift+Space", "Ctrl+Space" })
        {
            var key = new MenuItem { Header = setting == "DoubleCtrl" ? "双击 Ctrl" : setting.Replace("Space", "空格"), IsCheckable = true, IsChecked = _hotkey?.Setting == setting };
            key.Click += (_, _) =>
            {
                if (_hotkey!.Set(setting))
                {
                    _settings = _settings with { Hotkey = setting };
                    SaveSettings();
                    HotkeyText.Text = _hotkey.Display;
                    if (_tray != null) _tray.Text = "QBox · " + _hotkey.Display;
                    _notice = null;
                }
                else _notice = "该快捷键已被占用，当前快捷键保持为 " + _hotkey.Display;
                RefreshStatus();
            };
            keys.Items.Add(key);
        }
        menu.Items.Add(keys);
        menu.Items.Add(new Separator());
        Item("打开数据目录", () =>
        {
            try { Directory.CreateDirectory(_options.StateDirectory); using var p = Process.Start(new ProcessStartInfo(_options.StateDirectory) { UseShellExecute = true }); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or Win32Exception)
            { _store.Log(error); _notice = "数据目录无法打开：" + error.Message; RefreshStatus(); }
        });
        Item("退出 QBox", Quit);
        menu.IsOpen = true;
    }

    private async Task ChooseIndexDirectoryAsync()
    {
        var picker = new OpenFolderDialog { Title = "选择 QBox 索引目录", Multiselect = false };
        if (Directory.Exists(_indexDirectory)) picker.InitialDirectory = _indexDirectory;
        if (picker.ShowDialog(this) != true) return;
        _indexDirectory = picker.FolderName;
        _settings = _settings with { IndexDirectory = _indexDirectory };
        SaveSettings();
        await ReloadAsync();
    }
    private void SaveSettings()
    {
        try { _store.Save(_settings); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { _store.Log(error); _notice = "设置未保存：" + error.Message; }
    }
    private async void OnUpdate(object sender, RoutedEventArgs e) => await UpdateAsync();
    private async Task UpdateAsync()
    {
        if (_update != null || _quitting) return;
        try { _update = new(_options, _indexDirectory); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        { _store.Log(error); _notice = "无法准备索引更新：" + error.Message; RefreshStatus(); return; }
        _updateText = "正在等待 Windows 管理员授权…";
        BuildButton.IsEnabled = false;
        RefreshStatus();
        try
        {
            await Task.Run(_update.Start, _lifetime.Token);
            var exitedPolls = 0;
            while (!_lifetime.IsCancellationRequested)
            {
                var status = _update.Read();
                if (_update.HasExited && (status == null || status.State is "Running" or "Watching"))
                {
                    if (++exitedPolls >= 3) throw new IOException("索引更新进程提前退出，请查看数据目录中的日志");
                }
                if (status != null)
                {
                    var current = status.Drives.LastOrDefault();
                    _updateText = status.State == "Watching" ? null : current == null ? "正在准备更新…" : $"{current.Drive} {current.Stage} · {current.Files:N0} 项";
                    if (status.State == "Watching")
                    {
                        var failures = status.Drives.Where(d => d.Error != null).ToArray();
                        _workerWarning = failures.Length == 0 ? null : string.Join("；", failures.Select(d => d.Drive + " " + d.Error));
                    }
                    RefreshStatus();
                    if (status.State is not ("Running" or "Watching"))
                    {
                        _notice = status.State switch
                        {
                            "Completed" => null,
                            "Cancelled" => "索引更新已取消，保留上次完成的快照。",
                            _ => status.Error ?? string.Join("；", status.Drives.Where(d => d.Error != null).Select(d => d.Drive + " " + d.Error))
                        };
                        var notice = _notice;
                        await ReloadAsync();
                        _notice = notice;
                        break;
                    }
                }
                await Task.Delay(400, _lifetime.Token);
            }
        }
        catch (OperationCanceledException) { }
        catch (Win32Exception error) when (error.NativeErrorCode == 1223) { _notice = "已取消管理员授权。"; }
        catch (Exception error) { _store.Log(error); _notice = "索引更新未完成：" + error.Message; }
        finally { _update?.Dispose(); _update = null; _updateText = null; BuildButton.IsEnabled = true; RefreshStatus(); }
    }

    private async Task MonitorIndexesAsync()
    {
        if (_monitorRunning) return;
        _monitorRunning = true;
        try
        {
            while (!_lifetime.IsCancellationRequested)
            {
                await Task.Delay(500, _lifetime.Token);
                if (_loading) continue;
                var before = _liveIndexes; var directory = _indexDirectory;
                try
                {
                    var after = await Task.Run(() =>
                    {
                        var count = Directory.Exists(directory) ? Directory.GetFiles(directory, "*.qbi").Length : 0;
                        if (count != before.Length) throw new IndexBaseChangedException();
                        return before.Select(source => source.Refresh(_lifetime.Token)).ToArray();
                    }, _lifetime.Token);
                    if (!ReferenceEquals(before, _liveIndexes) || directory != _indexDirectory) continue;
                    if (after.Where((source, i) => !ReferenceEquals(source, before[i])).Any())
                    {
                        if (after.Where((source, i) => source.View.HasShortcutChangesFrom(before[i].View)).Any())
                            _catalogRefreshAt = DateTimeOffset.MinValue;
                        MigrateHistory(before.Select(source => source.View).ToArray(), after.Select(source => source.View).ToArray());
                        _liveIndexes = after;
                        // 即使查询文本未变，也取消旧视图的查询，防止更新后又展示已删除项。
                        await SearchAsync();
                    }
                    if (DateTimeOffset.UtcNow - _catalogRefreshAt > TimeSpan.FromSeconds(30)) await RefreshCatalogsAsync();
                }
                catch (IndexBaseChangedException) { await ReloadAsync(true); }
                catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or System.Text.Json.JsonException)
                {
                    _notice = "实时索引暂不可读，保留上次结果：" + error.Message; RefreshStatus();
                    await Task.Delay(2500, _lifetime.Token);
                }
            }
        }
        catch (OperationCanceledException) { }
        finally { _monitorRunning = false; }
    }

    private void MigrateHistory(LiveVolumeIndex[] before, LiveVolumeIndex[] after)
    {
        try { _history.ReassignTargets(IndexHistoryMigration.Plan(_history.Snapshot, before, after)); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        { _store.Log(error); _notice = "索引已更新，历史迁移未保存：" + error.Message; }
    }

    private async Task RefreshCatalogsAsync()
    {
        using var request = _catalogs.Begin();
        var indexes = _indexes; var live = _liveIndexes.Select(source => source.View).ToArray();
        try
        {
            var result = await Task.Run(() => (Shortcuts: ShortcutCatalog.Load(live, request.Token),
                Applications: PackagedApplicationCatalog.Load(request.Token)), request.Token);
            if (!_catalogs.IsCurrent(request) || !ReferenceEquals(indexes, _indexes)) return;
            _shortcuts = result.Shortcuts;
            if (result.Applications.Warning == null) _applications = result.Applications;
            else _notice = result.Applications.Warning;
            await SearchAsync();
        }
        catch (OperationCanceledException) { }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or Win32Exception)
        { _store.Log(error); _notice = "应用列表稍后重试：" + error.Message; }
        finally { _catalogRefreshAt = DateTimeOffset.UtcNow; }
    }

    private void OnClosing(object? sender, CancelEventArgs e) { if (!_quitting) { e.Cancel = true; Hide(); } }
    private void Quit()
    {
        if (_quitting) return;
        _quitting = true;
        try { _update?.Cancel(); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { _store.Log(error); }
        _lifetime.Cancel();
        _queries.Dispose(); _loads.Dispose(); _catalogs.Dispose(); _icons.Dispose(); _hotkey?.Dispose();
        if (_tray != null) { var icon = _tray.Icon; _tray.Visible = false; _tray.Dispose(); icon?.Dispose(); }
        Close();
        System.Windows.Application.Current.Shutdown();
    }

    private sealed class UnavailableHistory(string message) : IHistoryPersistence
    {
        public HistorySnapshot Read() => HistorySnapshot.Empty;
        public void Write(HistorySnapshot snapshot) => throw new IOException(message);
    }
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool DestroyIcon(nint icon);
}
