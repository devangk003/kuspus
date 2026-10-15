using AppKit;
using CoreGraphics;

namespace KusPus.Mac.App;

/// <summary>
/// The WPF <c>ThemeTokens.Map</c> table, verbatim (same hex pairs, same keys), plus
/// the two compound brushes (pill surface gradient, visualizer bar fill). Colours are
/// resolved against the app's <em>effective</em> appearance, so a theme flip only
/// needs the owning view to re-read them — <see cref="ThemedRootView"/> fires
/// <see cref="ThemedRootView.AppearanceChanged"/> for exactly that.
/// </summary>
internal static class ThemeColors
{
    // key → (dark, light), ARGB hex exactly as in src/KusPus.App/ThemeTokens.cs
    private static readonly Dictionary<string, (uint Dark, uint Light)> Map = new(StringComparer.Ordinal)
    {
        ["AppBg"] = (0xFF202020, 0xFFF3F3F3),
        ["Sidebar"] = (0xFF1B1B1D, 0xFFECECEE),
        ["TitleBar"] = (0xFF1A1A1C, 0xFFEAEAEC),
        ["Surface"] = (0xFF2A2A2C, 0xFFFFFFFF),
        ["SurfaceElevated"] = (0xFF323234, 0xFFFAFAFA),
        ["SurfaceInput"] = (0xFF2F2F31, 0xFFF4F4F6),
        ["BorderSubtle"] = (0x14FFFFFF, 0x0F000000),
        ["BorderStrong"] = (0x22FFFFFF, 0x22000000),
        ["BorderDivider"] = (0x11FFFFFF, 0x11000000),
        ["PrimaryText"] = (0xF5FFFFFF, 0xE5141414),
        ["SecondaryText"] = (0xC8FFFFFF, 0xC8141414),
        // Light values raised to ≥4.5:1 on white / AppBg (were 3.4:1); Disabled below Muted.
        ["MutedText"] = (0x8CFFFFFF, 0xA6141414),
        ["DisabledText"] = (0x66FFFFFF, 0x66141414),
        ["HoverSubtle"] = (0x0DFFFFFF, 0x08000000),
        ["KeycapBg"] = (0xFF323234, 0xFFF7F7F8),
        ["KeycapBorder"] = (0x33FFFFFF, 0x1F000000),
        ["Mint"] = (0xFF4DDBA6, 0xFF4DDBA6),
        ["MintTint"] = (0x1A4DDBA6, 0x264DDBA6),
        ["MintBorder"] = (0x664DDBA6, 0x664DDBA6),
        ["ErrorRed"] = (0xFFEF5350, 0xFFEF5350),
        ["WarningAmber"] = (0xFFFFB74D, 0xFFFFB74D),
        ["WarningTint"] = (0xFF332B1F, 0xFFFFF4E0),
        ["WarningBorder"] = (0x66FFB74D, 0x66FFB74D),
        ["PillBorder"] = (0x12FFFFFF, 0x0F000000),
        ["PillInnerHighlight"] = (0x0DFFFFFF, 0xB3FFFFFF),
        ["VisualizerBarIdle"] = (0x47FFFFFF, 0x40141414),
        ["MeterTrack"] = (0x1AFFFFFF, 0x1A000000),
        ["ButtonHoverBg"] = (0x22FFFFFF, 0x22000000),

        // Text-safe accents: the brand mint / amber / red are fill colours and fail contrast
        // as text on light surfaces, so text and small icons use these (≥4.5:1 both themes).
        ["MintText"] = (0xFF4DDBA6, 0xFF0F7A55),
        ["WarningText"] = (0xFFFFB74D, 0xFF975A00),
        ["ErrorText"] = (0xFFFF6B68, 0xFFC62828),
        ["Separator"] = (0x1AFFFFFF, 0x14000000),
        ["RatingEmpty"] = (0x33FFFFFF, 0x26000000),
    };

    /// <summary>Dark text used on mint-filled surfaces (ACTIVE badge, primary button, toggle knob).</summary>
    public static readonly NSColor OnMint = FromArgb(0xFF0F1F18);

    /// <summary>Pill error accent (#FF4D4F) — the pill uses this, not ErrorRed, per PILL_DESIGN §2.6.</summary>
    public static readonly NSColor PillErrorAccent = FromArgb(0xFFFF4D4F);

    public static NSColor Get(string key) => Get(key, MacTheme.IsDark());

    public static NSColor Get(string key, bool dark)
    {
        if (!Map.TryGetValue(key, out var pair))
        {
            return NSColor.Magenta;
        }
        return FromArgb(dark ? pair.Dark : pair.Light);
    }

