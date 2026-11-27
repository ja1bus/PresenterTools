using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using PresenterTools.Core.Models;
using PresenterTools.Core.ScreenPen;

namespace PresenterTools.Overlay;

/// <summary>
/// 蛍光マーカーのカーソル（docs/archive/design-1.8.0.md P4）。32×32 DIP の枠に、縦向きの軸と幅 20 DIP の平たいペン先を描き、
/// 書き込み先の拡大率の物理ピクセルで .cur にして読む。ホットスポットはペン先の中心（16, 27.5）。
/// </summary>
public static class PenCursors
{
    private const double Frame = 32;
    private static readonly Color Edge = Color.FromRgb(0x40, 0x40, 0x40);

    /// <summary>マーカー形のカーソルを作る。失敗したら例外（呼び出し側が十字にする）。</summary>
    public static Cursor CreateHighlighter(ArgbColor tip, double scale)
    {
        var size = (int)Math.Clamp(Math.Round(Frame * (scale > 0 && double.IsFinite(scale) ? scale : 1)), 32, 128);
        var k = size / Frame;

        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.PushTransform(new ScaleTransform(k, k));
            var edge = new Pen(Frozen(new SolidColorBrush(Edge)), 1);
            edge.Freeze();
            // 軸（白地に濃い灰色の縁）
            dc.DrawRectangle(Brushes.White, edge, new Rect(10.5, 2.5, 11, 17.5));
            // 首（台形）
            var neck = new StreamGeometry();
            using (var g = neck.Open())
            {
                g.BeginFigure(new Point(10, 20), isFilled: true, isClosed: true);
                g.PolyLineTo([new Point(22, 20), new Point(24, 25), new Point(8, 25)], isStroked: true, isSmoothJoin: false);
            }
            neck.Freeze();
            dc.DrawGeometry(Frozen(new SolidColorBrush(Color.FromRgb(0x90, 0x90, 0x90))), edge, neck);
            // ペン先（幅 20 DIP ＝線幅、マーカーの色を不透明で）
            dc.DrawRectangle(Frozen(new SolidColorBrush(Color.FromRgb(tip.R, tip.G, tip.B))), edge, new Rect(6.5, 25.5, 19, 4));
            dc.Pop();
        }

        var bitmap = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        var pixels = new byte[size * size * 4];
        bitmap.CopyPixels(pixels, size * 4, 0);
        Unpremultiply(pixels);

        var hotX = Math.Clamp((int)Math.Round(16 * k), 0, size - 1);
        var hotY = Math.Clamp((int)Math.Round(27.5 * k), 0, size - 1);
        var bytes = CursorFile.Build(size, size, hotX, hotY, pixels);
#if DEBUG
        if (Environment.GetEnvironmentVariable("PRESENTERTOOLS_TEST_PEN_CURSOR_FAIL") == "1")
            throw new InvalidOperationException("Test failure: PRESENTERTOOLS_TEST_PEN_CURSOR_FAIL");
#endif
        return new Cursor(new MemoryStream(bytes), scaleWithDpi: false);
    }

    private static SolidColorBrush Frozen(SolidColorBrush b)
    {
        b.Freeze();
        return b;
    }

    /// <summary>Pbgra32 の乗算済みを、.cur の 32 bpp（乗算なしのアルファ）へ戻す。</summary>
    private static void Unpremultiply(byte[] p)
    {
        for (var i = 0; i < p.Length; i += 4)
        {
            var a = p[i + 3];
            if (a is 0 or 255) continue;
            p[i] = (byte)Math.Min(255, p[i] * 255 / a);
            p[i + 1] = (byte)Math.Min(255, p[i + 1] * 255 / a);
            p[i + 2] = (byte)Math.Min(255, p[i + 2] * 255 / a);
        }
    }
}
