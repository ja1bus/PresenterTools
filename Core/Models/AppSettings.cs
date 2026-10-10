using System.Text.Json;
using System.Text.Json.Serialization;

namespace PresenterTools.Core.Models;

/// <summary>
/// 保存するユーザー設定の全体。既定値はプロパティ初期化子で持つ。
/// 範囲・形式の検証と補正は <see cref="Settings.SettingsNormalizer"/> が行う。
/// Key Display の現在モードと Magnifier の ON 状態は安全のため保存しない（SPEC §17）。
/// </summary>
public sealed class AppSettings
{
    public const int CurrentSchemaVersion = 1;

    public int SchemaVersion { get; set; } = CurrentSchemaVersion;
    public GeneralSettings General { get; set; } = new();
    public HotkeySettings Hotkeys { get; set; } = new();
    public CursorHighlightSettings CursorHighlight { get; set; } = new();
    public ClickEffectSettings ClickEffect { get; set; } = new();
    public LaserSettings Laser { get; set; } = new();
    public MagnifierSettings Magnifier { get; set; } = new();
    public KeyDisplaySettings KeyDisplay { get; set; } = new();
    public ControlPanelSettings ControlPanel { get; set; } = new();

    /// <summary>JSON の往復で深い複製を作る（Settings 画面の編集用）。</summary>
    public AppSettings Clone()
    {
        var json = JsonSerializer.Serialize(this, SettingsJson.Options);
        return JsonSerializer.Deserialize<AppSettings>(json, SettingsJson.Options) ?? new AppSettings();
    }
}

public sealed class GeneralSettings
{
    public bool MasterEnabled { get; set; } = true;
    public bool AlwaysOnTop { get; set; } = true;
    public bool HideOwnShortcutsInKeyDisplay { get; set; } = true;
    /// <summary>操作パネルの各項目に割り当て中のショートカットを表示する（SPEC §10.1）。</summary>
    public bool ShowShortcutsInPanel { get; set; } = true;
    /// <summary>
    /// 表示言語（V1.3.1。SPEC §34）。"" は Windows の表示言語に合わせる、"en" / "ja" は固定（<see cref="Localization.UiLanguage"/>）。
    /// 変更は再起動後に反映する。General の［このタブを既定値に戻す］では戻さない（D-39）。
    /// </summary>
    public string Language { get; set; } = "";
}

/// <summary>ショートカットは "Ctrl+Shift+F9" 形式の文字列。空文字は未割当。</summary>
public sealed class HotkeySettings
{
    public string ToggleMaster { get; set; } = "Ctrl+Shift+F9";
    public string ShowControlPanel { get; set; } = "Ctrl+Shift+F10";
    // 以下の既定は未割当（SPEC §11.1・D-32）。利用者が押しやすい空きキーを選んで割り当てる。
    public string ToggleCursorHighlight { get; set; } = "";
    public string ToggleClickEffect { get; set; } = "";
    public string ToggleLaser { get; set; } = "";
    /// <summary>Laser 描画のトリガー（名前は V1.1 までの Laser Hold のまま。設定ファイルの互換のため）。</summary>
    public string LaserHold { get; set; } = "";
    public string ToggleMagnifier { get; set; } = "";
    public string ToggleKeyDisplay { get; set; } = "";
    // V1.2 の改訂 3 で Key Display 即時 OFF（keyDisplayOff）を廃止した。古い設定ファイルの値は読み込み時に無視する。

    public string Get(HotkeyAction action) => action switch
    {
        HotkeyAction.ToggleMaster => ToggleMaster,
        HotkeyAction.ShowControlPanel => ShowControlPanel,
        HotkeyAction.ToggleCursorHighlight => ToggleCursorHighlight,
        HotkeyAction.ToggleClickEffect => ToggleClickEffect,
        HotkeyAction.ToggleLaser => ToggleLaser,
        HotkeyAction.LaserHold => LaserHold,
        HotkeyAction.ToggleMagnifier => ToggleMagnifier,
        HotkeyAction.ToggleKeyDisplay => ToggleKeyDisplay,
        _ => throw new ArgumentOutOfRangeException(nameof(action)),
    };

    public void Set(HotkeyAction action, string value)
    {
        switch (action)
        {
            case HotkeyAction.ToggleMaster: ToggleMaster = value; break;
            case HotkeyAction.ShowControlPanel: ShowControlPanel = value; break;
            case HotkeyAction.ToggleCursorHighlight: ToggleCursorHighlight = value; break;
            case HotkeyAction.ToggleClickEffect: ToggleClickEffect = value; break;
            case HotkeyAction.ToggleLaser: ToggleLaser = value; break;
            case HotkeyAction.LaserHold: LaserHold = value; break;
            case HotkeyAction.ToggleMagnifier: ToggleMagnifier = value; break;
            case HotkeyAction.ToggleKeyDisplay: ToggleKeyDisplay = value; break;
            default: throw new ArgumentOutOfRangeException(nameof(action));
        }
    }
}

public sealed class CursorHighlightSettings
{
    public bool Enabled { get; set; } = true;
    public CursorHighlightStyle Style { get; set; } = CursorHighlightStyle.Ring;
    public string Color { get; set; } = "#FFD400";
    /// <summary>リングの不透明度。0.1～1.0。</summary>
    public double Opacity { get; set; } = 0.8;
    /// <summary>塗りつぶし円の不透明度。下の文字が透けるよう低めにする。0.05～0.8。</summary>
    public double FillOpacity { get; set; } = 0.35;
    /// <summary>リング・塗りつぶし円の直径（DIP）。</summary>
    public double Diameter { get; set; } = 64;
    /// <summary>リングの線幅（DIP）。</summary>
    public double Thickness { get; set; } = 5;
}

