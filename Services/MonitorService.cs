using Microsoft.Win32;
using PresenterTools.Core.Geometry;
using PresenterTools.Core.ScreenPen;
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

    private DisplaySnapshot _snapshot = DisplaySnapshot.Incomplete;

    public MonitorService(System.Windows.Threading.Dispatcher dispatcher)
    {
        _dispatcher = dispatcher;
        (_monitors, _snapshot) = Enumerate();
        SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;
    }

    /// <summary>モニター構成が変わった（UI スレッドで発生）。</summary>
    public event EventHandler? DisplaysChanged;

    public IReadOnlyList<MonitorInfo> Monitors => _monitors;

    /// <summary>
    /// 最後に列挙したときの画面の識別（V1.7.0。画面ペンの書き込み先。docs/archive/design-1.7.0.md D3）。
    /// 既存機能に渡す <see cref="Monitors"/> の行の扱い（失敗した行を飛ばす・拡大率 1.0）は変えない。
    /// </summary>
    public DisplaySnapshot Snapshot => _snapshot;

    /// <summary>
    /// 今の構成を列挙し直して識別だけを返す（<see cref="Monitors"/> は変えない。オーバーレイと食い違わないため）。
    /// 画面ペンの開始・DPI 変更・Master ON・保留の再試行で使う（D11）。
    /// </summary>
    public DisplaySnapshot QuerySnapshot() => Enumerate().Item2;

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

    public void Refresh() => (_monitors, _snapshot) = Enumerate();

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

    private static (IReadOnlyList<MonitorInfo>, DisplaySnapshot) Enumerate()
    {
        var list = new List<MonitorInfo>();
        var raw = new List<RawMonitor>();
        var enumOk = EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (IntPtr h, IntPtr _, ref RECT _, IntPtr _) =>
        {
            var info = new MONITORINFOEX { cbSize = System.Runtime.InteropServices.Marshal.SizeOf<MONITORINFOEX>() };
            if (GetMonitorInfo(h, ref info))
            {
                var scale = TryGetScale(h);
                var m = new MonitorInfo(
                    h,
                    info.szDevice,
                    ToRect(info.rcMonitor),
                    ToRect(info.rcWork),
                    (info.dwFlags & MONITORINFOF_PRIMARY) != 0,
                    scale ?? 1.0);
                list.Add(m);
                raw.Add(new RawMonitor(m.DeviceName, m.Bounds, m.IsPrimary, scale, true));
            }
            else
            {
                // 既存機能には渡さない（従来どおり）が、画面ペンの判定では列挙を不完全とする。
                raw.Add(new RawMonitor("", default, false, null, false));
            }
            return true;
        }, IntPtr.Zero);

        if (list.Count == 0)
        {
            // 取得できない異常時の保険（通常は起きない）。画面ペンの書き込み先にはしない（Placeholder）。
            var r = new RectInt(0, 0, 1920, 1080);
            list.Add(new MonitorInfo(IntPtr.Zero, "", r, r, true, 1.0));
            return (list, DisplaySnapshot.Incomplete);
        }
        return (list, DisplaySnapshotBuilder.Build(raw, enumOk, QueryPaths()));
    }

    private static double? TryGetScale(IntPtr monitor) =>
        GetDpiForMonitor(monitor, MDT_EFFECTIVE_DPI, out var dpiX, out _) == 0 && dpiX > 0 ? dpiX / 96.0 : null;

    /// <summary>
    /// 有効な表示経路ごとに、source の GDI 名と target のデバイスパス・名前を取る（D3）。照会全体が失敗したら null。
    /// 個々の名前の取得に失敗した値は null にする（その GDI 名の画面は識別できないものとして扱う）。
    /// </summary>
    private static IReadOnlyList<RawPath>? QueryPaths()
    {
        for (var attempt = 0; attempt < 3; attempt++)
        {
            if (GetDisplayConfigBufferSizes(QDC_ONLY_ACTIVE_PATHS, out var numPaths, out var numModes) != ERROR_SUCCESS) return null;
            var paths = new DISPLAYCONFIG_PATH_INFO[numPaths];
            var modes = new DISPLAYCONFIG_MODE_INFO[numModes];
            var result = QueryDisplayConfig(QDC_ONLY_ACTIVE_PATHS, ref numPaths, paths, ref numModes, modes, IntPtr.Zero);
            if (result == ERROR_INSUFFICIENT_BUFFER) continue; // 構成が変わった。大きさを取り直す
            if (result != ERROR_SUCCESS) return null;

            var list = new List<RawPath>((int)numPaths);
            for (var i = 0; i < numPaths; i++)
            {
                var p = paths[i];
                var source = new DISPLAYCONFIG_SOURCE_DEVICE_NAME
                {
                    header = new DISPLAYCONFIG_DEVICE_INFO_HEADER
                    {
                        type = DISPLAYCONFIG_DEVICE_INFO_GET_SOURCE_NAME,
                        size = (uint)System.Runtime.InteropServices.Marshal.SizeOf<DISPLAYCONFIG_SOURCE_DEVICE_NAME>(),
                        adapterId = p.sourceInfo.adapterId,
                        id = p.sourceInfo.id,
                    },
                };
                string? gdi = DisplayConfigGetDeviceInfo(ref source) == ERROR_SUCCESS && !string.IsNullOrEmpty(source.viewGdiDeviceName)
                    ? source.viewGdiDeviceName : null;

                var target = new DISPLAYCONFIG_TARGET_DEVICE_NAME
                {
                    header = new DISPLAYCONFIG_DEVICE_INFO_HEADER
                    {
                        type = DISPLAYCONFIG_DEVICE_INFO_GET_TARGET_NAME,
                        size = (uint)System.Runtime.InteropServices.Marshal.SizeOf<DISPLAYCONFIG_TARGET_DEVICE_NAME>(),
                        adapterId = p.targetInfo.adapterId,
                        id = p.targetInfo.id,
                    },
                };
                var targetOk = DisplayConfigGetDeviceInfo(ref target) == ERROR_SUCCESS;
                list.Add(new RawPath(gdi,
                    targetOk && !string.IsNullOrEmpty(target.monitorDevicePath) ? target.monitorDevicePath : null,
                    targetOk ? target.monitorFriendlyDeviceName : null,
                    targetOk,
                    IsInternalOutput(p.targetInfo.outputTechnology)));
            }
            return list;
        }
        return null;
    }

    /// <summary>内蔵の出力（ノート PC の画面など）か。DISPLAYCONFIG_VIDEO_OUTPUT_TECHNOLOGY の INTERNAL・LVDS・埋め込み DisplayPort・埋め込み UDI。</summary>
    private static bool IsInternalOutput(uint technology) =>
        technology is 0x80000000 or 6 or 11 or 13;

    private static RectInt ToRect(RECT r) => RectInt.FromLTRB(r.Left, r.Top, r.Right, r.Bottom);
}
