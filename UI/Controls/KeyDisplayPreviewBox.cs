using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using PresenterTools.Core.Localization;
using PresenterTools.Core.Models;
using PresenterTools.Overlay;

namespace PresenterTools.UI.Controls;

/// <summary>
/// Settings の Key Display タブの見本（V1.5.0。SPEC §10.2）。オーバーレイと同じ <see cref="KeyDisplayView"/> に決まった文字を出し、
/// 編集中の文字サイズ・文字色・背景色・背景の不透明度を確かめる。キー入力は読まない（Key Display を ON にしなくても使える）。
/// 文字サイズは実寸で出す（大きくすると枠も高くなる）。表示位置・表示時間は見本では示さない。
/// </summary>
public sealed class KeyDisplayPreviewBox : Border
{
    private static readonly string[] SampleKeys = ["Ctrl", "C"];

    private readonly KeyDisplayView _view = new()
    {
        HorizontalAlignment = HorizontalAlignment.Center,
        VerticalAlignment = VerticalAlignment.Center,
    };

    public KeyDisplayPreviewBox()
    {
        // 背景の不透明度が分かるよう、画面の内容に見立てた縞模様の上に出す。
        Background = CreateBackdrop();
        BorderBrush = new SolidColorBrush(Color.FromRgb(0xB0, 0xB0, 0xB0));
        BorderThickness = new Thickness(1);
        ClipToBounds = true;
        MinHeight = 90;
        Padding = new Thickness(8);
        IsHitTestVisible = false;
        Child = _view;
    }

    /// <summary>編集中の見た目を反映する。色が入力途中で解釈できない間は既定の色で出す（<see cref="KeyDisplayView.ApplySettings"/>）。</summary>
    public void ApplySettings(KeyDisplaySettings settings)
    {
        _view.ApplySettings(settings);
        _view.Update(Strings.KeySampleText, SampleKeys, 1);
    }

    private static Brush CreateBackdrop()
    {
        var brush = new LinearGradientBrush
        {
            StartPoint = new Point(0, 0),
            EndPoint = new Point(24, 24),
            MappingMode = BrushMappingMode.Absolute,
            SpreadMethod = GradientSpreadMethod.Repeat,
        };
        var light = Color.FromRgb(0xF5, 0xF5, 0xF5);
        var dark = Color.FromRgb(0xD6, 0xDE, 0xE8);
        brush.GradientStops.Add(new GradientStop(light, 0));
        brush.GradientStops.Add(new GradientStop(light, 0.5));
        brush.GradientStops.Add(new GradientStop(dark, 0.5));
        brush.GradientStops.Add(new GradientStop(dark, 1));
        brush.Freeze();
        return brush;
    }
}
