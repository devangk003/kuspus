#pragma warning disable CA1848
#pragma warning disable CA1873

using AppKit;
using Foundation;
using KusPus.Core.Hotkeys;
using KusPus.Core.Platform;
using KusPus.Core.Settings;
using KusPus.Mac.Platform;
using KusPus.Persistence;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace KusPus.Mac.App;

/// <summary>
/// The hotkey card + listen-mode state machine that WPF duplicates between
/// MainWindow and OnboardingWindow, extracted once. Click the card → the global tap
/// is suspended (empty chord), a local <c>NSEvent</c> monitor swallows key events,
/// the largest chord seen before all keys are released is saved to settings, and a
/// conflict row appears for known system shortcuts.
///
/// Modifier keys arrive as <c>FlagsChanged</c>; direction comes from the
/// device-dependent flag bit (same trick as <see cref="MacHotkeyEngine"/>).
/// </summary>
internal sealed class HotkeyPickerView : FlippedView
{
    private readonly IPrefsStore _prefs;
    private readonly IHotkeyEngine _hotkey;
    private readonly ILogger _logger;
    private readonly bool _hero;
    private readonly bool _compact;
    private readonly NSButton? _changeButton;
    private const string CompactHint = "Hold it anywhere to dictate, release to paste.";

    private readonly CardView _card;
    private readonly NSTextField _eyebrow;
    private readonly NSStackView _keycaps;
    private readonly NSTextField _hint;
    private readonly CardView _conflictRow;
    private readonly NSTextField _conflictText;
    private readonly NSView _listenBorder;

    private bool _isListening;
    private HotkeyChord? _savedChord;
    private NSObject? _monitor;
    private NSObject? _resignObserver;
    private readonly List<VirtualKey> _pressedOrder = new();
    private readonly HashSet<VirtualKey> _heldKeys = new();
    private IReadOnlyList<VirtualKey> _bestModifiers = Array.Empty<VirtualKey>();
    private VirtualKey? _bestKey;

