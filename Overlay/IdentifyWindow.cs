using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using PresenterTools.Core.Geometry;

namespace PresenterTools.Overlay;

/// <summary>
/// 設定画面の［識別］で各モニターの中央に番号を 3 秒出す窓（V1.7.0。docs/archive/design-1.7.0.md D3）。
/// Key Display に近い見た目（暗い角丸の板に白い大きな文字）。Master に関係なく出し、クリックは透過する。
/// </summary>
public sealed class IdentifyWindow : ClickThroughWindow
{
    public IdentifyWindow(RectInt bounds, string text)
    {
        Title = "PresenterTools Identify";
        Content = new Grid
        {
            IsHitTestVisible = false,
            Children =
            {
                new Border
                {
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                    Background = new SolidColorBrush(Color.FromArgb(0xD0, 0x20, 0x20, 0x20)),
                    CornerRadius = new CornerRadius(16),
                    Padding = new Thickness(40, 16, 40, 16),
                    Child = new TextBlock
                    {
                        Text = text,
                        Foreground = Brushes.White,
                        FontSize = 96,
                        FontWeight = FontWeights.Bold,
                        FontFamily = new FontFamily("Segoe UI, Meiryo UI"),
                    },
                },
            },
        };
        SetBounds(bounds);
    }

    /// <summary>各画面に出して 3 秒後に閉じる。</summary>
    public static void ShowAll(IEnumerable<(RectInt Bounds, string Text)> displays)
    {
        var windows = displays.Select(d => new IdentifyWindow(d.Bounds, d.Text)).ToList();
        foreach (var w in windows)
        {
            w.Show();
            w.BringToTop();
        }
        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            foreach (var w in windows) w.Close();
        };
        timer.Start();
    }
}
