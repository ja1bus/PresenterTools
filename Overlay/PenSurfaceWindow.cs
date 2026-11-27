using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using PresenterTools.Core.Geometry;
using static PresenterTools.Interop.NativeMethods;

namespace PresenterTools.Overlay;

/// <summary>入力面が受けた操作の通知先（<see cref="PenController"/>）。</summary>
public interface IPenSurfaceSink
{
    bool IsMenuOpen { get; }
    bool CanBeginStroke { get; }
    void BeginStroke(Point p);
    void AddPoint(Point p, bool inside);
    /// <summary>描いている画を確定する（離した・キャプチャ喪失・非アクティブ化）。</summary>
    void EndStroke();
    void OpenMenu(Point p);
    void EscExit();
    /// <summary>入力面が Alt+F4 等で閉じられようとした（Closing を取り消した後に呼ぶ）。</summary>
    void CloseRequested(PenSurfaceWindow surface);
    void SurfaceActivated();
    /// <summary>入力面がアクティブになる直前の前面の窓（戻す候補）。</summary>
    void NoteForeground(IntPtr foreground);
    void CaptureFailed();
    /// <summary>消しゴムを選んでいる（V1.8.0。docs/archive/design-1.8.0.md P8）。</summary>
    bool IsEraser { get; }
    /// <summary>消しゴムの間、ボタンを押していない移動。</summary>
    void Hover(PenSurfaceWindow surface, Point p);
    /// <summary>消しゴムの間に入力面の外へ出た・非アクティブのままになった。</summary>
    void HoverEnded(PenSurfaceWindow surface);
    /// <summary>消しゴムの間の左ボタンの押下。</summary>
    void EraseAt(PenSurfaceWindow surface, Point p);
    /// <summary>消しゴムの間に左ボタンを離した。</summary>
    void EraserReleased(PenSurfaceWindow surface, Point p);
}

/// <summary>
/// 画面ペンの入力面（docs/archive/design-1.7.0.md D4）。書き込み先の全体領域を覆い、左右クリック・Esc を受けて背後のアプリへ通さない。
/// <see cref="ClickThroughWindow"/> の例外として、透過スタイル・非アクティブ化の応答を持たない。共通化は物理矩形の固定だけ。
/// </summary>
public sealed class PenSurfaceWindow : PinnedBoundsWindow
{
    private readonly IPenSurfaceSink _sink;
    private bool _drawing;
    private bool _escDown;
    private bool _rightArmed;
    private bool _allowClose;
    // 最初のマウスのボタンまでアクティブにならない（WS_EX_NOACTIVATE）。作成時の DPI 処理や、前の入力面を閉じた後の
    // OS の選択で前面になり、クリック前のキー・Esc を受けるのを防ぐ（docs/archive/design-1.8.0.md P6 手順 9。コードレビュー R3）。
    private bool _activationEnabled;

