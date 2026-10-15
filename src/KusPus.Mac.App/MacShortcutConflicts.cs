using KusPus.Core.Hotkeys;

namespace KusPus.Mac.App;

/// <summary>
/// macOS counterpart of <c>MainWindow.DetectConflict</c>: warns when a chosen chord
/// collides with a system shortcut. ⌘ is the Windows <c>LeftWin</c>/<c>RightWin</c>
/// VK; ⌥ is Alt; ⌃ is Ctrl.
/// </summary>
internal static class MacShortcutConflicts
{
    public static string? Detect(IReadOnlyList<VirtualKey> mods, VirtualKey? key)
    {
        bool cmd = mods.Contains(VirtualKey.LeftWin) || mods.Contains(VirtualKey.RightWin);
        bool ctrl = mods.Contains(VirtualKey.LeftCtrl) || mods.Contains(VirtualKey.RightCtrl);
        bool opt = mods.Contains(VirtualKey.LeftAlt) || mods.Contains(VirtualKey.RightAlt);
        bool shift = mods.Contains(VirtualKey.LeftShift) || mods.Contains(VirtualKey.RightShift);

        if (mods.Count == 1 && key is null && cmd)
        {
            return "⌘ on its own is used by many apps for menu shortcuts. Add a second modifier or a key.";
        }
        if (cmd && !ctrl && !opt && !shift && key is { } k1)
        {
            return k1 switch
            {
                VirtualKey.Space => "⌘ Space opens Spotlight.",
                VirtualKey.Tab => "⌘ Tab switches apps.",
                VirtualKey.Q => "⌘ Q quits the active app.",
                VirtualKey.W => "⌘ W closes the active window.",
                VirtualKey.H => "⌘ H hides the active app.",
                VirtualKey.M => "⌘ M minimises the active window.",
                VirtualKey.C or VirtualKey.V or VirtualKey.X or VirtualKey.Z or VirtualKey.A or VirtualKey.S
                    => $"⌘ {UI.ShortModelId(k1.ToString())} is a standard editing shortcut in every app.",
                _ => null,
            };
        }
        if (ctrl && !cmd && !opt && !shift && key == VirtualKey.Space)
        {
            return "⌃ Space switches the input source.";
        }
        if (ctrl && cmd && !opt && !shift && key == VirtualKey.Space)
        {
            return "⌃ ⌘ Space opens the emoji picker.";
        }
        if (cmd && shift && !ctrl && !opt && key is VirtualKey.D3 or VirtualKey.D4 or VirtualKey.D5)
        {
            return "⌘ ⇧ 3/4/5 take screenshots.";
        }
        if (cmd && opt && !ctrl && !shift && key == VirtualKey.Escape)
        {
            return "⌘ ⌥ Esc opens Force Quit.";
        }
        if (cmd && ctrl && !opt && !shift && key == VirtualKey.Q)
        {
            return "⌃ ⌘ Q locks the screen.";
        }
        if (cmd && shift && !ctrl && !opt && key == VirtualKey.Q)
        {
            return "⌘ ⇧ Q logs you out.";
        }
        return null;
    }
}
