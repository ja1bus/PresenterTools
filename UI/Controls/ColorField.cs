using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using PresenterTools.Core.Models;

namespace PresenterTools.UI.Controls;

/// <summary>
/// 色の入力欄。"#RRGGBB" の入力欄・見本・よく使う色のボタンを並べる。
/// 不正な形式のまま適用しようとした場合は Settings 画面の検証で止める。
/// </summary>
public sealed class ColorField : StackPanel
{
    public static readonly DependencyProperty ColorProperty = DependencyProperty.Register(
        nameof(Color), typeof(string), typeof(ColorField),
        // 既定値を実在の色にすると、同じ色がバインドされたときに変更通知が起きず欄が空のままになる（V1.0 の #000000 で発生）。
        new FrameworkPropertyMetadata("", FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, OnColorChanged));

    private static readonly string[] Presets =
        ["#FFD400", "#FF2020", "#FF8C00", "#2F80FF", "#00C853", "#E040FB", "#FFFFFF", "#000000"];

    private readonly TextBox _text = new() { Width = 80, VerticalContentAlignment = VerticalAlignment.Center };
    private readonly Border _preview = new()
    {
        Width = 26,
        Height = 22,
        BorderBrush = Brushes.Gray,
        BorderThickness = new Thickness(1),
        Margin = new Thickness(0, 0, 6, 0),
    };

    public ColorField()
    {
        Orientation = Orientation.Horizontal;
        Children.Add(_preview);
        Children.Add(_text);
        _text.TextChanged += (_, _) =>
        {
            if (Color != _text.Text) Color = _text.Text.Trim();
        };
        foreach (var preset in Presets)
        {
            var c = ArgbColor.TryParse(preset, out var v) ? v : default;
            var button = new Button
            {
                Width = 20,
                Height = 20,
                Margin = new Thickness(4, 0, 0, 0),
                Background = new SolidColorBrush(System.Windows.Media.Color.FromRgb(c.R, c.G, c.B)),
                BorderBrush = Brushes.DimGray,
                ToolTip = preset,
                Focusable = false,
            };
            button.Click += (_, _) => Color = preset;
            Children.Add(button);
        }
    }

    public string Color
    {
        get => (string)GetValue(ColorProperty);
        set => SetValue(ColorProperty, value);
    }

    private static void OnColorChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var field = (ColorField)d;
        var text = e.NewValue as string ?? "";
        if (field._text.Text != text) field._text.Text = text;
        if (ArgbColor.TryParse(text, out var c))
        {
            field._preview.Background = new SolidColorBrush(System.Windows.Media.Color.FromArgb(c.A, c.R, c.G, c.B));
            field._text.ClearValue(Control.BorderBrushProperty);
        }
        else
        {
            field._preview.Background = Brushes.Transparent;
            field._text.BorderBrush = Brushes.Red;
        }
    }
}
