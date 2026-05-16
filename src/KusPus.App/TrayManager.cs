using WinFormsApp = System.Windows.Forms;

namespace KusPus.App;

/// <summary>
/// Tray icon per TECH_SPEC §8.9 + §25. Switched from <c>H.NotifyIcon.Wpf</c> to
/// <c>System.Windows.Forms.NotifyIcon</c> after Phase 6 manual smoke: H.NotifyIcon
/// didn't actually surface the tray icon, and the WinForms version is well-trodden
/// in WPF apps with <c>UseWindowsForms=true</c> alongside <c>UseWPF=true</c>.
/// </summary>
internal sealed class TrayManager : IDisposable
{
    private readonly WinFormsApp.NotifyIcon _icon;

    public TrayManager(AppCoordinator coordinator, Action onQuit)
    {
        _icon = new WinFormsApp.NotifyIcon
        {
            Icon = System.Drawing.SystemIcons.Application,
            Text = "KusPus",
            Visible = true,
        };

        var menu = new WinFormsApp.ContextMenuStrip();
        menu.Items.Add("Toggle Recorder", null, (_, _) => coordinator.ToggleFromTray());
        menu.Items.Add(new WinFormsApp.ToolStripSeparator());
        menu.Items.Add("Quit", null, (_, _) => onQuit());
        _icon.ContextMenuStrip = menu;
    }

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
    }
}
