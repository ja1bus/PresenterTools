using PresenterTools.Core.Models;

namespace PresenterTools.Core.ScreenPen;

/// <summary>画面ペンの道具（docs/archive/design-1.8.0.md P2）。</summary>
public enum PenTool
{
    Pen,
    Highlighter,
    Eraser,
}

/// <summary>蛍光マーカーの固定値（P3。設定に出さない）。</summary>
public static class PenHighlighter
{
    /// <summary>線の幅（DIP）。</summary>
    public const double Width = 20;
    /// <summary>標準の不透明度。色コードのアルファはこれに掛ける。</summary>
    public const double Opacity = 0.4;
    /// <summary>色を読めないときの色（設定の既定と同じ黄色）。</summary>
    public static readonly ArgbColor DefaultColor = ArgbColor.FromRgb(0xFF, 0xD4, 0x00);

    /// <summary>
    /// 設定の色から、描く色（不透明の RGB）と不透明度を決める（P3）。読めない値は既定の黄色。
    /// </summary>
    public static (ArgbColor Color, double Opacity) Resolve(string? color)
    {
        var c = ArgbColor.TryParse(color, out var parsed) ? parsed : DefaultColor;
        return (new ArgbColor(255, c.R, c.G, c.B), Opacity * c.A / 255.0);
    }
}

/// <summary>消しゴムの固定値（P7）。</summary>
public static class PenEraser
{
    /// <summary>線の半幅に加える当たりの余裕（DIP）。</summary>
    public const double HitMargin = 4;
    /// <summary>強調の縁取りの片側の幅（DIP）。</summary>
    public const double OutlineMargin = 4;
}

/// <summary>
/// 道具の選択の規則（P2・SPEC §36.9）。WPF に依存しない。保存しない（同じ起動の間だけ保つ）。
/// </summary>
public sealed class PenToolState
{
    public PenToolState(PenWidth startWidth = PenWidth.Medium) => Reset(startWidth);

    public PenTool Tool { get; private set; }
    /// <summary>通常のペンの太さ（蛍光マーカー・消しゴムの間も保つ）。</summary>
    public PenWidth Width { get; private set; }
    /// <summary>消しゴムの前の描く道具（<see cref="PenTool.Pen"/> か <see cref="PenTool.Highlighter"/>）。</summary>
    public PenTool LastDrawingTool { get; private set; }

    public bool IsEraser => Tool == PenTool.Eraser;

    public void SelectWidth(PenWidth width)
    {
        Tool = PenTool.Pen;
        Width = width;
    }

    public void SelectHighlighter() => Tool = PenTool.Highlighter;

    public void SelectEraser()
    {
        if (Tool != PenTool.Eraser) LastDrawingTool = Tool;
        Tool = PenTool.Eraser;
    }

    /// <summary>ペンを終えた（書き込み先の切り替えでは呼ばない）。消しゴムなら前の道具に戻す。</summary>
    public void OnPenEnded()
    {
        if (Tool == PenTool.Eraser) Tool = LastDrawingTool;
    }

    /// <summary>起動時と、開始時の太さを変えて適用したとき。</summary>
    public void Reset(PenWidth startWidth)
    {
        Tool = PenTool.Pen;
        Width = startWidth;
        LastDrawingTool = PenTool.Pen;
    }

    /// <summary>メニューの✓。常に 1 つだけ真になる。</summary>
    public bool IsCheckedWidth(PenWidth width) => Tool == PenTool.Pen && Width == width;
    public bool IsCheckedHighlighter => Tool == PenTool.Highlighter;
    public bool IsCheckedEraser => Tool == PenTool.Eraser;

    /// <summary>試験用の記録 pen-tool の値。</summary>
    public string TraceName => Tool switch
    {
        PenTool.Highlighter => "highlighter",
        PenTool.Eraser => "eraser",
        _ => Width switch
        {
            PenWidth.Thin => "pen-thin",
            PenWidth.Thick => "pen-thick",
            _ => "pen-medium",
        },
    };
}
