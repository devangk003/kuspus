using WinFormsApp = System.Windows.Forms;

namespace KusPus.App;

/// <summary>
/// Tray icon per TECH_SPEC §8.9 + §25. Uses the bundled icon.ico (generated from
/// icons/icon.svg by tools/IconBuilder). Switched from H.NotifyIcon.Wpf to
/// System.Windows.Forms.NotifyIcon during Phase 6 manual smoke — H.NotifyIcon
/// didn't reliably render on Win 11 25H2.
/// </summary>
internal sealed class TrayManager : IDisposable
{
    private readonly WinFormsApp.NotifyIcon _icon;

    public TrayManager(AppCoordinator coordinator, Action onPreferences, Action onQuit)
    {
        _icon = new WinFormsApp.NotifyIcon
        {
            Icon = LoadAppIcon(),
            Text = "KusPus",
            Visible = true,
        };

        // Menu order per APP_DESIGN §5.2 (truncated for v1 — Active-model
        // submenu + History item come with the Models / History tab clusters).
        var menu = new WinFormsApp.ContextMenuStrip();
        menu.Items.Add("Toggle Recorder", null, (_, _) => coordinator.ToggleFromTray());
        menu.Items.Add(new WinFormsApp.ToolStripSeparator());
        menu.Items.Add("Preferences…", null, (_, _) => onPreferences());
        menu.Items.Add(new WinFormsApp.ToolStripSeparator());
        menu.Items.Add("Quit", null, (_, _) => onQuit());
        _icon.ContextMenuStrip = menu;
    }

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Icon?.Dispose();
        _icon.Dispose();
    }

    private static System.Drawing.Icon LoadAppIcon()
    {
        // Resolve the WPF Resource icon.ico via pack URI and hand the stream to
        // System.Drawing.Icon. Keeps the tray, window, and exe icons unified on
        // a single source-of-truth file.
        var info = System.Windows.Application.GetResourceStream(new Uri("pack://application:,,,/icon.ico"));
        if (info is null)
        {
            // Defensive — shouldn't happen because icon.ico is included as a Resource
            // in KusPus.App.csproj. Fall back to the system app icon so the tray still works.
            return System.Drawing.SystemIcons.Application;
        }
        using var stream = info.Stream;
        return new System.Drawing.Icon(stream);
    }
}
