using System.Windows.Threading;
using PresenterTools.Core.Geometry;
using PresenterTools.Core.Models;
using PresenterTools.Services;
using static PresenterTools.Interop.NativeMethods;

namespace PresenterTools.Overlay;

/// <summary>
/// Magnifier の動作（SPEC §16）。ON の間だけ約 30 fps で画面を取得し、拡大窓をカーソルの近くへ配置する。
/// OFF の間は画面取得もタイマーも止める。
/// </summary>
public sealed class MagnifierController : IDisposable
{
    private readonly MonitorService _monitors;
    private readonly DispatcherTimer _timer;
    private readonly ScreenCapture _capture = new();
    private MagnifierWindow? _window;
    private MagnifierSettings _settings = new();
    private MagnifierQuadrant? _quadrant;
    /// <summary>拡大窓の今の位置（物理ピクセル）。窓を出していなければ null。</summary>
    private RectInt? _shownRect;

    public MagnifierController(MonitorService monitors)
    {
        _monitors = monitors;
        _timer = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(33) };
        _timer.Tick += (_, _) => UpdateFrame();
    }

    public bool IsRunning => _timer.IsEnabled;

    public void ApplySettings(MagnifierSettings settings)
    {
        _settings = settings;
        _window?.SetZoomLabel(settings.ZoomPercent);
        if (IsRunning) UpdateFrame();
    }

    public void Start()
    {
        if (IsRunning) return;
        _window ??= new MagnifierWindow();
        _window.SetZoomLabel(_settings.ZoomPercent);
        _quadrant = null;
        _shownRect = null;
        UpdateFrame(); // 表示前に位置と画像を決める
        _window.Show();
        _window.BringToTop();
        _timer.Start();
    }

    public void Stop()
    {
        if (!IsRunning && _window is null) return;
        _timer.Stop();
        // 窓は破棄して、取得用のバッファも解放する（長時間の常駐でメモリを持ち続けない）。
        _window?.Close();
        _window = null;
        _shownRect = null;
        _capture.Dispose();
    }

    public void Dispose() => Stop();

    private void UpdateFrame()
    {
        if (_window is null || !GetCursorPos(out var pt)) return;
        var cursor = new PointInt(pt.X, pt.Y);
        var monitor = _monitors.FromPoint(cursor);
        var zoom = _settings.ZoomPercent / 100.0;

        var size = new SizeInt(
            (int)Math.Round(_settings.Width * monitor.DpiScale),
            (int)Math.Round(_settings.Height * monitor.DpiScale));
        var source = MagnifierLayout.ComputeSourceRect(cursor, size, zoom, monitor.Bounds);
        var rect = MagnifierLayout.ComputeWindowRect(size, source, monitor.WorkArea, _quadrant, out var quadrant);
        _quadrant = quadrant;

        // 今の窓が取得範囲に掛かるなら、窓を先に動かして取得は次のフレームにする（窓自身の写り込みを防ぐ。§16.2）。
        var defer = MagnifierLayout.ShouldDeferCapture(_shownRect, rect, source);
        if (!defer && _capture.Capture(source)) _window.SetImage(_capture.Bitmap);
        _window.SetBounds(rect);
        _shownRect = rect;
        _window.SetMarker(
            (cursor.X - source.Left + 0.5) / source.Width,
            (cursor.Y - source.Top + 0.5) / source.Height);
    }
}
