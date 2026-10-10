using System.Windows.Forms;
using PresenterTools.Core.Localization;
using PresenterTools.Core.Models;
using PresenterTools.Core.State;

namespace PresenterTools.Services;

/// <summary>
/// タスクトレイのアイコンとメニュー（SPEC §10.3）。WinForms の NotifyIcon を使う（SPEC §32 D-01）。
/// </summary>
public sealed class TrayService : IDisposable
{
    private readonly NotifyIcon _icon;
    private readonly System.Drawing.Icon? _ownedIcon;
    private readonly ToolStripMenuItem _masterItem;

    public TrayService()
    {
        _ownedIcon = LoadAppIcon();
        _masterItem = new ToolStripMenuItem(Strings.ActionToggleMaster, null, (_, _) => ToggleMasterRequested?.Invoke(this, EventArgs.Empty));

        var menu = new ContextMenuStrip();
        menu.Items.Add(new ToolStripMenuItem(Strings.TrayShowPanel, null, (_, _) => ShowPanelRequested?.Invoke(this, EventArgs.Empty))
        {
            Font = new System.Drawing.Font(menu.Font, System.Drawing.FontStyle.Bold),
        });
        menu.Items.Add(_masterItem);
        menu.Items.Add(new ToolStripMenuItem(Strings.TrayKeyDisplayOff, null, (_, _) => KeyDisplayOffRequested?.Invoke(this, EventArgs.Empty)));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(new ToolStripMenuItem(Strings.TraySettings, null, (_, _) => SettingsRequested?.Invoke(this, EventArgs.Empty)));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(new ToolStripMenuItem(Strings.TrayExit, null, (_, _) => ExitRequested?.Invoke(this, EventArgs.Empty)));

        _icon = new NotifyIcon
        {
            Icon = _ownedIcon ?? System.Drawing.SystemIcons.Application,
            Text = "PresenterTools",
            ContextMenuStrip = menu,
            Visible = true,
        };
        _icon.DoubleClick += (_, _) => ShowPanelRequested?.Invoke(this, EventArgs.Empty);
    }

    public event EventHandler? ShowPanelRequested;
    public event EventHandler? ToggleMasterRequested;
    public event EventHandler? KeyDisplayOffRequested;
    public event EventHandler? SettingsRequested;
    public event EventHandler? ExitRequested;

    public void Update(PresenterState state)
    {
        _masterItem.Checked = state.MasterEnabled;
        var key = state.EffectiveKeyDisplayMode == KeyDisplayMode.Off ? "OFF" : "ON";
        // NotifyIcon.Text は 127 文字まで。
        _icon.Text = $"PresenterTools - Master {(state.MasterEnabled ? "ON" : "OFF")} / Key {key}";
    }

    public void ShowBalloon(string title, string text, bool warning)
    {
        _icon.ShowBalloonTip(5000, title, text, warning ? ToolTipIcon.Warning : ToolTipIcon.Info);
    }

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.ContextMenuStrip?.Dispose();
        _icon.Dispose();
        _ownedIcon?.Dispose();
    }

    /// <summary>exe に埋め込まれたアイコン（csproj の ApplicationIcon）を使う。</summary>
    private static System.Drawing.Icon? LoadAppIcon()
    {
        try
        {
            var path = Environment.ProcessPath;
            if (string.IsNullOrEmpty(path)) return null;
            // 通知領域の大きさ（表示倍率に応じた小アイコン）のフレームを直接取り出す。
            return System.Drawing.Icon.ExtractIcon(path, 0, SystemInformation.SmallIconSize.Width);
        }
        catch (Exception ex) when (ex is ArgumentException or System.IO.IOException)
        {
            return null;
        }
    }
}