    public PenSurfaceWindow(IPenSurfaceSink sink, RectInt bounds)
    {
        _sink = sink;
        Title = "PresenterTools Pen";
        // アルファ 0 の画素はレイヤードウィンドウでクリックが透過するため、全面をアルファ 1/255 にする。
        var background = new SolidColorBrush(Color.FromArgb(1, 0, 0, 0));
        background.Freeze();
        Background = background;
        Focusable = true;
        Cursor = Cursors.Pen;
        // 窓を最初から書き込み先の画面の上に作る（主画面で作ってから拡大率の違う画面へ移す往復を避ける）。
        // 作成位置の換算はシステムの DPI を使う。なお拡大率がシステムと違う画面では、作成時の WPF の DPI 処理で
        // 窓がスレッド内でアクティブになり、前面を取れる状況では開始時に前面になる（初期試験で確認。終了時の復帰は
        // 開始時の前面を候補にして補う。PenController.Activate）。
        var system = GetDpiForSystem() / 96.0;
        WindowStartupLocation = WindowStartupLocation.Manual;
        Left = (bounds.Left + bounds.Width / 2) / system;
        Top = (bounds.Top + bounds.Height / 2) / system;
        SetBounds(bounds);
        Closing += OnClosingWindow;
        Activated += (_, _) => _sink.SurfaceActivated();
        // 自アプリのクリック透過の窓が一瞬アクティブになり、すぐ返される場合がある（拡大窓が画面をまたいだとき。ClickThroughWindow）。
        // その間は画を続け、返されずに非アクティブのままなら（Alt+Tab 等）そこまでで確定する。
        Deactivated += (_, _) => Dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
        {
            if (IsActive) return;
            FinishStroke();
            _sink.HoverEnded(this); // 通知先が今の入力面と照合する（古い入力面の遅れた通知で消さない）
        });
        MouseLeave += (_, _) => _sink.HoverEnded(this);
        LostMouseCapture += (_, _) => FinishStroke();
    }

    protected override int ExtendedStyle => WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE;

    /// <summary>最初のマウスのボタンを受けた（それまでは前面になっても前の窓へ返す。PenController.OnForegroundChanged。R3）。</summary>
    public bool ActivationEnabled => _activationEnabled;

    /// <summary>最初のマウスのボタンで、アクティブになれるようにする（以後は通常の窓と同じ。Esc で終えられる）。</summary>
    private void EnableActivation()
    {
        if (_activationEnabled) return;
        _activationEnabled = true;
        if (Handle == IntPtr.Zero) return;
        var current = GetWindowLongPtr(Handle, GWL_EXSTYLE).ToInt64();
        SetWindowLongPtr(Handle, GWL_EXSTYLE, new IntPtr(current & ~(long)WS_EX_NOACTIVATE));
    }

    /// <summary>道具・上限に応じたカーソル（P4。PenController.UpdateCursor が決める）。</summary>
    public void SetToolCursor(Cursor cursor)
    {
        if (!ReferenceEquals(Cursor, cursor)) Cursor = cursor;
    }

    /// <summary>描いている画をそこまでで確定し、キャプチャを外す（旗で一度だけ）。</summary>
    public void FinishStroke()
    {
        if (!_drawing) return;
        _drawing = false;
        _sink.EndStroke();
        if (IsMouseCaptured) ReleaseMouseCapture();
    }

    /// <summary><see cref="PenController"/> から閉じる（Closing を取り消さない）。</summary>
    public void CloseByController()
    {
        _allowClose = true;
        FinishStroke();
        Close();
    }

    protected override IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_MOUSEACTIVATE)
        {
            // アクティブになる直前の前面を、終了時に戻す候補として記録する（D4）。応答は既定（MA_ACTIVATE）のまま。
            _sink.NoteForeground(GetForegroundWindow());
            if (!_activationEnabled)
            {
                // 最初のクリック: ここでは OS にアクティブにさせず（WS_EX_NOACTIVATE のままの応答では、スレッドの中だけ
                // アクティブになり前面が元の窓に残る。工程Fで確認）、押下の処理で前面にする（OnPreviewMouseDown。R3）。
                handled = true;
                return new IntPtr(MA_NOACTIVATE);
            }
        }
        return IntPtr.Zero;
    }

    private bool Inside(Point p) => p.X >= 0 && p.Y >= 0 && p.X < ActualWidth && p.Y < ActualHeight;

    protected override void OnPreviewMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        e.Handled = true;
        if (_sink.IsMenuOpen || _drawing) return;
        if (_sink.IsEraser)
        {
            _sink.EraseAt(this, e.GetPosition(this));
            return;
        }
        if (!_sink.CanBeginStroke) return;
        if (!CaptureMouse())
        {
            _sink.CaptureFailed();
            return;
        }
        _drawing = true;
        _sink.BeginStroke(e.GetPosition(this));
    }

    protected override void OnPreviewMouseMove(MouseEventArgs e)
    {
        if (!_drawing)
        {
            if (_sink.IsEraser && e.LeftButton != MouseButtonState.Pressed && e.RightButton != MouseButtonState.Pressed)
                _sink.Hover(this, e.GetPosition(this));
            return;
        }
        if (e.LeftButton != MouseButtonState.Pressed)
        {
            // 離した通知を取りこぼした（別の窓で離した等）。そこまでで確定する。
            FinishStroke();
            return;
        }
        var p = e.GetPosition(this);
        _sink.AddPoint(p, Inside(p));
    }

    protected override void OnPreviewMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        e.Handled = true;
        if (!_drawing && _sink.IsEraser)
        {
            _sink.EraserReleased(this, e.GetPosition(this));
            return;
        }
        FinishStroke();
    }

    protected override void OnPreviewMouseRightButtonDown(MouseButtonEventArgs e)
    {
        e.Handled = true;
        // 左ドラッグ中に押した右ボタンは、押下も解放も無視する（D4）。
        _rightArmed = !_drawing && !_sink.IsMenuOpen;
    }

    protected override void OnPreviewMouseRightButtonUp(MouseButtonEventArgs e)
    {
        e.Handled = true;
        if (!_rightArmed) return;
        _rightArmed = false;
        if (_drawing || _sink.IsMenuOpen) return;
        _sink.OpenMenu(e.GetPosition(this));
    }

    // 中ボタン・ホイールは何もしない（背後のアプリにも届かない。既知の制約）。
    protected override void OnPreviewMouseWheel(MouseWheelEventArgs e) => e.Handled = true;

    protected override void OnPreviewMouseDown(MouseButtonEventArgs e)
    {
        if (!_activationEnabled)
        {
            // 最初のクリック: WS_EX_NOACTIVATE を外して前面にする（クリックを受けた本人のプロセスなので前面にできる。R3）。
            // アクティブになった後にパネルを上へ置き直す（PenController.SurfaceActivated）。
            EnableActivation();
            var ok = SetForegroundWindow(Handle);
            PresenterTools.Services.TestTrace.Write("pen-surface-activate", ok ? "ok" : "refused");
        }
        if (e.ChangedButton is MouseButton.Middle or MouseButton.XButton1 or MouseButton.XButton2) e.Handled = true;
        base.OnPreviewMouseDown(e);
    }

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        // Esc 以外のキーは処理せず、背後のアプリにも送らない（D14 の1）。Alt との組み合わせ（Key.System）は
        // 既定の処理に任せる（Alt+F4 を Closing へ通し、ペンだけを終える。D4）。
        if (e.Key == Key.System) return;
        e.Handled = true;
        if (e.Key != Key.Escape) return;
        if (!e.IsRepeat) _escDown = true;
    }

    protected override void OnPreviewKeyUp(KeyEventArgs e)
    {
        if (e.Key == Key.System) return;
        e.Handled = true;
        if (e.Key != Key.Escape || !_escDown) return;
        // 離したときに終える。終了と前面の復帰を離した後に行うため、同じキーのリピート・KeyUp が途中で別の窓へ渡らない（D7）。
        _escDown = false;
        _sink.EscExit();
    }

    private void OnClosingWindow(object? sender, CancelEventArgs e)
    {
        if (_allowClose) return;
        // Alt+F4 などは取り消し、通常の終了経路（線は残す）へ結ぶ（D4）。
        // Closing の中では同じ窓の Close を呼べないため、取り消して戻った後に通常の終了を 1 回だけ行う（R2）。
        e.Cancel = true;
        Dispatcher.BeginInvoke(() =>
        {
            if (!_allowClose) _sink.CloseRequested(this);
        });
    }
}
