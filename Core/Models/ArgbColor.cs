using System.Globalization;

namespace PresenterTools.Core.Models;

/// <summary>WPF に依存しない ARGB 色。設定ファイルでは "#RRGGBB" / "#AARRGGBB" の文字列で持つ。</summary>
public readonly record struct ArgbColor(byte A, byte R, byte G, byte B)
{
    public static ArgbColor FromRgb(byte r, byte g, byte b) => new(255, r, g, b);

    public static bool TryParse(string? text, out ArgbColor color)
    {
        color = default;
        if (string.IsNullOrWhiteSpace(text)) return false;
        var s = text.Trim();
        if (!s.StartsWith('#')) return false;
        s = s[1..];
        if (s.Length != 6 && s.Length != 8) return false;
        if (!uint.TryParse(s, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var value)) return false;

        color = s.Length == 6
            ? new ArgbColor(255, (byte)(value >> 16), (byte)(value >> 8), (byte)value)
            : new ArgbColor((byte)(value >> 24), (byte)(value >> 16), (byte)(value >> 8), (byte)value);
        return true;
    }

    public static bool IsValid(string? text) => TryParse(text, out _);

    /// <summary>不透明なら "#RRGGBB"、半透明なら "#AARRGGBB"。</summary>
    public override string ToString() => A == 255 ? $"#{R:X2}{G:X2}{B:X2}" : $"#{A:X2}{R:X2}{G:X2}{B:X2}";

    /// <summary>アルファだけ差し替える（0～1 の不透明度を掛ける）。</summary>
    public ArgbColor WithOpacity(double opacity)
    {
        var o = Math.Clamp(double.IsFinite(opacity) ? opacity : 1.0, 0.0, 1.0);
        return this with { A = (byte)Math.Round(A * o) };
    }
}
