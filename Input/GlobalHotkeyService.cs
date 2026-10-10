using System.Runtime.InteropServices;
using System.Windows.Interop;
using PresenterTools.Core.Input;
using PresenterTools.Core.Localization;
using PresenterTools.Core.Models;
using static PresenterTools.Interop.NativeMethods;

namespace PresenterTools.Input;

public enum HotkeyStatus
{
    Unassigned,
    Registered,
    /// <summary>他のアプリまたは Windows が使用中などで登録できなかった。</summary>
    Failed,
    /// <summary>設定画面での入力中のため一時的に解除している。</summary>
    Suspended,
}

public sealed record HotkeyRegistration(HotkeyAction Action, Shortcut Shortcut, HotkeyStatus Status, int ErrorCode = 0)
{
    public string Describe() => Status switch
    {
        HotkeyStatus.Unassigned => Strings.ShortcutUnassigned,
        HotkeyStatus.Registered => Strings.HotkeyStatusRegistered,
        HotkeyStatus.Suspended => Strings.HotkeyStatusSuspended,
        _ when ErrorCode == ERROR_HOTKEY_ALREADY_REGISTERED => Strings.HotkeyStatusInUse,
        _ => string.Format(Strings.HotkeyStatusErrorFormat, ErrorCode),
    };
}

/// <summary>
/// Win32 RegisterHotKey によるグローバルショートカット（SPEC §11）。
/// メッセージ専用ウィンドウで WM_HOTKEY を受け、UI スレッドで <see cref="Pressed"/> を発生させる。
/// </summary>
public sealed class GlobalHotkeyService : IDisposable
{
    private readonly HwndSource _source;
    private readonly Dictionary<HotkeyAction, HotkeyRegistration> _registrations = [];
    private IReadOnlyDictionary<HotkeyAction, Shortcut> _bindings = new Dictionary<HotkeyAction, Shortcut>();
    private int _suspendCount;

    public GlobalHotkeyService()
    {
        var p = new HwndSourceParameters("PresenterTools.Hotkeys") { ParentWindow = HWND_MESSAGE, Width = 0, Height = 0 };
        _source = new HwndSource(p);
        _source.AddHook(WndProc);
    }

    /// <summary>ショートカットが押された（UI スレッド）。</summary>
    public event Action<HotkeyAction>? Pressed;

    /// <summary>登録状態が変わった。</summary>
    public event EventHandler? RegistrationsChanged;

    public IReadOnlyCollection<HotkeyRegistration> Registrations => _registrations.Values;

    public HotkeyRegistration? Get(HotkeyAction action) => _registrations.GetValueOrDefault(action);

    public IEnumerable<HotkeyRegistration> Failures => _registrations.Values.Where(r => r.Status == HotkeyStatus.Failed);

    /// <summary>設定画面のショートカット入力中で、すべて一時解除しているか（SPEC §32 D-17）。</summary>
    public bool IsSuspended => _suspendCount > 0;

    /// <summary>割当を差し替えて登録し直す。登録に失敗したものを返す。</summary>
    public IReadOnlyList<HotkeyRegistration> Apply(IReadOnlyDictionary<HotkeyAction, Shortcut> bindings)
    {
        _bindings = bindings;
        if (_suspendCount > 0)
        {
            MarkSuspended();
            return [];
        }
        RegisterAll();
        return Failures.ToList();
    }

    /// <summary>一時的にすべて解除する（Settings のショートカット入力中）。<see cref="Resume"/> と対で呼ぶ。</summary>
    public void Suspend()
    {
        if (_suspendCount++ > 0) return;
        UnregisterAll();
        MarkSuspended();
    }

    public void Resume()
    {
        if (_suspendCount == 0 || --_suspendCount > 0) return;
        RegisterAll();
    }

    public void Dispose()
    {
        UnregisterAll();
        _source.RemoveHook(WndProc);
        _source.Dispose();
    }

    private void RegisterAll()
    {
        UnregisterAll();
        _registrations.Clear();
        foreach (var action in HotkeyActionNames.All)
        {
            if (!_bindings.TryGetValue(action, out var sc) || sc.IsEmpty)
            {
                _registrations[action] = new HotkeyRegistration(action, Shortcut.None, HotkeyStatus.Unassigned);
                continue;
            }
            // MOD_NOREPEAT: 押しっぱなしの自動リピートで ON/OFF が連続しないようにする。
            var ok = RegisterHotKey(_source.Handle, IdOf(action), (int)sc.Modifiers | MOD_NOREPEAT, sc.Key);
            _registrations[action] = ok
                ? new HotkeyRegistration(action, sc, HotkeyStatus.Registered)
                : new HotkeyRegistration(action, sc, HotkeyStatus.Failed, Marshal.GetLastWin32Error());
        }
        RegistrationsChanged?.Invoke(this, EventArgs.Empty);
    }

    private void UnregisterAll()
    {
        foreach (var r in _registrations.Values.Where(r => r.Status == HotkeyStatus.Registered))
            UnregisterHotKey(_source.Handle, IdOf(r.Action));
    }

    private void MarkSuspended()
    {
        _registrations.Clear();
        foreach (var action in HotkeyActionNames.All)
        {
            var sc = _bindings.GetValueOrDefault(action);
            _registrations[action] = new HotkeyRegistration(action, sc, sc.IsEmpty ? HotkeyStatus.Unassigned : HotkeyStatus.Suspended);
        }
        RegistrationsChanged?.Invoke(this, EventArgs.Empty);
    }

    private static int IdOf(HotkeyAction action) => (int)action + 1;

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_HOTKEY)
        {
            var action = (HotkeyAction)(wParam.ToInt32() - 1);
            if (Enum.IsDefined(action))
            {
                handled = true;
                Pressed?.Invoke(action);
            }
        }
        return IntPtr.Zero;
    }
}
