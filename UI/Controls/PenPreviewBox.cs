using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using PresenterTools.Core.Localization;
using PresenterTools.Core.Models;

namespace PresenterTools.UI.Controls;

/// <summary>
/// 設定画面のペン タブの見本（V1.7.0。docs/archive/design-1.7.0.md D9）。編集中の色で細・中・太の 3 本を実際の幅（DIP）で描く。
/// 描いた線と次の画には［適用］まで反映しない（ここだけに反映する）。
/// </summary>
public sealed class PenPreviewBox : Border
{
    private readonly Grid _rows = new();

    public PenPreviewBox()
    {
        Background = Brushes.White;
        BorderBrush = new SolidColorBrush(Color.FromRgb(0xB0, 0xB0, 0xB0));
        BorderThickness = new Thickness(1);
        ClipToBounds = true;
        Padding = new Thickness(8, 4, 8, 4);
        for (var i = 0; i < 3; i++) _rows.RowDefinitions.Add(new RowDefinition());
        // 名前の列は文字の幅に合わせる（英語の「Medium」が線に隠れないように。初期試験で確認）。
        _rows.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto, MinWidth = 40 });
        _rows.ColumnDefinitions.Add(new ColumnDefinition());
        Child = _rows;
    }

    public void ApplySettings(PenSettings pen)
    {
        _rows.Children.Clear();
        var c = ArgbColor.TryParse(pen.Color, out var color) ? color : ArgbColor.FromRgb(0xFF, 0x20, 0x20);
        var brush = new SolidColorBrush(Color.FromArgb(c.A, c.R, c.G, c.B));
        brush.Freeze();
        var items = new[] { (Strings.PenThin, pen.ThinWidth), (Strings.PenMedium, pen.MediumWidth), (Strings.PenThick, pen.ThickWidth) };
        for (var i = 0; i < items.Length; i++)
        {
            var (label, width) = items[i];
            var text = new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center, Foreground = Brushes.DimGray, Margin = new Thickness(0, 0, 12, 0) };
            Grid.SetRow(text, i);
            _rows.Children.Add(text);
            var w = double.IsFinite(width) ? Math.Clamp(width, 1, 40) : 1;
            var line = new Line
            {
                X1 = w / 2, X2 = 220, Y1 = 0, Y2 = 0,
                Stroke = brush, StrokeThickness = w,
                StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round,
                VerticalAlignment = VerticalAlignment.Center,
                Height = w,
                RenderTransform = new TranslateTransform(0, w / 2),
            };
            Grid.SetRow(line, i);
            Grid.SetColumn(line, 1);
            _rows.Children.Add(line);
        }
    }
}
