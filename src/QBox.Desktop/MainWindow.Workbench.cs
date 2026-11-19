using System;
using System.Diagnostics;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;

namespace QBox.Desktop;

public partial class MainWindow
{
    private static readonly Brush Accent = new SolidColorBrush(Color.FromRgb(36, 102, 209));
    private static readonly Brush Muted = new SolidColorBrush(Color.FromRgb(116, 129, 152));
    private string _clipboardFilter = "全部";
    private string _clipboardSearch = "", _clipboardSelected = "项目会议纪要 · 明天上午 10 点";
    private readonly string[] _clipboardItems = ["项目会议纪要 · 明天上午 10 点", "图片 · 产品草图.png", "文件 · 工作计划.docx", "已置顶 · 常用地址"];

    private void OnNavigate(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string page }) SetPage(page);
    }

    private void SetPage(string page)
    {
        _previewOpen = false;
        var previousPage = _page;
        _page = page;
        if (_chrome.Kind == QBox.Core.Search.SearchChromeKind.Full) _lastPage = page;
        UpdateNavigation();
        if (page == "Search")
        {
            ToolHost.Content = null;
            if (IsVisible && _loaded && previousPage != "Search" && _chrome.Kind == QBox.Core.Search.SearchChromeKind.Full) _ = SearchAsync();
        }
        else
        {
            _inputVersion = _queries.Cancel();
            _icons.CancelPending();
            _searching = _silentRefresh = false;
            ToolHost.Content = page == "Settings" ? BuildSettings() : BuildToolPage(page);
        }
        ApplyChrome();
        if (page == "Search" && IsVisible) QueryBox.Focus();
    }

    private void UpdateNavigation()
    {
        if (!IsInitialized) return;
        var compact = Width < 700;
        foreach (var button in new[] { SettingsNav }.Concat(((StackPanel)((DockPanel)Navigation.Child).Children[1]).Children.OfType<Button>()))
        {
            var page = (string)button.Tag;
            var label = page switch { "Search" => "⌕  搜索", "Clipboard" => "▤  剪贴板", "Capture" => "▣  截图与贴图", "Desktop" => "▦  桌面整理", "Translate" => "文  翻译", _ => "⚙  设置" };
            button.Content = compact ? label[..1] : label;
            button.ToolTip = label[3..];
            button.Background = page == _page ? new SolidColorBrush(Color.FromRgb(221, 233, 253)) : Brushes.Transparent;
            button.Foreground = page == _page ? Accent : new SolidColorBrush(Color.FromRgb(70, 85, 107));
            button.FontWeight = page == _page ? FontWeights.SemiBold : FontWeights.Normal;
        }
    }

    private static TextBlock Text(string text, double size = 14, Brush? color = null) => new()
    { Text = text, FontSize = size, Foreground = color ?? Brushes.Black, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 10) };
    private static Button Button(string text, RoutedEventHandler click) { var b = new Button { Content = text, Padding = new Thickness(14, 7, 14, 7), Margin = new Thickness(0, 4, 8, 8), HorizontalAlignment = HorizontalAlignment.Left, Focusable = true }; b.Click += click; return b; }
    private static StackPanel Page(string title)
    {
        var p = new StackPanel { Margin = new Thickness(28, 20, 28, 20) };
        p.Children.Add(Text(title, 24));
        p.Children.Add(Text("界面预览 · 示例数据", 12, Accent));
        return p;
    }
    private static ScrollViewer Scroll(UIElement content) => new() { Content = content, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };

    private UIElement BuildToolPage(string page)
    {
        var p = Page(page switch { "Clipboard" => "剪贴板", "Capture" => "截图与贴图", "Desktop" => "桌面整理", _ => "翻译" });
        if (page == "Clipboard")
        {
            p.Children.Add(Text("浏览最近复制的内容，选择后查看详情。", 14, Muted));
            var open = Button("查看快捷面板", (_, _) => OpenPreview("Clipboard")); _previewReturn = open; p.Children.Add(open);
            AddClipboard(p, false);
        }
        else if (page == "Capture")
        {
            p.Children.Add(Text("选择区域后可使用标注、保存、贴图和文字识别。这里仅展示示例流程。", 14, Muted));
            var open = Button("查看截图流程", (_, _) => OpenPreview("Capture")); _previewReturn = open; p.Children.Add(open);
            p.Children.Add(Text("截图  →  选择区域  →  标注或识别文字", 18, Accent));
        }
        else if (page == "Desktop")
        {
            p.Children.Add(Text("规则与整理结果仅作用于以下样例，不会读取或移动桌面文件。", 14, Muted));
            p.Children.Add(Text("整理规则", 17));
            p.Children.Add(Text("文档 → 文档文件夹    图片 → 图片文件夹    其他 → 待分类", 14, Muted));
            var result = Text("整理前：报告.docx    草图.png    备忘.txt", 14);
            p.Children.Add(result);
            p.Children.Add(Button("预览样例整理", (_, _) => result.Text = "整理后：文档 / 报告.docx、备忘.txt    图片 / 草图.png"));
        }
        else
        {
            p.Children.Add(Text("输入示例文字，查看翻译界面。不会连接翻译服务。", 14, Muted));
            var open = Button("查看快捷面板", (_, _) => OpenPreview("Translate")); _previewReturn = open; p.Children.Add(open);
            AddTranslation(p);
        }
        return Scroll(p);
    }

    private void AddClipboard(Panel p, bool preview)
    {
        var filters = new ComboBox { Width = 150, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 0, 0, 10), ItemsSource = new[] { "全部", "文字", "图片", "文件" }, SelectedItem = _clipboardFilter };
        var search = new TextBox { Width = 260, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 0, 0, 10), ToolTip = "搜索示例内容", Text = _clipboardSearch };
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        if (!preview) grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var left = new StackPanel();
        var pinned = new ListBox { MinHeight = 40, Margin = new Thickness(0, 0, 8, 8) };
        var recent = new ListBox { MinHeight = 110, Margin = new Thickness(0, 0, 8, 8) };
        var right = new StackPanel { Margin = new Thickness(12, 0, 0, 0) };
        var detail = Text("", 14, Muted);
        var thumbnail = new Border { Width = 110, Height = 70, Background = new SolidColorBrush(Color.FromRgb(207, 224, 247)), Child = new TextBlock { Text = "▧  图片示例", HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Foreground = Accent }, HorizontalAlignment = HorizontalAlignment.Left, Visibility = Visibility.Collapsed };
        void Select(ListBox list, ListBox other)
        {
            if (list.SelectedItem is not string item) return;
            _clipboardSelected = item; other.UnselectAll(); detail.Text = "详情 · " + item;
            thumbnail.Visibility = item.StartsWith("图片") ? Visibility.Visible : Visibility.Collapsed;
        }
        pinned.SelectionChanged += (_, _) => Select(pinned, recent);
        recent.SelectionChanged += (_, _) => Select(recent, pinned);
        void Update()
        {
            var filtered = _clipboardItems.Where(x => (_clipboardFilter == "全部" || x.Contains(_clipboardFilter) || _clipboardFilter == "文字" && !x.StartsWith("图片") && !x.StartsWith("文件")) && x.Contains(_clipboardSearch, StringComparison.OrdinalIgnoreCase)).ToArray();
            pinned.ItemsSource = filtered.Where(x => x.StartsWith("已置顶")).ToArray();
            recent.ItemsSource = filtered.Where(x => !x.StartsWith("已置顶")).ToArray();
            if (filtered.Contains(_clipboardSelected)) { if (_clipboardSelected.StartsWith("已置顶")) pinned.SelectedItem = _clipboardSelected; else recent.SelectedItem = _clipboardSelected; }
            else { recent.SelectedIndex = recent.Items.Count > 0 ? 0 : -1; if (recent.SelectedIndex < 0) pinned.SelectedIndex = pinned.Items.Count > 0 ? 0 : -1; }
            if (filtered.Length == 0) detail.Text = "没有匹配的示例";
        }
        filters.SelectionChanged += (_, _) => { _clipboardFilter = (string)filters.SelectedItem; Update(); };
        search.TextChanged += (_, _) => { _clipboardSearch = search.Text; Update(); };
        p.Children.Add(search); p.Children.Add(filters);
        left.Children.Add(Text("置顶", 15)); left.Children.Add(pinned); left.Children.Add(Text("最近", 15)); left.Children.Add(recent);
        right.Children.Add(Text("内容详情", 15)); right.Children.Add(detail); right.Children.Add(thumbnail);
        right.Children.Add(Button("纯文本粘贴（演示）", (_, _) => detail.Text = "演示：此操作不会向其他窗口粘贴。"));
        grid.Children.Add(left);
        if (preview) left.Children.Add(right); else { Grid.SetColumn(right, 1); grid.Children.Add(right); }
        p.Children.Add(grid);
        if (preview) p.Children.Add(Button("展开到工作台", (_, _) => SetPage("Clipboard")));
        Update();
    }

    private void AddTranslation(Panel p)
    {
        p.Children.Add(Text("原文", 15));
        var input = new TextBox { Text = _translationText, AcceptsReturn = true, MinHeight = 70, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 12) };
        input.TextChanged += (_, _) => _translationText = input.Text;
        p.Children.Add(input); p.Children.Add(Text("译文 · 示例", 15));
        p.Children.Add(Text("你好，欢迎使用 QBox。", 18, Accent));
        p.Children.Add(Button("复制译文（演示）", (_, _) => { }));
    }

    private void OpenPreview(string page)
    {
        _previewOpen = true;
        var p = Page(page switch { "Clipboard" => "剪贴板快捷面板", "Capture" => "截图流程", _ => "翻译快捷面板" });
        p.Children.Add(Button("取消", (_, _) => ClosePreview()));
        if (page == "Clipboard") AddClipboard(p, true);
        else if (page == "Translate") { AddTranslation(p); p.Children.Add(Button("展开到工作台", (_, _) => SetPage("Translate"))); }
        else AddCapture(p);
        var surface = new Border { Background = Brushes.White, BorderBrush = new SolidColorBrush(Color.FromRgb(206, 215, 230)), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(8), Child = Scroll(p), MaxWidth = page == "Capture" ? 680 : 520, MaxHeight = 520, Margin = new Thickness(20), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        ToolHost.Content = surface;
    }

    private void AddCapture(Panel p)
    {
        p.Children.Add(Text("在示例区域拖动选择范围。", 14, Muted));
        var canvas = new Canvas { MaxWidth = 450, Height = 220, Background = new SolidColorBrush(Color.FromRgb(228, 235, 245)), HorizontalAlignment = HorizontalAlignment.Stretch, ClipToBounds = true };
        var selection = new Rectangle { Stroke = Accent, StrokeThickness = 2, Fill = new SolidColorBrush(Color.FromArgb(35, 36, 102, 209)), Visibility = Visibility.Collapsed };
        var toolbar = new Border { Background = Brushes.White, BorderBrush = Accent, BorderThickness = new Thickness(1), Padding = new Thickness(6), Child = new TextBlock { Text = "标注   复制   保存   贴图   OCR", Foreground = Accent }, Visibility = Visibility.Collapsed };
        canvas.Children.Add(selection); canvas.Children.Add(toolbar);
        Point? origin = null;
        void SetBounds(Rect area)
        {
            Canvas.SetLeft(selection, area.X); Canvas.SetTop(selection, area.Y);
            selection.Width = area.Width; selection.Height = area.Height;
            selection.Visibility = Visibility.Visible;
        }
        canvas.MouseLeave += (_, _) => { if (origin == null && toolbar.Visibility != Visibility.Visible) selection.Visibility = Visibility.Collapsed; };
        canvas.MouseLeftButtonDown += (_, e) => { origin = e.GetPosition(canvas); canvas.CaptureMouse(); SetBounds(new Rect(origin.Value, origin.Value)); toolbar.Visibility = Visibility.Collapsed; };
        canvas.MouseMove += (_, e) =>
        {
            var mouse = e.GetPosition(canvas);
            if (origin is Point start)
            {
                var end = new Point(Math.Clamp(mouse.X, 0, canvas.ActualWidth), Math.Clamp(mouse.Y, 0, canvas.ActualHeight));
                SetBounds(new Rect(start, end));
            }
            else if (toolbar.Visibility != Visibility.Visible && canvas.ActualWidth >= 24)
            {
                var half = canvas.ActualWidth / 2;
                SetBounds(new Rect(mouse.X < half ? 8 : half + 4, 10, half - 12, canvas.ActualHeight - 20));
            }
        };
        canvas.MouseLeftButtonUp += (_, e) => { if (origin == null) return; var end = e.GetPosition(canvas); origin = null; canvas.ReleaseMouseCapture(); Canvas.SetLeft(toolbar, Math.Clamp(end.X, 0, Math.Max(0, canvas.ActualWidth - 250))); Canvas.SetTop(toolbar, Math.Clamp(end.Y + 4, 0, 190)); toolbar.Visibility = Visibility.Visible; };
        p.Children.Add(canvas);
        p.Children.Add(Button("示例 OCR → 翻译", (_, _) => { _translationText = "示例识别文字"; SetPage("Translate"); }));
    }

    private void ClosePreview()
    {
        _previewOpen = false;
        ToolHost.Content = BuildToolPage(_page);
        _previewReturn?.Focus();
    }

    private UIElement BuildSettings()
    {
        var p = new StackPanel { Margin = new Thickness(28, 20, 28, 20) };
        p.Children.Add(Text("设置", 24));
        p.Children.Add(Text("搜索唤起键", 16));
        var keys = new ComboBox { ItemsSource = new[] { "DoubleCtrl", "Ctrl+Alt+Space", "Ctrl+Shift+Space", "Ctrl+Space" }, SelectedItem = _hotkey?.Setting ?? _settings.Hotkey, Width = 190, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 0, 0, 12) };
        var message = Text("双击 Ctrl 或选择已有组合键", 12, Muted);
        var restoringKey = false;
        keys.SelectionChanged += (_, _) =>
        {
            if (restoringKey || keys.SelectedItem is not string key) return;
            if (SetSearchHotkey(key)) { message.Text = "当前：" + _hotkey?.Display; return; }
            message.Text = _notice;
            restoringKey = true;
            keys.SelectedItem = _hotkey?.Setting;
            restoringKey = false;
        };
        p.Children.Add(keys); p.Children.Add(message);
        var result = new CheckBox { Content = "启用 Alt+1～9 结果快捷键", IsChecked = _settings.ResultShortcuts, Margin = new Thickness(0, 0, 0, 12) };
        result.Click += (_, _) => ToggleResultShortcuts(); p.Children.Add(result);
        var realtime = new CheckBox { Content = "启动时实时更新索引", IsChecked = _settings.AutoUpdate, Margin = new Thickness(0, 0, 0, 12) };
        realtime.Click += (_, _) => ToggleAutoUpdate(); p.Children.Add(realtime);
        p.Children.Add(Text("索引目录：" + _indexDirectory, 13, Muted));
        p.Children.Add(Button("选择索引目录…", async (_, _) => { await ChooseIndexDirectoryAsync(); SetPage("Settings"); }));
        p.Children.Add(Button("打开数据目录", (_, _) => OpenDataDirectory()));
        p.Children.Add(Button("更新索引", async (_, _) => await UpdateAsync()));
        p.Children.Add(Text("未来快捷键 · 未启用", 16));
        p.Children.Add(Text("剪贴板   Ctrl + `（~ 同键）\n截图   Ctrl + 1\n翻译   Ctrl + 2\n桌面整理   暂无默认", 14, Muted));
        var future = Button("录入快捷键 / 检查重复 / 禁用 / 恢复默认", (_, _) => { }); future.IsEnabled = false; p.Children.Add(future);
        return Scroll(p);
    }

    private bool SetSearchHotkey(string setting)
    {
        if (_hotkey!.Set(setting))
        {
            _settings = _settings with { Hotkey = setting }; SaveSettings();
            HotkeyText.Text = "Enter 打开 · " + _hotkey.Display;
            if (_tray != null) _tray.Text = "QBox · " + _hotkey.Display;
            _notice = null;
        }
        else _notice = "该快捷键已被占用，当前快捷键保持为 " + _hotkey.Display;
        RefreshStatus();
        return _notice == null;
    }
    private void ToggleAutoUpdate() { _settings = _settings with { AutoUpdate = !_settings.AutoUpdate }; SaveSettings(); if (!_settings.AutoUpdate) _update?.Cancel(); else _ = UpdateAsync(); }
    private async void ToggleResultShortcuts() { _settings = _settings with { ResultShortcuts = !_settings.ResultShortcuts }; SaveSettings(); await SearchAsync(); }
    private void OpenDataDirectory()
    {
        try { Directory.CreateDirectory(_options.StateDirectory); using var p = Process.Start(new ProcessStartInfo(_options.StateDirectory) { UseShellExecute = true }); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or Win32Exception) { _store.Log(error); _notice = "数据目录无法打开：" + error.Message; RefreshStatus(); }
    }
}
