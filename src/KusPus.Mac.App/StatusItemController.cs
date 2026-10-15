using AppKit;
using Foundation;
using KusPus.Coordination;
using KusPus.Core.Hotkeys;
using KusPus.Core.Settings;
using KusPus.Core.State;
using KusPus.Persistence;
using KusPus.Whisper;

namespace KusPus.Mac.App;

/// <summary>
/// Menu-bar status item — the macOS counterpart of <c>TrayManager</c> + <c>TrayMenuWindow</c>.
/// The five-bar brand mark drawn procedurally (an SVG-backed NSImage is not honoured as a
/// template on the full-screen menu bar and paints black). Idle: a template image, so it
/// follows the menu bar's tint. Recording / transcribing: the bars in the label colour
/// with a red / mint dot badge at the top-right — the same cue as the island's tile.
/// A red <c>exclamationmark.triangle</c> replaces it after a failed paste.
///
/// Menu mirrors the Windows one: header + "Version · state", Toggle Recording with the
/// hotkey glyphs, Active model → Models tab, Preferences…, History…, Quit.
/// </summary>
internal sealed class StatusItemController : IDisposable
{
    private readonly IPrefsStore _prefs;
    private readonly IModelManager _models;
    private readonly AppCoordinator _coordinator;
    private readonly Action _onToggleRecorder;
    private readonly Action<string> _onOpenTab;
    private readonly Action _onQuit;

    private readonly NSStatusItem _item;
    private readonly NSMenuItem _stateItem;
    private readonly NSMenuItem _toggleItem;
    private readonly NSMenuItem _modelItem;
    private readonly IDisposable _stateSub;
    private readonly IDisposable _prefsSub;

    private readonly NSImage _iconMark;
    private readonly NSImage _iconRecording;
    private readonly NSImage _iconTranscribing;
    private readonly NSImage _iconError;
    // The post-paste snapshot stays current until the next dictation; clear the error
    // glyph after the same hold the island uses instead.
    private const double ErrorHoldSec = 3.0;
    private CoordinatorSnapshot? _expiredError;
    private CoordinatorSnapshot? _timedError;
    private NSTimer? _errorTimer;

    public StatusItemController(
        IPrefsStore prefs,
        IModelManager models,
        AppCoordinator coordinator,
        Action onToggleRecorder,
        Action<string> onOpenTab,
        Action onQuit)
    {
        _prefs = prefs;
        _models = models;
        _coordinator = coordinator;
        _onToggleRecorder = onToggleRecorder;
        _onOpenTab = onOpenTab;
        _onQuit = onQuit;

        _iconMark = MarkImage(badge: null);
        _iconRecording = MarkImage(badge: ThemeColors.FromArgb(0xFFEF5350));
        _iconTranscribing = MarkImage(badge: ThemeColors.Get("Mint"));
        _iconError = ErrorSymbol();

        _item = NSStatusBar.SystemStatusBar.CreateStatusItem(NSStatusItemLength.Square);
        if (_item.Button is { } button)
        {
            button.Image = _iconMark;
            button.ToolTip = "KusPus — local voice-to-text";
        }

        var menu = new NSMenu();
        var header = new NSMenuItem("KusPus") { Enabled = false };
        _stateItem = new NSMenuItem(string.Empty) { Enabled = false };
        _toggleItem = new NSMenuItem("Toggle Recording", (_, _) => _onToggleRecorder());
        _modelItem = new NSMenuItem(string.Empty, (_, _) => _onOpenTab("models"));
        var prefsItem = new NSMenuItem("Preferences…", (_, _) => _onOpenTab("general")) { KeyEquivalent = "," };
        var historyItem = new NSMenuItem("History…", (_, _) => _onOpenTab("history"));
        var quitItem = new NSMenuItem("Quit KusPus", (_, _) => _onQuit()) { KeyEquivalent = "q" };

        menu.AddItem(header);
        menu.AddItem(_stateItem);
        menu.AddItem(NSMenuItem.SeparatorItem);
        menu.AddItem(_toggleItem);
        menu.AddItem(_modelItem);
        menu.AddItem(NSMenuItem.SeparatorItem);
        menu.AddItem(prefsItem);
        menu.AddItem(historyItem);
        menu.AddItem(NSMenuItem.SeparatorItem);
        menu.AddItem(quitItem);
        _item.Menu = menu;

        Render(_coordinator.Snapshot, _prefs.Current);
        // Async hops: Changes can fire on a thread-pool thread while the main thread is
        // blocked in the very SaveAsync that produced it (see AppDelegate).
        _stateSub = _coordinator.State.Subscribe(snap =>
            CoreFoundation.DispatchQueue.MainQueue.DispatchAsync(() => Render(snap, _prefs.Current)));
        _prefsSub = _prefs.Changes.Subscribe(s =>
            CoreFoundation.DispatchQueue.MainQueue.DispatchAsync(() => Render(_coordinator.Snapshot, s)));
    }