    public HotkeyPickerView(IPrefsStore prefs, IHotkeyEngine hotkey, ILogger? logger, string description, bool hero, bool compact = false)
    {
        _prefs = prefs;
        _hotkey = hotkey;
        _logger = logger ?? NullLogger.Instance;
        _hero = hero;
        _compact = compact;
        TranslatesAutoresizingMaskIntoConstraints = false;

        if (compact)
        {
            // Settings variant: a plain row (title + hint | keycaps + Change…). Keyboard
            // reachable through the button; the listen state shows in the hint + button title.
            _eyebrow = UI.Eyebrow(string.Empty);
            _keycaps = UI.HStack(4);
            _hint = UI.Label(CompactHint, Fonts.Supporting, "MutedText", wraps: true);
            _changeButton = UI.Button("Change…", UI.ButtonKind.Secondary, () =>
            {
                if (_isListening)
                {
                    CancelListenMode();
                }
                else
                {
                    EnterListenMode();
                }
            });
            var titleCol = UI.VStack(4, UI.Label("Shortcut", Fonts.RowTitle, "PrimaryText"), _hint);
            var line = UI.Row(titleCol, UI.HStack(12, _keycaps, _changeButton));
            _conflictText = UI.Label(string.Empty, Fonts.Supporting, "PrimaryText", wraps: true);
            var conflictLine = UI.VStack(4, Settings.Status(StatusKind.Warning, "Shortcut conflict"), _conflictText);
            _conflictRow = new CardView(conflictLine, "WarningTint", "WarningBorder", radius: 6, padX: 12, padY: 8) { Hidden = true };
            _card = new CardView(new NSView());
            _listenBorder = new NSView { Hidden = true };
            var col = UI.VStack(8, line, _conflictRow);
            line.WidthAnchor.ConstraintEqualTo(col.WidthAnchor).Active = true;
            _conflictRow.WidthAnchor.ConstraintEqualTo(col.WidthAnchor).Active = true;
            AddSubview(col);
            NSLayoutConstraint.ActivateConstraints(
            [
                col.LeadingAnchor.ConstraintEqualTo(LeadingAnchor),
                col.TrailingAnchor.ConstraintEqualTo(TrailingAnchor),
                col.TopAnchor.ConstraintEqualTo(TopAnchor),
                col.BottomAnchor.ConstraintEqualTo(BottomAnchor),
            ]);
            ApplyHotkeyDisplay(_prefs.Current.Hotkey);
            return;
        }

        _eyebrow = UI.Eyebrow("HOTKEY");
        _keycaps = UI.HStack(0);
        var desc = UI.RowSubtitle(description);
        _hint = UI.Hint("Click the picker, then press a new chord.");
        var inner = UI.VStack(0, _eyebrow, _keycaps, desc, _hint);
        inner.SetCustomSpacing(10, _eyebrow);
        inner.SetCustomSpacing(14, _keycaps);
        inner.SetCustomSpacing(6, desc);
        _card = new CardView(inner, radius: hero ? 10 : 8, padX: hero ? 24 : 20, padY: hero ? 22 : 16);

        // Dashed mint listen border overlaid on the card.
        _listenBorder = new NSView { WantsLayer = true, Hidden = true, TranslatesAutoresizingMaskIntoConstraints = false };
        var dash = new CoreAnimation.CAShapeLayer
        {
            FillColor = NSColor.Clear.CGColor,
            StrokeColor = ThemeColors.Cg("Mint"),
            LineWidth = (nfloat)1.5,
            LineDashPattern = [NSNumber.FromInt32(4), NSNumber.FromInt32(3)],
        };
        _listenBorder.Layer!.AddSublayer(dash);

        _conflictText = UI.Label(string.Empty, Fonts.BodySmall, "PrimaryText", wraps: true);
        var conflictInner = UI.HStack(8,
            new DotView("WarningAmber"),
            UI.Label("Shortcut conflict.", Fonts.WarningEmphasis, "WarningAmber"),
            _conflictText);
        _conflictRow = new CardView(conflictInner, "WarningTint", "WarningBorder", radius: 6, padX: 14, padY: 10) { Hidden = true };

        var column = UI.VStack(1, _card, _conflictRow);
        AddSubview(column);
        AddSubview(_listenBorder);
        NSLayoutConstraint.ActivateConstraints(
        [
            column.LeadingAnchor.ConstraintEqualTo(LeadingAnchor),
            column.TrailingAnchor.ConstraintEqualTo(TrailingAnchor),
            column.TopAnchor.ConstraintEqualTo(TopAnchor),
            column.BottomAnchor.ConstraintEqualTo(BottomAnchor),
            _card.WidthAnchor.ConstraintEqualTo(WidthAnchor),
            _conflictRow.WidthAnchor.ConstraintEqualTo(WidthAnchor),
            _listenBorder.LeadingAnchor.ConstraintEqualTo(_card.LeadingAnchor),
            _listenBorder.TrailingAnchor.ConstraintEqualTo(_card.TrailingAnchor),
            _listenBorder.TopAnchor.ConstraintEqualTo(_card.TopAnchor),
            _listenBorder.BottomAnchor.ConstraintEqualTo(_card.BottomAnchor),
        ]);

        var click = new NSClickGestureRecognizer(() => EnterListenMode());
        _card.AddGestureRecognizer(click);

        ApplyHotkeyDisplay(_prefs.Current.Hotkey);
    }

    /// <summary>Raised after a new chord is saved (so owners can refresh sidebar glyphs etc.).</summary>
    public event Action<HotkeySettings>? Rebound;

    public bool IsListening => _isListening;

    public override void Layout()
    {
        base.Layout();
        if (_listenBorder.Layer?.Sublayers?.FirstOrDefault() is CoreAnimation.CAShapeLayer dash)
        {
            var b = _listenBorder.Bounds;
            var r = new CoreGraphics.CGRect(b.X + 1, b.Y + 1, b.Width - 2, b.Height - 2);
            dash.Frame = _listenBorder.Bounds;
            dash.Path = CoreGraphics.CGPath.FromRoundedRect(r, (nfloat)(_hero ? 10 : 8), (nfloat)(_hero ? 10 : 8));
        }
    }

