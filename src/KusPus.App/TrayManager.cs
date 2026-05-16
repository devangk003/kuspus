using H.NotifyIcon;
using H.NotifyIcon.Core;

namespace KusPus.App;

/// <summary>
/// Minimal tray icon per TECH_SPEC §8.9 + §25. Phase 6 ships Quit only; the
/// "Toggle Recorder", "Active Model", "Preferences", and "History" entries are
/// Phase 9 / 10 / 11 additions.
/// </summary>
internal sealed class TrayManager : IDisposable
{
    private readonly TaskbarIcon _icon;

    public TrayManager(AppCoordinator coordinator, Action onQuit)
    {
        _icon = new TaskbarIcon
        {
            ToolTipText = "KusPus",
            Icon = new System.Drawing.Icon(System.Drawing.SystemIcons.Application, 16, 16),
        };

        var menu = new System.Windows.Controls.ContextMenu();

        var toggle = new System.Windows.Controls.MenuItem { Header = "Toggle Recorder" };
        toggle.Click += (_, _) => coordinator.ToggleFromTray();
        menu.Items.Add(toggle);

        menu.Items.Add(new System.Windows.Controls.Separator());

        var quit = new System.Windows.Controls.MenuItem { Header = "Quit" };
        quit.Click += (_, _) => onQuit();
        menu.Items.Add(quit);

        _icon.ContextMenu = menu;
    }

    public void Dispose()
    {
        _icon.Dispose();
    }
}