    public void Dispose()
    {
        _stateSub.Dispose();
        _prefsSub.Dispose();
        _errorTimer?.Invalidate();
        NSStatusBar.SystemStatusBar.RemoveStatusItem(_item);
        _item.Dispose();
    }

    private void Render(CoordinatorSnapshot snap, AppSettings settings)
    {
        var version = NSBundle.MainBundle.InfoDictionary?["CFBundleShortVersionString"]?.ToString() ?? "dev";
        var stateText = snap.State switch
        {
            AppState.Recording => "Recording",
            AppState.Transcribing => "Transcribing",
            AppState.Armed => "Armed",
            _ => "Idle",
        };
        _stateItem.Title = $"Version {version} · {stateText}";
        _toggleItem.Title = snap.State == AppState.Recording
            ? $"Stop Recording   {HotkeyGlyphs.Format(settings.Hotkey)}"
            : $"Toggle Recording   {HotkeyGlyphs.Format(settings.Hotkey)}";

        var active = _models.Manifest.Models.FirstOrDefault(m =>
            string.Equals(m.Id, settings.Models.ActiveModelId, StringComparison.Ordinal));
        _modelItem.Title = $"Model: {active?.DisplayName ?? settings.Models.ActiveModelId}";

        if (_item.Button is { } button)
        {
            bool showError = snap.PostPaste is { Pasted: false } && !ReferenceEquals(snap, _expiredError);
            if (showError && !ReferenceEquals(snap, _timedError))
            {
                _timedError = snap;
                _errorTimer?.Invalidate();
                _errorTimer = Timers.Once(TimeSpan.FromSeconds(ErrorHoldSec), _ =>
                {
                    _errorTimer = null;
                    _expiredError = snap;
                    Render(_coordinator.Snapshot, _prefs.Current);
                });
            }
            button.Image = showError
                ? _iconError
                : snap.State switch
                {
                    AppState.Recording => _iconRecording,
                    AppState.Transcribing => _iconTranscribing,
                    _ => _iconMark,
                };
        }
    }

    private static NSImage Symbol(string name)
    {
        var img = NSImage.GetSystemSymbol(name, null) ?? new NSImage();
        img.Template = true;
        return img;
    }

    /// <summary>Red warning triangle — a template image would render monochrome.</summary>
    private static NSImage ErrorSymbol()
    {
        var img = NSImage.GetSystemSymbol("exclamationmark.triangle.fill", null);
        if (img is null)
        {
            return Symbol("exclamationmark.triangle");
        }
        var config = NSImageSymbolConfiguration.Create(NSColor.SystemRed);
        var tinted = img.GetImage(config) ?? img;
        tinted.Template = false;
        return tinted;
    }