public sealed class ClickEffectSettings
{
    public bool Enabled { get; set; } = true;
    public string LeftColor { get; set; } = "#2F80FF";
    public string RightColor { get; set; } = "#FF8C00";
    /// <summary>波紋の最大サイズ（DIP）。</summary>
    public double Size { get; set; } = 80;
    public int DurationMs { get; set; } = 400;
    /// <summary>クリック位置の近くに「左クリック」「右クリック」を表示する（SPEC §8.3）。</summary>
    public bool ShowButtonLabel { get; set; }
    /// <summary>ボタンを押している間、カーソルに印を表示する（ドラッグの説明向け）。</summary>
    public bool ShowWhileHeld { get; set; }
}

public sealed class LaserSettings
{
    public bool Enabled { get; set; } = true;
    public string Color { get; set; } = "#FF2020";
    /// <summary>軌跡の線幅（DIP）。</summary>
    public double Width { get; set; } = 8;
    /// <summary>残像時間（秒）。</summary>
    public double TrailSeconds { get; set; } = 0.8;
    /// <summary>描画方式（V1.2）。キーボード・マウスボタンのトリガー共通。</summary>
    public LaserTriggerMode TriggerMode { get; set; } = LaserTriggerMode.Hold;
    public MouseTriggerButton MouseButton { get; set; } = MouseTriggerButton.None;
}

public sealed class MagnifierSettings
{
    /// <summary>200 / 300 / 400。</summary>
    public int ZoomPercent { get; set; } = 300;
    /// <summary>拡大窓の幅（DIP）。</summary>
    public double Width { get; set; } = 480;
    /// <summary>拡大窓の高さ（DIP）。</summary>
    public double Height { get; set; } = 300;
    public MouseTriggerButton MouseButton { get; set; } = MouseTriggerButton.None;
}

public sealed class KeyDisplaySettings
{
    // V1.2 の改訂 3 で ON/OFF で使うモード（toggleMode）を廃止した（D-36）。古い設定ファイルの値は読み込み時に無視する。
    public double FontSize { get; set; } = 40;
    public KeyDisplayPosition Position { get; set; } = KeyDisplayPosition.BottomCenter;
    public string TextColor { get; set; } = "#FFFFFF";
    public string BackgroundColor { get; set; } = "#000000";
    public double BackgroundOpacity { get; set; } = 0.8;
    /// <summary>特殊キー・ショートカットの表示時間（秒）。</summary>
    public double ChordSeconds { get; set; } = 2.0;
    /// <summary>Full Text の文字列を最後の入力から保持する時間（秒）。</summary>
    public double TextSeconds { get; set; } = 5.0;
    /// <summary>Enter 後に文字列を残す時間（秒）。</summary>
    public double EnterHoldSeconds { get; set; } = 1.5;
}

public sealed class ControlPanelSettings
{
    /// <summary>左上の位置（物理ピクセル）。null は未保存。</summary>
    public int? Left { get; set; }
    public int? Top { get; set; }
    /// <summary>サイズ（DIP）。</summary>
    public double Width { get; set; } = 300;
    public double Height { get; set; } = 520;
}

/// <summary>設定ファイルの JSON 形式（camelCase・インデント・列挙は文字列）。</summary>
public static class SettingsJson
{
    public static readonly JsonSerializerOptions Options = CreateOptions();

    private static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = true,
            ReadCommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true,
            NumberHandling = JsonNumberHandling.AllowReadingFromString,
            // "Ctrl+Shift+F9" の + を + にせず、手で読み書きしやすくする（ローカル専用の設定ファイルで HTML 埋め込みはしない）。
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        };
        options.Converters.Add(new LenientEnumConverterFactory());
        return options;
    }
}

/// <summary>
/// 列挙を名前の文字列で読み書きする。未知の名前は例外にせず -1（未定義値）として読み、
/// SettingsNormalizer で既定値に戻す（1 項目の誤りで設定ファイル全体を破損扱いにしないため）。
/// </summary>
internal sealed class LenientEnumConverterFactory : JsonConverterFactory
{
    public override bool CanConvert(Type typeToConvert) => typeToConvert.IsEnum;

    public override JsonConverter CreateConverter(Type typeToConvert, JsonSerializerOptions options) =>
        (JsonConverter)Activator.CreateInstance(typeof(LenientEnumConverter<>).MakeGenericType(typeToConvert))!;

    private sealed class LenientEnumConverter<T> : JsonConverter<T> where T : struct, Enum
    {
        private static readonly T Undefined = (T)Enum.ToObject(typeof(T), -1);

        public override T Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            switch (reader.TokenType)
            {
                case JsonTokenType.String:
                    var text = reader.GetString();
                    return Enum.TryParse<T>(text, ignoreCase: true, out var value) && !int.TryParse(text, out _) ? value : Undefined;
                case JsonTokenType.Number when reader.TryGetInt32(out var n):
                    return (T)Enum.ToObject(typeof(T), n);
                default:
                    reader.Skip();
                    return Undefined;
            }
        }

        public override void Write(Utf8JsonWriter writer, T value, JsonSerializerOptions options) =>
            writer.WriteStringValue(value.ToString());
    }
}
