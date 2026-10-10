using Microsoft.Win32;
using PresenterTools.Core.Geometry;
using PresenterTools.Interop;
using static PresenterTools.Interop.NativeMethods;

namespace PresenterTools.Services;

/// <summary>モニター 1 台分の情報。座標は物理ピクセル、DpiScale は 1.0 = 100%。</summary>
public sealed record MonitorInfo(IntPtr Handle, string DeviceName, RectInt Bounds, RectInt WorkArea, bool IsPrimary, double DpiScale)
{
    public MonitorArea Area => new(Bounds, WorkArea, IsPrimary);
}

/// <summary>
/// モニターの列挙と構成変更の通知（SPEC §13）。一覧はキャッシュし、構成変更時に取り直す。
/// </summary>
public sealed class MonitorService : IDisposable
{
    private readonly System.Windows.Threading.Dispatcher _dispatcher;
    private IReadOnlyList<MonitorInfo> _monitors;

    public MonitorService(System.Windows.Threading.Dispatcher dispatcher)
    {
        _dispatcher = dispatcher;
        _monitors = Enumerate();
        SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;
    }

    /// <summary>モニター構成が変わった（UI スレッドで発生）。</summary>
    public event EventHandler? DisplaysChanged;

    public IReadOnlyList<MonitorInfo> Monitors => _monitors;

    public MonitorInfo Primary => _monitors.FirstOrDefault(m => m.IsPrimary) ?? _monitors[0];

    /// <summary>点を含むモニター（無ければ最も近いモニター）。</summary>
    public MonitorInfo FromPoint(PointInt p)
    {
        foreach (var m in _monitors)
        {
            if (m.Bounds.Contains(p)) return m;
        }
        var handle = MonitorFromPoint(new POINT { X = p.X, Y = p.Y }, MONITOR_DEFAULTTONEAREST);
        return FromHandle(handle);
    }

    public MonitorInfo FromWindow(IntPtr hwnd) => FromHandle(MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST));

    public MonitorInfo FromHandle(IntPtr handle) =>
        _monitors.FirstOrDefault(m => m.Handle == handle) ?? Primary;

    public void Refresh() => _monitors = Enumerate();

    public void Dispose() => SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;

    private void OnDisplaySettingsChanged(object? sender, EventArgs e)
    {
        // SystemEvents は別スレッドから呼ばれることがあるため UI スレッドへ移す。
        _dispatcher.BeginInvoke(() =>
        {
            Refresh();
            DisplaysChanged?.Invoke(this, EventArgs.Empty);
        });
    }

    private static IReadOnlyList<MonitorInfo> Enumerate()
    {
        var list = new List<MonitorInfo>();
        EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (IntPtr h, IntPtr _, ref RECT _, IntPtr _) =>
        {
            var info = new MONITORINFOEX { cbSize = System.Runtime.InteropServices.Marshal.SizeOf<MONITORINFOEX>() };
            if (GetMonitorInfo(h, ref info))
            {
                list.Add(new MonitorInfo(
                    h,
                    info.szDevice,
                    ToRect(info.rcMonitor),
                    ToRect(info.rcWork),
                    (info.dwFlags & MONITORINFOF_PRIMARY) != 0,
                    GetScale(h)));
            }
            return true;
        }, IntPtr.Zero);

        if (list.Count == 0)
        {
            // 取得できない異常時の保険（通常は起きない）。
            var r = new RectInt(0, 0, 1920, 1080);
            list.Add(new MonitorInfo(IntPtr.Zero, "", r, r, true, 1.0));
        }
        return list;
    }

    private static double GetScale(IntPtr monitor) =>
        GetDpiForMonitor(monitor, MDT_EFFECTIVE_DPI, out var dpiX, out _) == 0 && dpiX > 0 ? dpiX / 96.0 : 1.0;

    private static RectInt ToRect(RECT r) => RectInt.FromLTRB(r.Left, r.Top, r.Right, r.Bottom);
}