    public void Retheme()
    {
        _card.Retheme();
        _conflictRow.Retheme();
        ApplyHotkeyDisplay(_prefs.Current.Hotkey);
        if (_listenBorder.Layer?.Sublayers?.FirstOrDefault() is CoreAnimation.CAShapeLayer dash)
        {
            dash.StrokeColor = ThemeColors.Cg("Mint");
        }
    }

    /// <summary>Cancels an in-flight rebind (window closing / step change).</summary>
    public void CancelListenMode()
    {
        if (!_isListening)
        {
            return;
        }
        ExitListenModeVisuals();
        if (_savedChord is { } restore)
        {
            _hotkey.SetChord(restore);
        }
        _savedChord = null;
        ApplyHotkeyDisplay(_prefs.Current.Hotkey);
    }

    // ── display ──────────────────────────────────────────────────────────────

    public void ApplyHotkeyDisplay(HotkeySettings hk)
    {
        var keys = new List<VirtualKey>(hk.Modifiers);
        if (hk.KeyCode is { } k)
        {
            keys.Add(k);
        }
        RenderKeycaps(keys);
    }

    private void RenderKeycaps(IReadOnlyList<VirtualKey> keys)
    {
        foreach (var v in _keycaps.ArrangedSubviews.ToArray())
        {
            _keycaps.RemoveArrangedSubview(v);
            v.RemoveFromSuperview();
        }
        _keycaps.AccessibilityElement = true;
        _keycaps.AccessibilityRole = NSAccessibilityRoles.StaticTextRole;
        _keycaps.AccessibilityLabel = keys.Count == 0
            ? "No shortcut set"
            : "Current shortcut: " + string.Join(" plus ", keys.Select(SpokenKey));
        if (keys.Count == 0)
        {
            _keycaps.AddArrangedSubview(_compact ? UI.Keycap("…", 8, 4) : UI.Keycap("…"));
            return;
        }
        if (_compact)
        {
            for (int i = 0; i < keys.Count; i++)
            {
                if (i > 0)
                {
                    _keycaps.AddArrangedSubview(UI.Label("+", Fonts.Supporting, "MutedText"));
                }
                _keycaps.AddArrangedSubview(UI.Keycap(FriendlyKey(keys[i]), 8, 4));
            }
            return;
        }
        for (int i = 0; i < keys.Count; i++)
        {
            if (i > 0)
            {
                var plus = UI.Label("+", Fonts.Medium(13), "MutedText");
                _keycaps.AddArrangedSubview(plus);
                _keycaps.SetCustomSpacing(10, _keycaps.ArrangedSubviews[^2]);
                _keycaps.SetCustomSpacing(10, plus);
            }
            _keycaps.AddArrangedSubview(UI.Keycap(FriendlyKey(keys[i])));
        }
    }

    /// <summary>Key name as VoiceOver should say it ("Left Control"), not the glyph.</summary>
    private static string SpokenKey(VirtualKey k) => k switch
    {
        VirtualKey.LeftCtrl => "Left Control",
        VirtualKey.RightCtrl => "Right Control",
        VirtualKey.LeftWin => "Left Command",
        VirtualKey.RightWin => "Right Command",
        VirtualKey.LeftAlt => "Left Option",
        VirtualKey.RightAlt => "Right Option",
        VirtualKey.LeftShift => "Left Shift",
        VirtualKey.RightShift => "Right Shift",
        _ => FriendlyKey(k),
    };

    /// <summary>Mac-flavoured key names (⌃ ⌥ ⇧ ⌘), left/right annotated like the Windows picker.</summary>
    public static string FriendlyKey(VirtualKey k) => k switch
    {
        VirtualKey.LeftCtrl => "L⌃",
        VirtualKey.RightCtrl => "R⌃",
        VirtualKey.LeftWin => "L⌘",
        VirtualKey.RightWin => "R⌘",
        VirtualKey.LeftAlt => "L⌥",
        VirtualKey.RightAlt => "R⌥",
        VirtualKey.LeftShift => "L⇧",
        VirtualKey.RightShift => "R⇧",
        VirtualKey.Space => "Space",
        VirtualKey.Return => "Return",
        VirtualKey.Tab => "Tab",
        VirtualKey.Escape => "Esc",
        VirtualKey.Backspace => "Delete",
        _ => HotkeyGlyphs.Glyph(k),
    };

