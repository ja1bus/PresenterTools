using PresenterTools.Core.Localization;

namespace PresenterTools.Core.Models;

/// <summary>
/// Key Display の表示モード。V1.2 の改訂 3 で Special Keys Only（値 1）を廃止し、ON/OFF の 2 つにした（D-36）。
/// 値 2 は V1.1 からの番号のまま残す。
/// </summary>
public enum KeyDisplayMode
{
    Off = 0,
    /// <summary>ON。入力した文字・ショートカットをすべて表示する。</summary>
    FullText = 2,
}

/// <summary>Key Display の表示位置（表示先モニター内）。</summary>
public enum KeyDisplayPosition
{
    BottomCenter = 0,
    BottomLeft = 1,
    BottomRight = 2,
    TopCenter = 3,
    TopLeft = 4,
    TopRight = 5,
}

/// <summary>Cursor Highlight の形（SPEC §8.2）。</summary>
public enum CursorHighlightStyle
{
    /// <summary>リング（V1.0 からの既定）。</summary>
    Ring = 0,
    /// <summary>半透明の塗りつぶし円（蛍光ペン風）。</summary>
    FilledCircle = 1,
    /// <summary>
    /// V1.1 の十字線。V1.2 で廃止した（D-33）。古い設定ファイルを読めるよう値だけ残し、読み込み時に Ring へ補正する。
    /// </summary>
    Crosshair = 2,
}

/// <summary>Laser の描画方式（SPEC §8.4）。</summary>
public enum LaserTriggerMode
{
    /// <summary>トリガーを押している間だけ描く（V1.1 までの動作）。</summary>
    Hold = 0,
    /// <summary>トリガーを押すたびに描画の開始・終了を切り替える。</summary>
    Toggle = 1,
}

/// <summary>機能に割り当てるマウスのサイドボタン。</summary>
public enum MouseTriggerButton
{
    None = 0,
    XButton1 = 1,
    XButton2 = 2,
}

/// <summary>グローバルショートカットで実行する操作。</summary>
public enum HotkeyAction
{
    ToggleMaster,
    ShowControlPanel,
    ToggleCursorHighlight,
    ToggleClickEffect,
    ToggleLaser,
    LaserHold,
    ToggleMagnifier,
    ToggleKeyDisplay,
}

public static class HotkeyActionNames
{
    public static readonly IReadOnlyList<HotkeyAction> All = Enum.GetValues<HotkeyAction>();

    /// <summary>画面・メッセージ表示用の名前（表示言語に従う。設定ファイルの名前とは別）。</summary>
    public static string GetDisplayName(HotkeyAction action) => action switch
    {
        HotkeyAction.ToggleMaster => Strings.ActionToggleMaster,
        HotkeyAction.ShowControlPanel => Strings.ActionShowControlPanel,
        HotkeyAction.ToggleCursorHighlight => Strings.ActionToggleCursorHighlight,
        HotkeyAction.ToggleClickEffect => Strings.ActionToggleClickEffect,
        HotkeyAction.ToggleLaser => Strings.ActionToggleLaser,
        HotkeyAction.LaserHold => Strings.ActionLaserHold,
        HotkeyAction.ToggleMagnifier => Strings.ActionToggleMagnifier,
        HotkeyAction.ToggleKeyDisplay => Strings.ActionToggleKeyDisplay,
        _ => action.ToString(),
    };
}
