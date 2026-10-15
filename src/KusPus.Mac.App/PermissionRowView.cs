using AppKit;
using Foundation;

namespace KusPus.Mac.App;

/// <summary>
/// One permission's status row — dot + "X granted / not granted yet" + a Grant button —
/// shared by the onboarding permission steps and Preferences → General so the same
/// three TCC grants are reachable from either place (not just on first launch).
/// </summary>
internal sealed class PermissionRowView : FlippedView
{
    private readonly string _name;
    private readonly Func<bool> _check;
    private readonly NSStackView _row;
    private readonly NSTextField _status;
    private readonly NSButton _grant;
    private readonly bool _settingsStyle;
    private readonly NSStackView? _granted;
    private readonly NSStackView? _needed;
    private DotView _dot;
    private bool? _lastState;

    /// <summary>
    /// Settings-window variant: titled row (name + why it's needed) with the state on the
    /// right as icon + text ("Granted") or an "Open Settings…" button. Onboarding keeps the
    /// original status-line layout.
    /// </summary>
    public static PermissionRowView ForSettings(string name, Func<bool> check, Action request, string description) =>
        new(name, check, request, description, settingsStyle: true);

    private PermissionRowView(string name, Func<bool> check, Action request, string description, bool settingsStyle)
    {
        _name = name;
        _check = check;
        _settingsStyle = settingsStyle;
        TranslatesAutoresizingMaskIntoConstraints = false;

        _dot = new DotView("WarningAmber");
        _status = UI.Label(string.Empty, Fonts.Medium(12), "WarningText");
        // Every state is stated, never implied by a button alone: "Granted" (check) or
        // "Not granted" (warning) + the action that fixes it.
        _grant = UI.Button("Grant Access…", UI.ButtonKind.Secondary, request);
        Settings.AxLabel(_grant, $"Grant {name} access in System Settings");
        _granted = Settings.Status(StatusKind.Ok, "Granted");
        _needed = Settings.Status(StatusKind.Warning, "Not granted");
        var right = UI.HStack(12, _granted, _needed, _grant);
        var left = UI.VStack(4,
            UI.Label(name, Fonts.RowTitle, "PrimaryText"),
            UI.Paragraph(description, Fonts.Supporting, "MutedText", lineSpacing: 2));
        _row = UI.Row(left, right);
        AddSubview(_row);
        NSLayoutConstraint.ActivateConstraints(
        [
            _row.LeadingAnchor.ConstraintEqualTo(LeadingAnchor),
            _row.TrailingAnchor.ConstraintEqualTo(TrailingAnchor),
            _row.TopAnchor.ConstraintEqualTo(TopAnchor),
            _row.BottomAnchor.ConstraintEqualTo(BottomAnchor),
        ]);
        Refresh();
    }

    /// <summary>Settings variant: makes this row's button the screen's one primary action.</summary>
    public void SetPrimary(bool primary) => UI.ApplyKind(_grant, primary ? UI.ButtonKind.Primary : UI.ButtonKind.Secondary);

    public PermissionRowView(string name, Func<bool> check, Action request, string? description = null)
    {
        _name = name;
        _check = check;
        TranslatesAutoresizingMaskIntoConstraints = false;

        _dot = new DotView("WarningAmber");
        _status = UI.Label(string.Empty, Fonts.Medium(12), "WarningAmber");
        _grant = UI.Button($"Grant {name}…", UI.ButtonKind.Primary, request);
        var left = description is null
            ? UI.VStack(0, UI.HStack(10, _dot, _status))
            : UI.VStack(3, UI.HStack(10, _dot, _status), UI.RowSubtitle(description));
        _row = UI.Row(left, _grant);
        AddSubview(_row);
        NSLayoutConstraint.ActivateConstraints(
        [
            _row.LeadingAnchor.ConstraintEqualTo(LeadingAnchor),
            _row.TrailingAnchor.ConstraintEqualTo(TrailingAnchor),
            _row.TopAnchor.ConstraintEqualTo(TopAnchor),
            _row.BottomAnchor.ConstraintEqualTo(BottomAnchor),
        ]);
        Refresh();
    }

    public bool IsGranted => _lastState == true;

    /// <summary>Re-reads the grant and updates the row. Returns true when the state flipped to granted.</summary>
    public bool Refresh()
    {
        bool ok = _check();
        bool flipped = _lastState == false && ok;
        if (_lastState == ok)
        {
            return false;
        }
        _lastState = ok;
        if (_settingsStyle)
        {
            _granted!.Hidden = !ok;
            _needed!.Hidden = ok;
            // Always a way back to System Settings: "Manage…" once granted (to review or
            // revoke), "Grant Access…" while missing.
            _grant.Hidden = false;
            _grant.Title = ok ? "Manage…" : "Grant Access…";
            Settings.AxLabel(_grant, ok ? $"Manage {_name} access in System Settings" : $"Grant {_name} access in System Settings");
            AccessibilityLabel = $"{_name}, {(ok ? "granted" : "not granted")}";
            return flipped;
        }
        _status.StringValue = ok ? $"{_name} granted" : $"{_name} not granted yet";
        _status.TextColor = ThemeColors.Get(ok ? "Mint" : "WarningAmber");
        _grant.Hidden = ok;
        var inner = (NSStackView)((NSStackView)_row.ArrangedSubviews[0]).ArrangedSubviews[0];
        inner.RemoveArrangedSubview(_dot);
        _dot.RemoveFromSuperview();
        _dot = new DotView(ok ? "Mint" : "WarningAmber");
        inner.InsertArrangedSubview(_dot, 0);
        return flipped;
    }
}

/// <summary>Quit and reopen the running bundle (needed when a TCC grant only takes effect for a fresh process).</summary>
internal static class MacRelaunch
{
    public static void Relaunch()
    {
        var bundle = NSBundle.MainBundle.BundlePath;
        // Detached shell: wait until this process has actually exited (SingleInstance
        // would otherwise see us still running and bounce the new copy back to the old
        // one), capped at 30 s. A fixed sleep wasn't enough when shutdown (coordinator,
        // whisper, Parakeet unload) took longer. -n forces a fresh instance.
        const string script = "i=0; while kill -0 \"$1\" 2>/dev/null && [ $i -lt 150 ]; do sleep 0.2; i=$((i+1)); done; /usr/bin/open -n \"$2\"";
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("/bin/sh")
        {
            ArgumentList = { "-c", script, "sh", Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture), bundle },
            UseShellExecute = false,
            CreateNoWindow = true,
        });
        NSApplication.SharedApplication.Terminate(null);
    }
}