    // ── listen mode ──────────────────────────────────────────────────────────

    private void EnterListenMode()
    {
        if (_isListening)
        {
            return;
        }
        var current = _prefs.Current.Hotkey;
        _savedChord = new HotkeyChord(current.Modifiers, current.KeyCode);
        _hotkey.SetChord(new HotkeyChord(Array.Empty<VirtualKey>(), null));

        _isListening = true;
        _pressedOrder.Clear();
        _heldKeys.Clear();
        _bestModifiers = Array.Empty<VirtualKey>();
        _bestKey = null;

        _listenBorder.Hidden = _compact;
        _eyebrow.TextColor = ThemeColors.Get("Mint");
        _hint.TextColor = ThemeColors.Get(_compact ? "MintText" : "SecondaryText");
        _hint.StringValue = _compact ? "Press the new shortcut now. Esc cancels." : "Now press the keys you want to use… Esc to cancel";
        if (_changeButton is not null)
        {
            _changeButton.Title = "Cancel";
        }
        _conflictRow.Hidden = true;
        RenderKeycaps(Array.Empty<VirtualKey>());

        _monitor = NSEvent.AddLocalMonitorForEventsMatchingMask(
            NSEventMask.KeyDown | NSEventMask.KeyUp | NSEventMask.FlagsChanged, OnLocalEvent);
        // The local monitor only sees our own key events. If the user switches apps while
        // listening, the global hotkey would stay disabled until they came back, and a
        // key released elsewhere would leave _heldKeys stuck — so bail out instead.
        _resignObserver = NSApplication.Notifications.ObserveDidResignActive((_, _) => CancelListenMode());
        _logger.LogDebug("Hotkey listen mode entered; event tap suspended.");
    }

    // LocalEventHandler is declared non-nullable in the binding; returning null is the
    // documented way to swallow the event, hence the null-forgiving returns.
    private NSEvent OnLocalEvent(NSEvent e)
    {
        if (!_isListening)
        {
            return e;
        }
        if (e.Type == NSEventType.KeyDown && e.KeyCode == MacKeyCodes.Escape)
        {
            CancelListenMode();
            return null!;
        }

        var vk = MacKeyCodes.ToVirtualKey(e.KeyCode);
        if (vk == VirtualKey.None)
        {
            return null!;
        }

        bool isDown;
        switch (e.Type)
        {
            case NSEventType.KeyDown:
                if (e.IsARepeat)
                {
                    return null!;
                }
                isDown = true;
                break;
            case NSEventType.KeyUp:
                isDown = false;
                break;
            case NSEventType.FlagsChanged:
                if (!MacKeyCodes.TryGetModifierFlagBit(e.KeyCode, out var bit))
                {
                    return null!;
                }
                isDown = ((ulong)e.ModifierFlags & bit) != 0;
                break;
            default:
                return e;
        }

        if (isDown)
        {
            if (_heldKeys.Add(vk))
            {
                _pressedOrder.Add(vk);
                SnapshotBest();
                RenderKeycaps(BuildPreview());
            }
        }
        else
        {
            if (!_heldKeys.Remove(vk))
            {
                // Key-up for a key pressed before listening began (e.g. the Space that
                // activated "Change…"): not part of the new chord, and must not commit
                // an empty capture.
                return null!;
            }
            // A released key is no longer part of what's held: without this, ⌃⇧ → release ⇧
            // → A would record ⌃⇧A, a chord that was never held together.
            _pressedOrder.Remove(vk);
            if (_heldKeys.Count == 0)
            {
                _ = CommitListenModeAsync();
            }
        }
        return null!; // swallowed
    }

