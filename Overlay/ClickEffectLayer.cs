using System.Globalization;
using System.Windows;
using System.Windows.Media;
using PresenterTools.Core.Localization;
using PresenterTools.Core.Models;

namespace PresenterTools.Overlay;

/// <summary>
/// クリックの波紋（SPEC §8.3）。左は円の実線、右はひし形の破線が拡大しながら消える。
/// 設定により、クリック位置の近くに「左クリック」「右クリック」の札も出す。
/// 座標はこのレイヤーのローカル DIP。
/// </summary>
public sealed class ClickEffectLayer : FrameworkElement
{
    private const int MaxRipples = 32;
    /// <summary>札は波紋より短いと読めないため、最低この秒数は表示する。</summary>
    private const double MinLabelSeconds = 0.8;
    private const double LabelOffset = 14;

    private readonly List<Ripple> _ripples = [];
    private Color _left = Colors.DodgerBlue;
    private Color _right = Colors.DarkOrange;
    private double _size = 80;
    private double _duration = 0.4;
    private bool _showLabel;
    private double _now;
    private FormattedText? _leftText, _rightText;
    private Brush? _leftBadge, _rightBadge;
    private double _textDpi;

    private readonly record struct Ripple(Point Center, double Start, bool IsRight);

    public ClickEffectLayer()
    {
        IsHitTestVisible = false;
    }

    public bool IsAnimating => _ripples.Count > 0;

    private double LabelSeconds => Math.Max(_duration, MinLabelSeconds);
    private double Lifetime => _showLabel ? LabelSeconds : _duration;

    public void ApplySettings(ClickEffectSettings s)
    {
        _left = ToColor(s.LeftColor, Colors.DodgerBlue);
        _right = ToColor(s.RightColor, Colors.DarkOrange);
        _size = s.Size;
        _duration = s.DurationMs / 1000.0;
        _showLabel = s.ShowButtonLabel;
        _leftText = _rightText = null; // 色・大きさが変わるので作り直す
    }

    public void Add(Point center, bool isRight, double now)
    {
        if (_ripples.Count >= MaxRipples) _ripples.RemoveAt(0);
        _ripples.Add(new Ripple(center, now, isRight));
        _now = now;
        InvalidateVisual();
    }

    public void Clear()
    {
        if (_ripples.Count == 0) return;
        _ripples.Clear();
        InvalidateVisual();
    }

    /// <summary>フレームごとに呼ぶ。終わった波紋を捨てて再描画する。</summary>
    public void Update(double now)
    {
        if (_ripples.Count == 0) return;
        var lifetime = Lifetime;
        _ripples.RemoveAll(r => now - r.Start >= lifetime);
        _now = now;
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext dc)
    {
        foreach (var r in _ripples)
        {
            var age = _now - r.Start;
            if (age < _duration) DrawRipple(dc, r, age / _duration);
            if (_showLabel && age < LabelSeconds) DrawLabel(dc, r, age / LabelSeconds);
        }
    }

    private void DrawRipple(DrawingContext dc, Ripple r, double progress)
    {
        var p = Math.Clamp(progress, 0, 1);
        var eased = 1 - Math.Pow(1 - p, 3); // ease-out
        var radius = _size / 2 * (0.2 + 0.8 * eased);
        var alpha = 1 - p;
        var baseColor = r.IsRight ? _right : _left;
        var color = Color.FromArgb((byte)(baseColor.A * alpha), baseColor.R, baseColor.G, baseColor.B);
        var pen = new Pen(new SolidColorBrush(color), Math.Max(1.5, 5 * (1 - p * 0.6)));

        if (!r.IsRight)
        {
            dc.DrawEllipse(null, pen, r.Center, radius, radius);
            return;
        }
        pen.DashStyle = new DashStyle([2, 1.2], 0);
        var g = new StreamGeometry();
        using (var ctx = g.Open())
        {
            ctx.BeginFigure(new Point(r.Center.X, r.Center.Y - radius), false, true);
            ctx.LineTo(new Point(r.Center.X + radius, r.Center.Y), true, false);
            ctx.LineTo(new Point(r.Center.X, r.Center.Y + radius), true, false);
            ctx.LineTo(new Point(r.Center.X - radius, r.Center.Y), true, false);
        }
        dc.DrawGeometry(null, pen, g);
    }

    /// <summary>
    /// ボタン名の札。カーソルの矢印（右下へ伸びる）に隠れないよう右上に出し、画面の端では反対側へ回す。
    /// 最後の 30% で薄くして消す。
    /// </summary>
    private void DrawLabel(DrawingContext dc, Ripple r, double progress)
    {
        EnsureLabels();
        var text = r.IsRight ? _rightText! : _leftText!;
        var badge = r.IsRight ? _rightBadge! : _leftBadge!;
        var pad = new Size(text.Height * 0.45, text.Height * 0.15);
        var w = text.Width + pad.Width * 2;
        var h = text.Height + pad.Height * 2;

        var x = r.Center.X + LabelOffset;
        var y = r.Center.Y - LabelOffset - h;
        if (x + w > ActualWidth) x = r.Center.X - LabelOffset - w;
        if (y < 0) y = r.Center.Y + LabelOffset * 2; // 上に余白が無ければ矢印の下へ
        x = Math.Clamp(x, 0, Math.Max(0, ActualWidth - w));
        y = Math.Clamp(y, 0, Math.Max(0, ActualHeight - h));

        var fade = progress < 0.7 ? 1 : 1 - (progress - 0.7) / 0.3;
        dc.PushOpacity(Math.Clamp(fade, 0, 1));
        dc.DrawRoundedRectangle(badge, null, new Rect(x, y, w, h), h / 2, h / 2);
        dc.DrawText(text, new Point(x + pad.Width, y + pad.Height));
        dc.Pop();
    }

    private void EnsureLabels()
    {
        var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        if (_leftText is not null && _textDpi == dpi) return;
        _textDpi = dpi;
        var fontSize = Math.Clamp(_size * 0.2, 13, 28);
        (_leftText, _leftBadge) = MakeLabel(Strings.ClickLabelLeft, _left, fontSize, dpi);
        (_rightText, _rightBadge) = MakeLabel(Strings.ClickLabelRight, _right, fontSize, dpi);
    }

    private static (FormattedText Text, Brush Badge) MakeLabel(string label, Color color, double fontSize, double dpi)
    {
        var typeface = new Typeface(new FontFamily("Segoe UI, Meiryo UI"), FontStyles.Normal, FontWeights.Bold, FontStretches.Normal);
        // 明るい色の札では白文字が読めないため、輝度で文字色を選ぶ。
        var luminance = (0.299 * color.R + 0.587 * color.G + 0.114 * color.B) / 255;
        var foreground = luminance > 0.65 ? Brushes.Black : Brushes.White;
        var text = new FormattedText(label, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, typeface, fontSize, foreground, dpi);
        var badge = new SolidColorBrush(Color.FromArgb(230, color.R, color.G, color.B));
        badge.Freeze();
        return (text, badge);
    }

    private static Color ToColor(string text, Color fallback) =>
        ArgbColor.TryParse(text, out var c) ? Color.FromArgb(c.A, c.R, c.G, c.B) : fallback;
}
