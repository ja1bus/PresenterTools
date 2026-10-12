using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;

namespace PresenterTools.Overlay;

/// <summary>
/// 拡大窓（SPEC §16.1）。取得した画像を窓いっぱいに最近傍補間で表示し、カーソル位置の十字マーカー・枠・倍率を重ねる。
/// 画面取得（ScreenCapture）にはこの窓自身も写る（SPEC §16.3）。取得範囲と重ならない位置に置くことで再帰拡大を防ぐ（§16.2）。
/// </summary>
public sealed class MagnifierWindow : ClickThroughWindow
{
    private const double BorderWidth = 3;
    private readonly Image _image = new() { Stretch = Stretch.Fill };
    private readonly Canvas _marker = new() { IsHitTestVisible = false };
    private readonly TextBlock _label = new()
    {
        Foreground = Brushes.White,
        Background = new SolidColorBrush(Color.FromArgb(0xB0, 0, 0, 0)),
        FontFamily = new FontFamily("Segoe UI"),
        FontWeight = FontWeights.Bold,
        FontSize = 13,
        Padding = new Thickness(6, 1, 6, 2),
        HorizontalAlignment = HorizontalAlignment.Right,
        VerticalAlignment = VerticalAlignment.Top,
    };
    private readonly Line _h1 = MarkerLine(), _h2 = MarkerLine(), _v1 = MarkerLine(), _v2 = MarkerLine();

    public MagnifierWindow()
    {
        Title = "PresenterTools Magnifier";
        RenderOptions.SetBitmapScalingMode(_image, BitmapScalingMode.NearestNeighbor);
        _marker.Children.Add(_h1);
        _marker.Children.Add(_h2);
        _marker.Children.Add(_v1);
        _marker.Children.Add(_v2);

        var grid = new Grid { ClipToBounds = true, Background = Brushes.Black };
        grid.Children.Add(_image);
        grid.Children.Add(_marker);
        grid.Children.Add(_label);
        Content = new Border
        {
            BorderBrush = new SolidColorBrush(Color.FromRgb(0x1E, 0x90, 0xFF)),
            BorderThickness = new Thickness(BorderWidth),
            Child = grid,
        };
    }

    public void SetZoomLabel(int zoomPercent) => _label.Text = $"×{zoomPercent / 100.0:0.#}";

    public void SetImage(WriteableBitmap? bitmap)
    {
        if (!ReferenceEquals(_image.Source, bitmap)) _image.Source = bitmap;
    }

    /// <summary>カーソル位置を窓内の割合（0～1）で指定して十字マーカーを描く。中心を空けた 4 本線。</summary>
    public void SetMarker(double fx, double fy)
    {
        var w = _marker.ActualWidth;
        var h = _marker.ActualHeight;
        if (w <= 0 || h <= 0) return;
        var x = fx * w;
        var y = fy * h;
        const double gap = 6, len = 18;
        Set(_h1, x - gap - len, y, x - gap, y);
        Set(_h2, x + gap, y, x + gap + len, y);
        Set(_v1, x, y - gap - len, x, y - gap);
        Set(_v2, x, y + gap, x, y + gap + len);
    }

    private static void Set(Line l, double x1, double y1, double x2, double y2)
    {
        l.X1 = x1; l.Y1 = y1; l.X2 = x2; l.Y2 = y2;
    }

    private static Line MarkerLine() => new()
    {
        Stroke = new SolidColorBrush(Color.FromArgb(0xE0, 0xFF, 0x20, 0x20)),
        StrokeThickness = 3,
        StrokeStartLineCap = PenLineCap.Round,
        StrokeEndLineCap = PenLineCap.Round,
    };
}