    private async Task CommitListenModeAsync()
    {
        var mods = _bestModifiers;
        var key = _bestKey;
        var saved = _savedChord;
        ExitListenModeVisuals();

        if (mods.Count == 0 && key is null)
        {
            // Nothing captured (e.g. the key-up of a key held before listening started):
            // put the engine's chord back, otherwise the global hotkey stays disabled.
            if (saved is not null)
            {
                _hotkey.SetChord(saved);
            }
            _savedChord = null;
            ApplyHotkeyDisplay(_prefs.Current.Hotkey);
            return;
        }

        var current = _prefs.Current;
        var next = current with
        {
            Hotkey = new HotkeySettings
            {
                Modifiers = mods.ToArray(),
                KeyCode = key,
                HoldThresholdMs = current.Hotkey.HoldThresholdMs,
            },
        };
        _logger.LogInformation("Hotkey rebound → mods=[{Mods}] key={Key}.", string.Join(",", mods), key);
        try
        {
            // PrefsStore.Changes → AppCoordinator.OnSettingsChanged → IHotkeyEngine.SetChord.
            await _prefs.UpdateAsync(s => s with
            {
                Hotkey = next.Hotkey with { HoldThresholdMs = s.Hotkey.HoldThresholdMs },
            }).ConfigureAwait(true);
            _savedChord = null;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "PrefsStore.SaveAsync failed for Hotkey rebind.");
            if (saved is not null)
            {
                _hotkey.SetChord(saved); // the save didn't reach the engine — restore the old chord
            }
            _savedChord = null;
            ApplyHotkeyDisplay(_prefs.Current.Hotkey);
            return;
        }
        ApplyHotkeyDisplay(next.Hotkey);
        var conflict = MacShortcutConflicts.Detect(mods, key);
        _conflictRow.Hidden = conflict is null;
        _conflictText.StringValue = conflict ?? string.Empty;
        Rebound?.Invoke(next.Hotkey);
    }

    private void ExitListenModeVisuals()
    {
        _isListening = false;
        if (_monitor is not null)
        {
            NSEvent.RemoveMonitor(_monitor);
            _monitor = null;
        }
        _resignObserver?.Dispose();
        _resignObserver = null;
        _heldKeys.Clear();
        _pressedOrder.Clear();
        _listenBorder.Hidden = true;
        _eyebrow.TextColor = ThemeColors.Get("MutedText");
        _hint.StringValue = _compact ? CompactHint : "Click the picker, then press a new chord.";
        _hint.TextColor = ThemeColors.Get(_compact ? "MutedText" : "DisabledText");
        if (_changeButton is not null)
        {
            _changeButton.Title = "Change…";
        }
    }

    private void SnapshotBest()
    {
        var mods = new List<VirtualKey>();
        VirtualKey? chordKey = null;
        foreach (var k in _pressedOrder)
        {
            if (IsModifier(k))
            {
                if (!mods.Contains(k))
                {
                    mods.Add(k);
                }
            }
            else
            {
                chordKey ??= k;
            }
        }
        // A chord with a non-modifier key beats a modifier-only one; otherwise the
        // combination with more modifiers wins.
        bool better = chordKey is not null && _bestKey is null
            || (chordKey is not null) == (_bestKey is not null) && mods.Count >= _bestModifiers.Count;
        if (better)
        {
            _bestModifiers = mods;
            _bestKey = chordKey;
        }
    }

    private List<VirtualKey> BuildPreview()
    {
        var keys = new List<VirtualKey>(_bestModifiers);
        if (_bestKey is { } k)
        {
            keys.Add(k);
        }
        return keys;
    }

    private static bool IsModifier(VirtualKey k) => k is
        VirtualKey.Shift or VirtualKey.Control or VirtualKey.Alt
        or VirtualKey.LeftShift or VirtualKey.RightShift
        or VirtualKey.LeftCtrl or VirtualKey.RightCtrl
        or VirtualKey.LeftAlt or VirtualKey.RightAlt
        or VirtualKey.LeftWin or VirtualKey.RightWin
        or VirtualKey.CapsLock;
}
