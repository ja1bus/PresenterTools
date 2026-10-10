using System.Runtime.InteropServices;
using System.Windows.Threading;
using PresenterTools.Core.Geometry;
using PresenterTools.Core.Input;
using PresenterTools.Core.Models;
using static PresenterTools.Interop.NativeMethods;

namespace PresenterTools.Input;

public enum MouseButtonKind
{
    Left,
    Right,
    XButton1,
    XButton2,
}

/// <summary>
/// WH_MOUSE_LL / WH_KEYBOARD_LL の低レベルフック（SPEC §11.4・§18.2）。
/// 専用スレッドでメッセージループを回してフックを導入し、UI スレッドの負荷がシステム全体の入力を遅らせないようにする。
/// フックのコールバックは最小限の処理だけ行い、結果を Dispatcher 経由で UI スレッドへ渡す。
/// 入力は観測するだけで、割り当てたサイドボタン以外は握りつぶさない。
/// </summary>
public sealed class LowLevelInputHook : IDisposable
{
    private const int MsgConfigure = WM_APP + 1;

    private readonly Dispatcher _dispatcher;
    private readonly Thread _thread;
    private readonly ManualResetEventSlim _ready = new();
    private readonly KeyTranslator _translator = new();
    private readonly LowLevelHookProc _mouseProc;
    private readonly LowLevelHookProc _keyboardProc;
    private readonly int _ownProcessId = Environment.ProcessId;
    private int _threadId;
    private IntPtr _mouseHook;
    private IntPtr _keyboardHook;

    // フックのスレッドと UI スレッドで共有する値
    private long _lastMove;          // 上位 32bit = X、下位 32bit = Y
    private int _movePending;
    private volatile bool _swallowX1;
    private volatile bool _swallowX2;
    private volatile bool _wantMouse;
    private volatile bool _wantKeyboard;

    public LowLevelInputHook(Dispatcher dispatcher)
    {
        _dispatcher = dispatcher;
        _mouseProc = MouseProc;       // デリゲートを GC から守るためフィールドで保持する
        _keyboardProc = KeyboardProc;
        _thread = new Thread(ThreadMain) { IsBackground = true, Name = "PresenterTools.InputHook" };
        _thread.Start();
        _ready.Wait();
    }

    /// <summary>カーソル移動（UI スレッド。移動は合体され、最新座標だけが届く）。</summary>
    public event Action<PointInt>? MouseMoved;

    /// <summary>ボタンの押下・解放（UI スレッド）。</summary>
    public event Action<MouseButtonKind, bool, PointInt>? MouseButton;

    /// <summary>キーイベント（UI スレッド）。2 つ目の引数はイベント時点のフォアグラウンドウィンドウ。</summary>
    public event Action<KeyInput, IntPtr>? KeyEvent;

    /// <summary>フックの導入に失敗した（UI スレッド）。引数はマウスなら true、エラーコード。</summary>
    public event Action<bool, int>? HookFailed;

    public bool IsMouseHooked => _mouseHook != IntPtr.Zero;
    public bool IsKeyboardHooked => _keyboardHook != IntPtr.Zero;

    /// <summary>必要なフックだけを導入し、不要なものは解除する（UI スレッドから呼ぶ）。</summary>
    public void Configure(bool mouse, bool keyboard, MouseTriggerButton swallowA, MouseTriggerButton swallowB)
    {
        _swallowX1 = swallowA == MouseTriggerButton.XButton1 || swallowB == MouseTriggerButton.XButton1;
        _swallowX2 = swallowA == MouseTriggerButton.XButton2 || swallowB == MouseTriggerButton.XButton2;
        if (_wantMouse == mouse && _wantKeyboard == keyboard) return;
        _wantMouse = mouse;
        _wantKeyboard = keyboard;
        PostThreadMessage(_threadId, MsgConfigure, IntPtr.Zero, IntPtr.Zero);
    }

    public void Dispose()
    {
        if (_threadId != 0 && _thread.IsAlive)
        {
            PostThreadMessage(_threadId, WM_QUIT, IntPtr.Zero, IntPtr.Zero);
            _thread.Join(TimeSpan.FromSeconds(2));
        }
        _ready.Dispose();
    }

    private void ThreadMain()
    {
        _threadId = GetCurrentThreadId();
        PeekMessage(out _, IntPtr.Zero, 0, 0, 0); // このスレッドのメッセージキューを作る
        _ready.Set();
        try
        {
            while (GetMessage(out var msg, IntPtr.Zero, 0, 0) > 0)
            {
                if (msg.message == MsgConfigure) ApplyHooks();
            }
        }
        finally
        {
            // 終了時に確実に解除する（SPEC §22）。
            if (_mouseHook != IntPtr.Zero) UnhookWindowsHookEx(_mouseHook);
            if (_keyboardHook != IntPtr.Zero) UnhookWindowsHookEx(_keyboardHook);
            _mouseHook = _keyboardHook = IntPtr.Zero;
        }
    }

