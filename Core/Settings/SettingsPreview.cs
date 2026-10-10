using PresenterTools.Core.Models;

namespace PresenterTools.Core.Settings;

/// <summary>
/// Settings 画面で編集中の見た目を、確定前に表示へ反映するための設定を作る（SPEC §10.2 のプレビュー・D-31）。
/// </summary>
public static class SettingsPreview
{
    /// <summary>
    /// 確定済みの設定 <paramref name="applied"/> に、編集中の <paramref name="draft"/> の見た目
    /// （Cursor Highlight・Click Effect・Laser の色・大きさ等）だけを重ねた複製を返す。
    /// ON/OFF・ショートカット・マウスボタン・描画方式は確定済みのまま。
    /// 入力途中で色として解釈できない色は確定済みの色を使う。
    /// </summary>
    public static AppSettings Compose(AppSettings applied, AppSettings draft)
    {
        var result = applied.Clone();
        var d = draft.Clone();

        var ch = result.CursorHighlight;
        ch.Style = d.CursorHighlight.Style;
        ch.Color = ColorOr(d.CursorHighlight.Color, ch.Color);
        ch.Opacity = d.CursorHighlight.Opacity;
        ch.FillOpacity = d.CursorHighlight.FillOpacity;
        ch.Diameter = d.CursorHighlight.Diameter;
        ch.Thickness = d.CursorHighlight.Thickness;

        var ce = result.ClickEffect;
        ce.LeftColor = ColorOr(d.ClickEffect.LeftColor, ce.LeftColor);
        ce.RightColor = ColorOr(d.ClickEffect.RightColor, ce.RightColor);
        ce.Size = d.ClickEffect.Size;
        ce.DurationMs = d.ClickEffect.DurationMs;
        ce.ShowButtonLabel = d.ClickEffect.ShowButtonLabel;
        ce.ShowWhileHeld = d.ClickEffect.ShowWhileHeld;

        var la = result.Laser;
        la.Color = ColorOr(d.Laser.Color, la.Color);
        la.Width = d.Laser.Width;
        la.TrailSeconds = d.Laser.TrailSeconds;

        SettingsNormalizer.Normalize(result);
        return result;
    }

    private static string ColorOr(string? value, string fallback) => ArgbColor.IsValid(value) ? value! : fallback;
}
