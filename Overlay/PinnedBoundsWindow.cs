using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using PresenterTools.Core.Geometry;
using static PresenterTools.Interop.NativeMethods;

namespace PresenterTools.Overlay;

/// <summary>
/// 位置とサイズを物理ピクセルで指定し、WM_WINDOWPOSCHANGING で常にその矩形へ固定する窓の基底
/// （拡大率の異なるモニターへ移したときに WPF が WM_DPICHANGED でサイズを変えるのを打ち消すため）。
/// V1.7.0 で <see cref="ClickThroughWindow"/> から切り出し、画面ペンの入力面（<see cref="PenSurfaceWindow"/>）と共有する。
/// 透過・非アクティブ化などの入力の扱いは派生クラスが決める。
/// </summary>
public abstract class PinnedBoundsWindow : Window
{
    private RectInt _bounds;
    private bool _hasBounds;

    protected PinnedBoundsWindow()
    {
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Topmost = true;
        ShowInTaskbar = false;
        ShowActivated = false;
        ResizeMode = ResizeMode.NoResize;
        Width = Height = 1;
    }

    public IntPtr Handle { get; private set; }

    /// <summary>このウィンドウの DPI 倍率（1.0 = 100%）。</summary>
    public double Scale => VisualTreeHelper.GetDpi(this).DpiScaleX;

    public RectInt Bounds => _bounds;

    /// <summary>派生クラスが加える拡張スタイル。</summary>
    protected abstract int ExtendedStyle { get; }

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
        AddExtendedStyle(Handle, ExtendedStyle);
        HwndSource.FromHwnd(Handle)?.AddHook(BaseWndProc);
        if (_hasBounds)
            SetWindowPos(Handle, HWND_TOPMOST, _bounds.Left, _bounds.Top, _bounds.Width, _bounds.Height, SWP_NOACTIVATE);
    }

    private IntPtr BaseWndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_WINDOWPOSCHANGING && _hasBounds)
        {
            var pos = Marshal.PtrToStructure<WINDOWPOS>(lParam);
            if ((pos.flags & SWP_NOMOVE) == 0) { pos.x = _bounds.Left; pos.y = _bounds.Top; }
            if ((pos.flags & SWP_NOSIZE) == 0) { pos.cx = _bounds.Width; pos.cy = _bounds.Height; }
            Marshal.StructureToPtr(pos, lParam, false);
            return IntPtr.Zero;
        }
        return WndProc(hwnd, msg, wParam, lParam, ref handled);
    }

    /// <summary>派生クラスのメッセージ処理（矩形の固定の後）。</summary>
    protected virtual IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled) => IntPtr.Zero;
}