    /// <summary>
    /// The brand mark at menu-bar size (18×18 pt): five rounded bars, centre tallest.
    /// Without a badge it is a template image (alpha mask → menu-bar tint, inverts on
    /// highlight). With a badge the bars are drawn in <c>LabelColor</c>, which the
    /// drawing handler resolves against the menu bar's appearance at draw time, and a
    /// small dot sits in the top-right corner inside a punched-out ring.
    /// </summary>
    private static NSImage MarkImage(NSColor? badge)
    {
        const double size = 18;
        var img = NSImage.ImageWithSize(new CoreGraphics.CGSize(size, size), false, _ =>
        {
            // Bars: width 2, pitch 3.6; heights 16 / 11 / 5.6 (centre → outer), centred.
            double[] heights = [5.6, 11, 16, 11, 5.6];
            (badge is null ? NSColor.Black : NSColor.Label).Set();
            for (int i = 0; i < heights.Length; i++)
            {
                double cx = (size / 2) + ((i - 2) * 3.6);
                double h = heights[i];
                var rect = new CoreGraphics.CGRect(cx - 1, (size - h) / 2, 2, h);
                NSBezierPath.FromRoundedRect(rect, 1, 1).Fill();
            }
            if (badge is not null)
            {
                var centre = new CoreGraphics.CGPoint(size - 3.5, size - 3.5);
                // Punch a ring so the dot never merges with a bar underneath it.
                var ctx = NSGraphicsContext.CurrentContext;
                var previousOp = ctx?.CompositingOperation ?? NSComposite.SourceOver;
                if (ctx is not null)
                {
                    ctx.CompositingOperation = NSComposite.DestinationOut;
                }
                NSColor.Black.Set();
                NSBezierPath.FromOvalInRect(new CoreGraphics.CGRect(centre.X - 4.25, centre.Y - 4.25, 8.5, 8.5)).Fill();
                if (ctx is not null)
                {
                    ctx.CompositingOperation = previousOp;
                }
                badge.Set();
                NSBezierPath.FromOvalInRect(new CoreGraphics.CGRect(centre.X - 3, centre.Y - 3, 6, 6)).Fill();
            }
            return true;
        });
        img.Template = badge is null;
        return img;
    }
}

/// <summary>Renders a <see cref="HotkeySettings"/> chord with Mac modifier glyphs (⌃ ⌥ ⇧ ⌘).</summary>
internal static class HotkeyGlyphs
{
    public static string Format(HotkeySettings hotkey)
    {
        var parts = new List<string>();
        foreach (var m in hotkey.Modifiers)
        {
            parts.Add(Glyph(m));
        }
        if (hotkey.KeyCode is { } key)
        {
            parts.Add(Glyph(key));
        }
        return string.Concat(parts);
    }

    public static string Glyph(VirtualKey key) => key switch
    {
        VirtualKey.LeftCtrl or VirtualKey.RightCtrl or VirtualKey.Control => "⌃",
        VirtualKey.LeftAlt or VirtualKey.RightAlt or VirtualKey.Alt => "⌥",
        VirtualKey.LeftShift or VirtualKey.RightShift or VirtualKey.Shift => "⇧",
        VirtualKey.LeftWin or VirtualKey.RightWin => "⌘",
        VirtualKey.Space => "Space",
        VirtualKey.Return => "↩",
        VirtualKey.Tab => "⇥",
        VirtualKey.Escape => "⎋",
        VirtualKey.Backspace => "⌫",
        VirtualKey.Delete => "⌦",
        VirtualKey.CapsLock => "⇪",
        >= VirtualKey.D0 and <= VirtualKey.D9 => ((char)('0' + (key - VirtualKey.D0))).ToString(),
        >= VirtualKey.A and <= VirtualKey.Z => ((char)('A' + (key - VirtualKey.A))).ToString(),
        >= VirtualKey.F1 and <= VirtualKey.F24 => "F" + (1 + (key - VirtualKey.F1)).ToString(System.Globalization.CultureInfo.InvariantCulture),
        _ => key.ToString(),
    };
}
