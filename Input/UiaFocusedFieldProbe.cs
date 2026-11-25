using System.Windows.Automation;
using PresenterTools.Core.KeyDisplay;
using static PresenterTools.Interop.NativeMethods;

namespace PresenterTools.Input;

/// <summary>
/// 入力先がパスワード欄かを UI Automation で調べる（V1.6.0。詳細設計 §2・§3）。読むのは IsPassword だけで、
/// 欄の名前・値は読まない。<see cref="FocusWatchWorker"/> の専用スレッドから呼ばれる。
/// 前面が PresenterTools 自身の窓なら調べずに Normal（設定画面のショートカット欄など）。
/// </summary>
public sealed class UiaFocusedFieldProbe : IFocusedFieldProbe
{
    private readonly int _ownProcessId = Environment.ProcessId;
    private AutomationFocusChangedEventHandler? _handler;

    public FieldProbeResult Query()
    {
        var foreground = GetForegroundWindow();
        if (IsOwnWindow(foreground)) return new FieldProbeResult(FieldKind.Normal, foreground);
        var element = AutomationElement.FocusedElement
            ?? throw new ElementNotAvailableException();
        return new FieldProbeResult(KindOf(element), foreground);
    }

    public void Subscribe(Func<long> clock, Action<FieldProbeResult, long> handler)
    {
        Unsubscribe();
        AutomationFocusChangedEventHandler h = (sender, _) =>
        {
            // 受けた時刻と前面の窓は、プロパティの取得（対象アプリの応答を待つことがある）より前に読む（コードレビュー R5）。
            var received = clock();
            var foreground = GetForegroundWindow();
            FieldKind kind;
            try
            {
                kind = sender is AutomationElement element && element.Current.ProcessId != _ownProcessId
                    ? KindOf(element)
                    : FieldKind.Normal;
            }
            catch (ElementNotAvailableException)
            {
                return; // 消えた要素のイベントは観測にしない（照会で判定する）
            }
            catch (System.Runtime.InteropServices.COMException)
            {
                return;
            }
            handler(new FieldProbeResult(kind, foreground), received);
        };
        Automation.AddAutomationFocusChangedEventHandler(h);
        _handler = h; // 登録できたものだけを解除の対象にする
    }

    public void Unsubscribe()
    {
        if (_handler is null) return;
        // 解除に成功するまでハンドラーを保持する（失敗したら例外のまま返し、呼び出し側が古い購読を残っているものとして扱う。R6）。
        Automation.RemoveAutomationFocusChangedEventHandler(_handler);
        _handler = null;
    }

    private static FieldKind KindOf(AutomationElement element) =>
        element.Current.IsPassword ? FieldKind.Password : FieldKind.Normal;

    private bool IsOwnWindow(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return false;
        GetWindowThreadProcessId(hwnd, out var pid);
        return pid == _ownProcessId;
    }
}
