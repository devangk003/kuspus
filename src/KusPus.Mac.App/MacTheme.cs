using AppKit;

namespace KusPus.Mac.App;

/// <summary>
/// Theme switch for the AppKit app. <c>auto</c> follows the system (appearance
/// <c>null</c>); <c>light</c>/<c>dark</c> pin <c>NSApp.appearance</c>. Views that use
/// <see cref="ThemeColors"/> re-read their colours when their <see cref="ThemedRootView"/>
/// reports an effective-appearance change.
/// </summary>
internal static class MacTheme
{
    public static void Apply(string theme)
    {
        NSApplication.SharedApplication.Appearance = theme switch
        {
            "light" => NSAppearance.GetAppearance(NSAppearance.NameAqua),
            "dark" => NSAppearance.GetAppearance(NSAppearance.NameDarkAqua),
            _ => null,
        };
    }

    /// <summary>True when the app's effective appearance is dark (after <see cref="Apply"/>).</summary>
    public static bool IsDark() => IsDark(NSApplication.SharedApplication.EffectiveAppearance);

    public static bool IsDark(NSAppearance appearance)
    {
        var aqua = NSAppearance.NameAqua.ToString();
        var darkAqua = NSAppearance.NameDarkAqua.ToString();
        var best = appearance.FindBestMatch([aqua, darkAqua]);
        return string.Equals(best, darkAqua, StringComparison.Ordinal);
    }
}
