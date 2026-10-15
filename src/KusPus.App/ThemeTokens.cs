using System.Windows;
using Color = System.Windows.Media.Color;
using ColorConverter = System.Windows.Media.ColorConverter;
using Colors = System.Windows.Media.Colors;
using SolidColorBrush = System.Windows.Media.SolidColorBrush;

namespace KusPus.App;

/// <summary>
/// Theme token registry. Each key maps to a (dark, light) colour pair from
/// docs/APP_DESIGN.md §3.1 / §3.2. <see cref="Register"/> creates one
/// <see cref="SolidColorBrush"/> per token and inserts it into Application.Resources
/// so MainWindow.xaml can reference it via <c>{DynamicResource &lt;key&gt;}</c>.
/// <see cref="Apply"/> flips every brush's <see cref="SolidColorBrush.Color"/> on
/// a theme change — WPF's render system picks up the colour change automatically
/// because <c>SolidColorBrush.Color</c> is a DependencyProperty.
/// </summary>
internal static class ThemeTokens
{
    public static readonly IReadOnlyDictionary<string, (Color Dark, Color Light)> Map =
        new Dictionary<string, (Color, Color)>(StringComparer.Ordinal)
        {
            // App surfaces (§3.1 / §3.2)
            ["AppBg"]            = (FromHex("#202020"), FromHex("#F3F3F3")),
            ["Sidebar"]          = (FromHex("#1B1B1D"), FromHex("#ECECEE")),
            ["TitleBar"]         = (FromHex("#1A1A1C"), FromHex("#EAEAEC")),
            ["Surface"]          = (FromHex("#2A2A2C"), FromHex("#FFFFFF")),
            ["SurfaceElevated"]  = (FromHex("#323234"), FromHex("#FAFAFA")),
            ["SurfaceInput"]     = (FromHex("#2F2F31"), FromHex("#F4F4F6")),

            // Border tokens
            ["BorderSubtle"]     = (FromHex("#14FFFFFF"), FromHex("#0F000000")),
            ["BorderStrong"]     = (FromHex("#22FFFFFF"), FromHex("#22000000")),
            ["BorderDivider"]    = (FromHex("#11FFFFFF"), FromHex("#11000000")),

            // Text
            // Contrast vs the relevant body bg (AppBg #202020 dark, #F3F3F3 light):
            //   PrimaryText   ~15:1   (way above AAA)
            //   SecondaryText ~12:1   (AAA)
            //   MutedText     ~5.3:1  (AA)
            //   DisabledText  ~5.1:1 dark / ~4.7:1 light  (AA — per APP_DESIGN §13.5 P0-3,
            //     raised from previous 3.0:1 / 1.9:1 which failed WCAG normal-text 4.5:1.
            //     Hex alpha encoded MSB-first: 0x80 = 50% white on dark; 0xA0 = 63% black on light.)
            ["PrimaryText"]      = (FromHex("#F5FFFFFF"), FromHex("#E5141414")),
            ["SecondaryText"]    = (FromHex("#C8FFFFFF"), FromHex("#C8141414")),
            ["MutedText"]        = (FromHex("#8CFFFFFF"), FromHex("#80141414")),
            ["DisabledText"]     = (FromHex("#80FFFFFF"), FromHex("#A0141414")),

            // Hover
            ["HoverSubtle"]      = (FromHex("#0DFFFFFF"), FromHex("#08000000")),

            // Keycap (the chord-key chip is more pronounced than a plain row card)
            ["KeycapBg"]         = (FromHex("#323234"), FromHex("#F7F7F8")),
            ["KeycapBorder"]     = (FromHex("#33FFFFFF"), FromHex("#1F000000")),

            // Brand colours stay the same across themes
            ["Mint"]             = (FromHex("#4DDBA6"), FromHex("#4DDBA6")),
            ["MintTint"]         = (FromHex("#1A4DDBA6"), FromHex("#264DDBA6")),
            ["MintBorder"]       = (FromHex("#664DDBA6"), FromHex("#664DDBA6")),

            // Status
            ["ErrorRed"]         = (FromHex("#EF5350"), FromHex("#EF5350")),
            ["WarningAmber"]     = (FromHex("#FFB74D"), FromHex("#FFB74D")),
            ["WarningTint"]      = (FromHex("#332B1F"), FromHex("#FFF4E0")),
            ["WarningBorder"]    = (FromHex("#66FFB74D"), FromHex("#66FFB74D")),

            // Pill-specific tokens — used by FloatingPillWindow. The Wispr-style
            // capsule is ALWAYS dark regardless of the app theme, so every Pill*
            // entry carries the same value in both columns. The surface gradient
            // is installed separately by BuildPillSurfaceGradient because it's a
            // LinearGradientBrush. Visualizer bars use the Mint token directly.
            ["PillBorder"]            = (FromHex("#1AFFFFFF"), FromHex("#1AFFFFFF")),
            ["PillText"]              = (FromHex("#F0FFFFFF"), FromHex("#F0FFFFFF")),
            ["PillTextMuted"]         = (FromHex("#99FFFFFF"), FromHex("#99FFFFFF")),
            ["PillIcon"]              = (FromHex("#C8FFFFFF"), FromHex("#C8FFFFFF")),
            ["PillHoverBg"]           = (FromHex("#22FFFFFF"), FromHex("#22FFFFFF")),
            ["PillHoverBgStrong"]     = (FromHex("#33FFFFFF"), FromHex("#33FFFFFF")),
            ["PillDockMicBg"]         = (FromHex("#14FFFFFF"), FromHex("#14FFFFFF")),

            // Audio-tab meter (9E UX refresh) — Discord-style track + fill.
            ["MeterTrack"]            = (FromHex("#1AFFFFFF"), FromHex("#1A000000")),

            // Generic hover surface. White-tinted in dark, black-tinted in light.
            // (The pill no longer uses it — see PillHoverBg above.)
            ["ButtonHoverBg"]         = (FromHex("#22FFFFFF"), FromHex("#22000000")),
        };

