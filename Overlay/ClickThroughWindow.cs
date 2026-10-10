using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using PresenterTools.Core.Geometry;
using static PresenterTools.Interop.NativeMethods;

namespace PresenterTools.Overlay;

/// <summary>
/// 枠なし・背景透明・最前面・マウス透過・非アクティブ化のウィンドウの基底（SPEC §14）。
/// 位置とサイズは物理ピクセルで指定し、WM_WINDOWPOSCHANGING で常にその矩形へ固定する
/// （拡大率の異なるモニターへ移したときに WPF が WM_DPICHANGED でサイズを変えるのを打ち消すため）。
/// </summary>
public abstract class ClickThroughWindow : Window
{
    private RectInt _bounds;
    private bool _hasBounds;

    protected ClickThroughWindow()
    {
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        Topmost = true;
        ShowInTaskbar = false;
        ShowActivated = false;
        ResizeMode = ResizeMode.NoResize;
        Focusable = false;
        IsHitTestVisible = false;
        Width = Height = 1;
    }

    public IntPtr Handle { get; private set; }

    /// <summary>このウィンドウの DPI 倍率（1.0 = 100%）。</summary>
    public double Scale => VisualTreeHelper.GetDpi(this).DpiScaleX;

    public RectInt Bounds => _bounds;

    /// <summary>物理ピクセルで位置とサイズを設定する。</summary>
    public void SetBounds(RectInt bounds)
    {
        var sizeChanged = !_hasBounds || bounds.Width != _bounds.Width || bounds.Height != _bounds.Height;
        if (_hasBounds && bounds == _bounds) return;
        _bounds = bounds;
        _hasBounds = true;
        if (Handle == IntPtr.Zero) return;
        var flags = SWP_NOACTIVATE | SWP_NOOWNERZORDER | SWP_NOZORDER | (sizeChanged ? 0 : SWP_NOSIZE);
        SetWindowPos(Handle, IntPtr.Zero, bounds.Left, bounds.Top, bounds.Width, bounds.Height, flags);
    }

    /// <summary>最前面へ出し直す（他の最前面ウィンドウより前にする）。</summary>
    public void BringToTop()
    {
        if (Handle != IntPtr.Zero)
            SetWindowPos(Handle, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        Handle = new WindowInteropHelper(this).Handle;
        AddExtendedStyle(Handle, WS_EX_TRANSPARENT | WS_EX_LAYERED | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE);
        HwndSource.FromHwnd(Handle)?.AddHook(WndProc);
        if (_hasBounds)
            SetWindowPos(Handle, HWND_TOPMOST, _bounds.Left, _bounds.Top, _bounds.Width, _bounds.Height, SWP_NOACTIVATE);
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        switch (msg)
        {
            case WM_WINDOWPOSCHANGING when _hasBounds:
                var pos = Marshal.PtrToStructure<WINDOWPOS>(lParam);
                if ((pos.flags & SWP_NOMOVE) == 0) { pos.x = _bounds.Left; pos.y = _bounds.Top; }
                if ((pos.flags & SWP_NOSIZE) == 0) { pos.cx = _bounds.Width; pos.cy = _bounds.Height; }
                Marshal.StructureToPtr(pos, lParam, false);
                break;
            case WM_NCHITTEST:
                handled = true;
                return new IntPtr(HTTRANSPARENT);
            case WM_MOUSEACTIVATE:
                handled = true;
                return new IntPtr(MA_NOACTIVATE);
        }
        return IntPtr.Zero;
    }
}
