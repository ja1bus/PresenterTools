using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using PresenterTools.Core.Effects;
using PresenterTools.Core.Geometry;
using PresenterTools.Core.Localization;
using PresenterTools.Core.Models;
using PresenterTools.Overlay;

namespace PresenterTools.UI.Controls;

/// <summary>
/// Settings の Laser タブの試し描きの枠（SPEC §10.2・D-31）。枠の中でマウスを動かすと、編集中の色・線幅・残像時間で軌跡を描く。
/// オーバーレイと同じ <see cref="LaserLayer"/> を使い、座標は枠内の DIP をそのまま渡す（倍率 1）。
/// Master・Laser の ON/OFF とは無関係で、枠の外には描かない。
/// </summary>
public sealed class LaserPreviewBox : Border
{
    private readonly LaserLayer _layer = new();
    private readonly LaserTrail _trail = new();
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private bool _drawing;
    private bool _animating;

    public LaserPreviewBox()
    {
        Background = Brushes.White;
        BorderBrush = new SolidColorBrush(Color.FromRgb(0xB0, 0xB0, 0xB0));
        BorderThickness = new Thickness(1);
        ClipToBounds = true;
        Cursor = Cursors.Cross;
        var grid = new Grid();
        grid.Children.Add(new TextBlock
        {
            Text = Strings.LaserPreviewHint,
            TextWrapping = TextWrapping.Wrap,
            TextAlignment = TextAlignment.Center,
            Margin = new Thickness(8, 0, 8, 0),
            Foreground = new SolidColorBrush(Color.FromRgb(0x9E, 0x9E, 0x9E)),
            FontSize = 12,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            IsHitTestVisible = false,
        });
        grid.Children.Add(_layer);
        Child = grid;
        Unloaded += (_, _) =>
        {
            _drawing = false;
            _trail.Clear();
            StopAnimation();
        };
    }

    private double Now => _clock.Elapsed.TotalSeconds;

    /// <summary>編集中の見た目を反映する。色が入力途中で解釈できない間は前の色のまま。</summary>
    public void ApplySettings(LaserSettings laser)
    {
        if (ArgbColor.TryParse(laser.Color, out var c))
            _layer.SetStyle(Color.FromArgb(c.A, c.R, c.G, c.B), laser.Width);
        _trail.LifetimeSeconds = laser.TrailSeconds;
        Redraw();
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        var p = e.GetPosition(_layer);
        if (!_drawing) _trail.BeginStroke(p.X, p.Y, Now);
        else _trail.Add(p.X, p.Y, Now);
        _drawing = true;
        StartAnimation();
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        base.OnMouseLeave(e);
        _drawing = false; // 残りの軌跡は残像時間で消える
        _trail.EndStroke();
    }

    private void Redraw()
    {
        var bounds = new RectInt(0, 0, (int)Math.Ceiling(ActualWidth), (int)Math.Ceiling(ActualHeight));
        _layer.Update(_trail, bounds, 1, Now, _drawing);
    }

    private void StartAnimation()
    {
        if (_animating) return;
        _animating = true;
        CompositionTarget.Rendering += OnRendering;
    }

    private void StopAnimation()
    {
        if (!_animating) return;
        _animating = false;
        CompositionTarget.Rendering -= OnRendering;
    }

    private void OnRendering(object? sender, EventArgs e)
    {
        _trail.Prune(Now);
        Redraw();
        if (!_drawing && _trail.IsEmpty) StopAnimation();
    }
}
