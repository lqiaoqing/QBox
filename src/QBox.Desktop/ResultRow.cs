using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using QBox.Core.Matching;
using TextRange = QBox.Core.Matching.TextRange;
using QBox.Core.Search;

namespace QBox.Desktop;

internal sealed class ResultRow(SearchHit hit, int index, ImageSource placeholder, bool resultShortcuts = false) : INotifyPropertyChanged
{
    private ImageSource _icon = placeholder;
    private bool _iconRequested;
    public SearchHit Hit { get; } = hit;
    public string Name => Hit.Candidate.Name;
    public string Target => Hit.Candidate.AppUserModelId == null ? Hit.Candidate.Target : "已安装应用";
    public IReadOnlyList<TextRange> Highlights => Hit.Match.Highlights;
    public string KeyHint => resultShortcuts && index < 9 ? $"Alt+{index + 1}" : "";
    public string HistoryHint => Hit.History.ExactQuery != null || Hit.History.PrefixQuery != null ? "常用" : "";
    public ImageSource Icon { get => _icon; set { _icon = value; PropertyChanged?.Invoke(this, new(nameof(Icon))); } }
    public bool TryMarkIconRequested() { if (_iconRequested) return false; _iconRequested = true; return true; }
    public event PropertyChangedEventHandler? PropertyChanged;
}

public sealed class HighlightedText : TextBlock
{
    public static readonly DependencyProperty SourceTextProperty = DependencyProperty.Register(nameof(SourceText), typeof(string),
        typeof(HighlightedText), new PropertyMetadata("", Changed));
    public static readonly DependencyProperty HighlightsProperty = DependencyProperty.Register(nameof(Highlights), typeof(IReadOnlyList<TextRange>),
        typeof(HighlightedText), new PropertyMetadata(null, Changed));
    public string SourceText { get => (string)GetValue(SourceTextProperty); set => SetValue(SourceTextProperty, value); }
    public IReadOnlyList<TextRange>? Highlights { get => (IReadOnlyList<TextRange>?)GetValue(HighlightsProperty); set => SetValue(HighlightsProperty, value); }
    private static readonly Brush Accent = FrozenBrush();
    private static Brush FrozenBrush() { var brush = new SolidColorBrush(Color.FromRgb(36, 102, 209)); brush.Freeze(); return brush; }
    private static void Changed(DependencyObject item, DependencyPropertyChangedEventArgs args) => ((HighlightedText)item).Rebuild();
    private void Rebuild()
    {
        Inlines.Clear();
        var text = SourceText ?? "";
        var offset = 0;
        foreach (var range in Highlights ?? [])
        {
            if (range.Start < offset || range.Start > text.Length || range.Length < 0 || range.Length > text.Length - range.Start) continue;
            Inlines.Add(new Run(text[offset..range.Start]));
            Inlines.Add(new Run(text.Substring(range.Start, range.Length)) { Foreground = Accent, FontWeight = FontWeights.SemiBold });
            offset = range.Start + range.Length;
        }
        Inlines.Add(new Run(text[offset..]));
    }
}
