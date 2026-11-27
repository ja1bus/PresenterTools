using System.Windows.Media;
using static PresenterTools.Interop.NativeMethods;

namespace PresenterTools.Overlay;

/// <summary>
/// 枠なし・背景透明・最前面・マウス透過・非アクティブ化のウィンドウの基底（SPEC §14）。
/// 物理ピクセルの矩形の固定は <see cref="PinnedBoundsWindow"/>（V1.7.0 で切り出し。動作は変えていない）。
/// </summary>
public abstract class ClickThroughWindow : PinnedBoundsWindow
{
    protected ClickThroughWindow()
    {
        Background = Brushes.Transparent;
        Focusable = false;
        IsHitTestVisible = false;
    }

    protected override int ExtendedStyle => WS_EX_TRANSPARENT | WS_EX_LAYERED | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE;

    protected override IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        switch (msg)
        {
            case WM_NCHITTEST:
                handled = true;
                return new IntPtr(HTTRANSPARENT);
            case WM_MOUSEACTIVATE:
                handled = true;
                return new IntPtr(MA_NOACTIVATE);
            case WM_ACTIVATE when (wParam.ToInt64() & 0xFFFF) != WA_INACTIVE && lParam != IntPtr.Zero:
                // 拡大率の違う画面へ移ると WPF（HwndTarget.OnDpiChanged）が SWP_NOACTIVATE なしで SetWindowPos を呼び、
                // 自アプリの窓（画面ペンの入力面・設定画面など）が前面だとこの窓がアクティブになる（V1.7.0 総合試験で確認）。
                // 直前にアクティブだった同じスレッドの窓へ返す。
                SetActiveWindow(lParam);
                break;
        }
        return IntPtr.Zero;
    }
}
