using System.Globalization;
using PresenterTools.Core.Localization;
using PresenterTools.Core.Models;

namespace PresenterTools.Core.ScreenPen;

/// <summary>書き込み先の一覧に出す画面の表示名（docs/archive/design-1.7.0.md D3）。例「1: DELL U2720Q（主） 2560×1440 150%」。</summary>
public static class PenDisplayLabel
{
    /// <summary>GDI 名の末尾の数字（\\.\DISPLAY2 → "2"）。無ければ GDI 名そのもの。</summary>
    public static string Number(string deviceName)
    {
        var end = deviceName.Length;
        var start = end;
        while (start > 0 && char.IsAsciiDigit(deviceName[start - 1])) start--;
        return start < end ? deviceName[start..end] : deviceName;
    }

    /// <summary>名前（取れなければ「ディスプレイ」）。</summary>
    public static string Name(DisplayDescriptor d) =>
        string.IsNullOrWhiteSpace(d.FriendlyName) ? Strings.PenDisplayName : d.FriendlyName.Trim();

    public static string Format(DisplayDescriptor d)
    {
        var marks = (d.IsPrimary ? Strings.PenPrimaryMark : "") + (d.IsDuplicate ? Strings.PenDuplicateMark : "");
        var percent = Math.Round(d.Scale * 100, MidpointRounding.AwayFromZero).ToString(CultureInfo.InvariantCulture);
        return string.Format(CultureInfo.CurrentCulture, Strings.PenDisplayItemFormat,
            Number(d.DeviceName), Name(d), marks, d.Bounds.Width, d.Bounds.Height, percent);
    }

    /// <summary>未接続のときに出す名前として保存する値（名前、取れなければ番号付きの「ディスプレイ」）。</summary>
    public static string SavedLabel(DisplayDescriptor d) =>
        string.IsNullOrWhiteSpace(d.FriendlyName) ? Strings.PenDisplayName + " " + Number(d.DeviceName) : d.FriendlyName.Trim();

    /// <summary>保存した書き込み先の表示用の名前。</summary>
    public static string Saved(PenSettings pen) =>
        pen.TargetLabel.Length > 0 ? pen.TargetLabel : pen.TargetDeviceName.Length > 0 ? pen.TargetDeviceName : Strings.PenDisplayName;
}