    /// <summary>
    /// Installs (or replaces) one brush per token for the given theme mode.
    /// Replacement — not mutation — because WPF freezes Freezable resources added
    /// to <c>Application.Resources</c> (the <c>x:Shared</c> semantics) and a frozen
    /// brush can't have its <see cref="SolidColorBrush.Color"/> mutated. Replacing
    /// the value fires <see cref="ResourceDictionary.ResourcesChanged"/>; every
    /// <c>{DynamicResource}</c> consumer re-resolves the new brush automatically.
    /// </summary>
    public static void Apply(ResourceDictionary appResources, ThemeApply.Mode mode)
    {
        foreach (var (key, pair) in Map)
        {
            var color = mode == ThemeApply.Mode.Dark ? pair.Dark : pair.Light;
            appResources[key] = new SolidColorBrush(color);
        }
        appResources["PillSurface"] = BuildPillSurfaceGradient();
    }

    /// <summary>
    /// Builds the pill's two-stop vertical gradient. LinearGradientBrush isn't in
    /// <see cref="Map"/> because it's compound (two stops) and doesn't fit the
    /// single-Color-pair shape. Theme-invariant: the capsule is always dark
    /// (near-opaque charcoal) so mint bars and light text read identically in
    /// the light and dark app themes.
    /// </summary>
    private static System.Windows.Media.LinearGradientBrush BuildPillSurfaceGradient()
    {
        return new System.Windows.Media.LinearGradientBrush(
            new System.Windows.Media.GradientStopCollection
            {
                new System.Windows.Media.GradientStop(FromHex("#F0202022"), 0.0),
                new System.Windows.Media.GradientStop(FromHex("#F5161618"), 1.0),
            },
            new System.Windows.Point(0, 0),
            new System.Windows.Point(0, 1));
    }

    private static Color FromHex(string hex)
    {
        var c = (Color?)ColorConverter.ConvertFromString(hex);
        return c ?? Colors.Magenta;
    }
}
