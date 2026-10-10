using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using PresenterTools.Core.Models;

namespace PresenterTools.Overlay;

/// <summary>
/// Key Display の表示（SPEC §15.5）。角丸の半透明背景に、入力文字列と Chord のキーキャップを並べる。
/// </summary>
public sealed class KeyDisplayView : Border
{
    private static readonly FontFamily Font = new("Consolas, Meiryo UI");

    private readonly TextBlock _text = new() { TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
    private readonly StackPanel _caps = new() { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _count = new() { VerticalAlignment = VerticalAlignment.Center };
    private Brush _foreground = Brushes.White;
    private Brush _capBackground = Brushes.Transparent;
    private double _fontSize = 40;
    private string[] _shownKeys = [];

    public KeyDisplayView()
    {
        IsHitTestVisible = false;
        CornerRadius = new CornerRadius(12);
        Visibility = Visibility.Collapsed;
        var row = new WrapPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        row.Children.Add(_text);
        row.Children.Add(_caps);
        row.Children.Add(_count);
        Child = row;
    }

    public void ApplySettings(KeyDisplaySettings s)
    {
        _fontSize = s.FontSize;
        var fg = ArgbColor.TryParse(s.TextColor, out var t) ? t : new ArgbColor(255, 255, 255, 255);
        var bg = (ArgbColor.TryParse(s.BackgroundColor, out var b) ? b : new ArgbColor(255, 0, 0, 0)).WithOpacity(s.BackgroundOpacity);
        _foreground = Freeze(new SolidColorBrush(Color.FromArgb(fg.A, fg.R, fg.G, fg.B)));
        _capBackground = Freeze(new SolidColorBrush(Color.FromArgb((byte)(fg.A * 0.18), fg.R, fg.G, fg.B)));
        Background = Freeze(new SolidColorBrush(Color.FromArgb(bg.A, bg.R, bg.G, bg.B)));
        Padding = new Thickness(_fontSize * 0.45, _fontSize * 0.25, _fontSize * 0.45, _fontSize * 0.25);

        foreach (var tb in new[] { _text, _count })
        {
            tb.FontFamily = Font;
            tb.FontSize = _fontSize;
            tb.Foreground = _foreground;
        }
        _count.FontSize = _fontSize * 0.7;
        _shownKeys = []; // キーキャップを作り直す
    }

    /// <summary>内容を差し替える。何も無ければ非表示。</summary>
    public void Update(string text, IReadOnlyList<string> keys, int count)
    {
        _text.Text = text;
        _text.Visibility = text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        _text.Margin = text.Length > 0 && keys.Count > 0 ? new Thickness(0, 0, _fontSize * 0.5, 0) : default;

        if (!_shownKeys.SequenceEqual(keys))
        {
            _caps.Children.Clear();
            foreach (var key in keys) _caps.Children.Add(CreateCap(key));
            _shownKeys = [.. keys];
        }
        _caps.Visibility = keys.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

        _count.Text = count > 1 ? $"×{count}" : "";
        _count.Visibility = count > 1 ? Visibility.Visible : Visibility.Collapsed;
        _count.Margin = new Thickness(_fontSize * 0.25, 0, 0, 0);

        Visibility = text.Length > 0 || keys.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    public void Hide()
    {
        _text.Text = "";
        Visibility = Visibility.Collapsed;
    }

    private Border CreateCap(string key) => new()
    {
        BorderBrush = _foreground,
        BorderThickness = new Thickness(Math.Max(1.5, _fontSize / 20)),
        CornerRadius = new CornerRadius(_fontSize / 6),
        Background = _capBackground,
        Padding = new Thickness(_fontSize * 0.3, 0, _fontSize * 0.3, _fontSize * 0.05),
        Margin = new Thickness(_fontSize * 0.08),
        Child = new TextBlock
        {
            Text = key,
            FontFamily = Font,
            FontSize = _fontSize * 0.8,
            FontWeight = FontWeights.SemiBold,
            Foreground = _foreground,
        },
    };

    private static Brush Freeze(SolidColorBrush b)
    {
        b.Freeze();
        return b;
    }
}
