using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace KusPus.App;

/// <summary>
/// Tiny theme resolver per docs/APP_DESIGN.md §8.7. Reads the user's PrefsStore
/// preference ("auto" / "light" / "dark") and resolves "auto" against the Windows
/// app-mode registry (<c>AppsUseLightTheme</c>). Applies the result to a window's
/// title-bar chrome via DWM's immersive dark-mode attribute.
///
/// Scope (cluster 9C): chrome only. The MainWindow/pill body colours don't yet
/// switch — that's a follow-up refactor to a DynamicResource-driven token system.
/// WM_SETTINGCHANGE live watching is also deferred — theme is resolved on startup
/// and on each PrefsStore.Changes emission.
/// </summary>
internal static class ThemeApply
{
    public enum Mode { Dark, Light }

    private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
    private const string PersonalizePath = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Themes\Personalize";
    private const string AppsUseLightThemeValue = "AppsUseLightTheme";

    // SetWindowPos flags needed to force a non-client repaint without moving / resizing
    // the window — see learn.microsoft.com Q&A "DWMWA_USE_IMMERSIVE_DARK_MODE won't update".
    private const uint SWP_NOMOVE = 0x0002;
    private const uint SWP_NOSIZE = 0x0001;
    private const uint SWP_NOZORDER = 0x0004;
    private const uint SWP_FRAMECHANGED = 0x0020;

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int dwAttribute, ref int pvAttribute, int cbAttribute);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);

    public static Mode Resolve(string preference) => preference switch
    {
        "light" => Mode.Light,
        "dark" => Mode.Dark,
        _ => ReadOsTheme(),
    };

    public static Mode ReadOsTheme()
    {
        using var key = Registry.CurrentUser.OpenSubKey(PersonalizePath);
        var v = key?.GetValue(AppsUseLightThemeValue);
        return v is int i && i == 1 ? Mode.Light : Mode.Dark;
    }

    public static void ApplyToWindow(IntPtr hwnd, Mode mode)
    {
        int useDark = mode == Mode.Dark ? 1 : 0;
        _ = DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref useDark, sizeof(int));
        // Force a non-client repaint so the title-bar tint switches NOW rather
        // than only on the next focus change.
        _ = SetWindowPos(hwnd, IntPtr.Zero, 0, 0, 0, 0,
            SWP_NOMOVE | SWP_NOSIZE | SWP_NOZORDER | SWP_FRAMECHANGED);
    }
}