    public static CGColor Cg(string key) => Get(key).CGColor;

    // ── notch island (theme-invariant: the island is always black over the notch) ──

    /// <summary>Island fill — must match the hardware notch exactly.</summary>
    public static readonly NSColor IslandBlack = FromArgb(0xFF000000);

    /// <summary>Tile behind the brand mark in the island's left wing — a shade above black so it reads as a tile.</summary>
    public static readonly NSColor IslandTile = FromArgb(0xFF1E1E20);

    /// <summary>Text on the island (light in both themes).</summary>
    public static readonly NSColor IslandText = FromArgb(0xF5FFFFFF);

    /// <summary>Secondary text / glyphs on the island.</summary>
    public static readonly NSColor IslandTextMuted = FromArgb(0xC8FFFFFF);

    /// <summary>Hover fill for island buttons.</summary>
    public static readonly NSColor IslandHoverBg = FromArgb(0x22FFFFFF);

    /// <summary>Resting fill of the island's chips (Record pill, mic chooser, keycap).</summary>
    public static readonly NSColor IslandMicBg = FromArgb(0x14FFFFFF);

    /// <summary>Pressed (pinned) fill of the Pin toggle — a mint tint on black.</summary>
    public static readonly NSColor IslandPinPressed = FromArgb(0x334DDBA6);

    /// <summary>Hairline around the hotkey keycap chip.</summary>
    public static readonly NSColor IslandKeycapBorder = FromArgb(0x1AFFFFFF);

    public static NSColor FromArgb(uint argb)
    {
        byte a = (byte)(argb >> 24);
        byte r = (byte)(argb >> 16);
        byte g = (byte)(argb >> 8);
        byte b = (byte)argb;
        return NSColor.FromRgba(r, g, b, a);
    }

    public static NSColor Rgba(byte r, byte g, byte b, byte a = 0xFF) => NSColor.FromRgba(r, g, b, a);
}

/// <summary>
/// Typography roles from <c>Styles/Typography.xaml</c>, mapped onto the system font
/// (SF Pro replaces Segoe UI Variable; SF Mono replaces Cascadia Mono).
/// </summary>
internal static class Fonts
{
    public static NSFont Sans(double size, nfloat? weight = null) =>
        NSFont.SystemFontOfSize((nfloat)size, weight ?? NSFontWeight.Regular)!;

    public static NSFont Medium(double size) => Sans(size, NSFontWeight.Medium);
    public static NSFont Semibold(double size) => Sans(size, NSFontWeight.Semibold);
    public static NSFont Mono(double size, nfloat? weight = null) =>
        NSFont.MonospacedSystemFont((nfloat)size, weight ?? NSFontWeight.Medium)!;

    /// <summary>System font with tabular (fixed-width) digits — sizes, percentages, counts line up.</summary>
    public static NSFont Numeric(double size, nfloat? weight = null) =>
        NSFont.MonospacedDigitSystemFontOfSize((nfloat)size, weight ?? NSFontWeight.Regular)!;

    // Preference-page scale (4 steps): Title 15 · Body / row title 13 · Supporting 12 · Caption 11.
    // Rows: title = RowTitle (Medium 13), body = Body (13), secondary = Supporting (12).
    // Hierarchy comes from size + weight at each step, not from extra sizes in between.
    public static NSFont Title => Semibold(15);
    public static NSFont CardTitle => Semibold(13);
    public static NSFont Supporting => Sans(12);
    public static NSFont SupportingNumeric => Numeric(12);
    public static NSFont Caption => Medium(11);

    // Roles (size / weight) — see Typography.xaml header table.
    public static NSFont Display => Semibold(32);
    public static NSFont Headline22 => Semibold(22);
    public static NSFont MintHeadline => Semibold(14);
    public static NSFont SectionHeader => Title;
    public static NSFont RowTitle => Medium(13);
    public static NSFont Body => Sans(13);
    public static NSFont WarningEmphasis => Semibold(12);
    public static NSFont Footnote => Medium(12);
    public static NSFont BodySmall => Sans(12);
    public static NSFont ErrorInline => Medium(11.5);
    public static NSFont RowSubtitle => Sans(11.5);
    public static NSFont MonoSm => Mono(11);
    public static NSFont Hint => Sans(11);
    public static NSFont SidebarStatus => Sans(11);
    public static NSFont BadgeMint => Medium(10.5);
    public static NSFont Eyebrow => Semibold(10.5);
}