    private void ApplyHooks()
    {
        var module = GetModuleHandle(null);
        if (_wantMouse && _mouseHook == IntPtr.Zero)
        {
            _mouseHook = SetWindowsHookEx(WH_MOUSE_LL, _mouseProc, module, 0);
            if (_mouseHook == IntPtr.Zero) ReportFailure(true);
        }
        else if (!_wantMouse && _mouseHook != IntPtr.Zero)
        {
            UnhookWindowsHookEx(_mouseHook);
            _mouseHook = IntPtr.Zero;
        }

        if (_wantKeyboard && _keyboardHook == IntPtr.Zero)
        {
            _keyboardHook = SetWindowsHookEx(WH_KEYBOARD_LL, _keyboardProc, module, 0);
            if (_keyboardHook == IntPtr.Zero) ReportFailure(false);
        }
        else if (!_wantKeyboard && _keyboardHook != IntPtr.Zero)
        {
            UnhookWindowsHookEx(_keyboardHook);
            _keyboardHook = IntPtr.Zero;
        }
    }

    private void ReportFailure(bool mouse)
    {
        var error = Marshal.GetLastWin32Error();
        _dispatcher.BeginInvoke(() => HookFailed?.Invoke(mouse, error));
    }

    private unsafe IntPtr MouseProc(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            var info = (MSLLHOOKSTRUCT*)lParam;
            var p = new PointInt(info->pt.X, info->pt.Y);
            switch ((int)wParam)
            {
                case WM_MOUSEMOVE:
                    Interlocked.Exchange(ref _lastMove, ((long)p.X << 32) | (uint)p.Y);
                    if (Interlocked.Exchange(ref _movePending, 1) == 0)
                        _dispatcher.BeginInvoke(DispatcherPriority.Input, DeliverMove);
                    break;
                case WM_LBUTTONDOWN:
                case WM_LBUTTONUP:
                    PostButton(MouseButtonKind.Left, (int)wParam == WM_LBUTTONDOWN, p);
                    break;
                case WM_RBUTTONDOWN:
                case WM_RBUTTONUP:
                    PostButton(MouseButtonKind.Right, (int)wParam == WM_RBUTTONDOWN, p);
                    break;
                case WM_XBUTTONDOWN:
                case WM_XBUTTONUP:
                    var which = (info->mouseData >> 16) & 0xFFFF;
                    var kind = which == XBUTTON1 ? MouseButtonKind.XButton1 : MouseButtonKind.XButton2;
                    PostButton(kind, (int)wParam == WM_XBUTTONDOWN, p);
                    // 機能に割り当てたサイドボタンだけ握りつぶす（ブラウザの「戻る」等を同時に動かさない。SPEC §12）。
                    if ((kind == MouseButtonKind.XButton1 && _swallowX1) || (kind == MouseButtonKind.XButton2 && _swallowX2))
                        return new IntPtr(1);
                    break;
            }
        }
        return CallNextHookEx(IntPtr.Zero, nCode, wParam, lParam);
    }

    private void DeliverMove()
    {
        Interlocked.Exchange(ref _movePending, 0);
        var packed = Interlocked.Read(ref _lastMove);
        MouseMoved?.Invoke(new PointInt((int)(packed >> 32), (int)(uint)packed));
    }

    private void PostButton(MouseButtonKind kind, bool down, PointInt p) =>
        _dispatcher.BeginInvoke(DispatcherPriority.Input, () => MouseButton?.Invoke(kind, down, p));

    private unsafe IntPtr KeyboardProc(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            var info = (KBDLLHOOKSTRUCT*)lParam;
            var msg = (int)wParam;
            var isDown = msg is WM_KEYDOWN or WM_SYSKEYDOWN;
            if (isDown || msg is WM_KEYUP or WM_SYSKEYUP)
            {
                var foreground = GetForegroundWindow();
                var input = _translator.Translate(info->vkCode, info->scanCode, isDown, foreground);
                _dispatcher.BeginInvoke(DispatcherPriority.Input, () => KeyEvent?.Invoke(input, foreground));
            }
        }
        return CallNextHookEx(IntPtr.Zero, nCode, wParam, lParam);
    }

    /// <summary>ウィンドウが PresenterTools 自身のものか。</summary>
    public bool IsOwnWindow(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return false;
        GetWindowThreadProcessId(hwnd, out var pid);
        return pid == _ownProcessId;
    }
}
