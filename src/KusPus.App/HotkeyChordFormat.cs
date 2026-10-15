using KusPus.Core.Hotkeys;
using KusPus.Core.Settings;

namespace KusPus.App;

/// <summary>
/// Renders a <see cref="HotkeySettings"/> chord with Windows modifier glyphs
/// (^ ⌥ ⇧ ⊞). Shared by the tray menu's keycap (compact, no separators,
/// "Spc") and the floating pill's hover hint ("Hold ^ ⊞ to dictate").
/// </summary>
internal static class HotkeyChordFormat
{
    private const string NoHotkeyHint = "Set a hotkey in Preferences";

    /// <summary>
    /// Pill hint line. Empty chord → <c>Set a hotkey in Preferences</c>.
    /// </summary>
    public static string Hint(HotkeySettings hk)
    {
        var glyphs = Glyphs(hk);
        return glyphs.Length == 0 ? NoHotkeyHint : $"Hold {glyphs} to dictate";
    }

    /// <summary>
    /// Space-separated glyph run for the pill (<c>^ ⊞</c>, <c>^ ⇧ Space</c>).
    /// Empty string for an empty chord.
    /// </summary>
    public static string Glyphs(HotkeySettings hk) => string.Join(' ', Parts(hk, compact: false));

    /// <summary>
    /// Tray keycap run — no separators, compact key names (<c>Spc</c>). Empty
    /// chord renders as an em dash so the keycap never collapses to nothing.
    /// </summary>
    public static string Keycap(HotkeySettings hk)
    {
        var parts = Parts(hk, compact: true);
        return parts.Count == 0 ? "—" : string.Concat(parts);
    }

    public static string ModifierGlyph(VirtualKey vk) => vk switch
    {
        VirtualKey.LeftCtrl => "^",
        VirtualKey.RightCtrl => "^",
        VirtualKey.LeftAlt => "⌥",
        VirtualKey.RightAlt => "⌥",
        VirtualKey.LeftShift => "⇧",
        VirtualKey.RightShift => "⇧",
        VirtualKey.LeftWin => "⊞",
        VirtualKey.RightWin => "⊞",
        _ => vk.ToString(),
    };

    /// <summary>
    /// Trigger-key display. <paramref name="compact"/> picks the tray's
    /// three-letter <c>Spc</c>; the pill has room for <c>Space</c>.
    /// </summary>
    public static string KeyDisplay(VirtualKey vk, bool compact) => vk switch
    {
        VirtualKey.Space => compact ? "Spc" : "Space",
        VirtualKey.Return => "↵",
        VirtualKey.Tab => "⇥",
        VirtualKey.Escape => "Esc",
        _ => vk.ToString(),
    };

    private static List<string> Parts(HotkeySettings hk, bool compact)
    {
        var parts = new List<string>();
        foreach (var m in hk.Modifiers)
        {
            parts.Add(ModifierGlyph(m));
        }
        if (hk.KeyCode is { } k)
        {
            parts.Add(KeyDisplay(k, compact));
        }
        return parts;
    }
}
