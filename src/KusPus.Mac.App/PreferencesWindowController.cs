#pragma warning disable CA1848
#pragma warning disable CA1873

using AppKit;
using CoreFoundation;
using CoreGraphics;
using Foundation;
using KusPus.Coordination;
using KusPus.Core.Platform;
using KusPus.Core.Settings;
using KusPus.Core.State;
using KusPus.Mac.Platform;
using KusPus.Persistence;
using KusPus.Whisper;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace KusPus.Mac.App;

/// <summary>
/// Preferences window — AppKit port of <c>MainWindow</c> (docs/APP_DESIGN.md §3). Same
/// six tabs with the same keys (<c>general audio models history privacy about</c>),
/// 200 pt sidebar with the brand footer (mark, wordmark, tagline, version), 880×620 default / 820×600 minimum.
/// Closing hides the window; only the status-item Quit exits (§3.1 / §8.5).
///
/// Every tab is code-built from one layout system (<see cref="Settings"/> sections,
/// grouped cards and <see cref="SettingsRow"/>s): neutral surfaces, a four-step type
/// scale, the 4/8 pt grid, and mint only for the primary action, selection and brand. A theme flip rebuilds the content
/// — the WPF window re-resolves DynamicResources; here a rebuild is the equivalent and
/// keeps the two code paths (first build, retheme) identical.
/// </summary>
internal sealed partial class PreferencesWindowController : IDisposable
{
    private static readonly string[] TabKeys = ["general", "audio", "models", "history", "privacy", "about"];

    private readonly IPrefsStore _prefs;
    private readonly IHotkeyEngine _hotkey;
    private readonly IModelManager _models;
    private readonly IHistoryStore _history;
    private readonly AppCoordinator _coordinator;
    private readonly IAudioRecorder _audio;
    private readonly ITranscriber _whisper;
    private readonly ILogger _logger;
    private readonly Func<Task> _rerunOnboarding;

    private readonly NSWindow _window;
    private readonly ThemedRootView _root;
    private readonly NSView _sidebar = new FlippedView();
    private readonly NSScrollView _scroll = new();
    private readonly FlippedView _document = new();
    private readonly List<SidebarTab> _tabButtons = new();
    private readonly Dictionary<string, NSStackView> _tabContent = new(StringComparer.Ordinal);
    // Host height follows the visible tab only: exactly one of these is active at a time.
    private readonly Dictionary<string, NSLayoutConstraint> _tabBottom = new(StringComparer.Ordinal);
    private string _currentTab = "general";
    private IDisposable? _prefsSub;
    private NSObject? _shortcutMonitor;
    private bool _suppressEvents;

    public PreferencesWindowController(
        IPrefsStore prefs,
        IHotkeyEngine hotkey,
        IModelManager models,
        IHistoryStore history,
        AppCoordinator coordinator,
        IAudioRecorder audio,
        ITranscriber whisper,
        Func<Task> rerunOnboarding,
        ILogger<PreferencesWindowController>? logger = null)
    {
        _prefs = prefs;
        _hotkey = hotkey;
        _models = models;
        _history = history;
        _coordinator = coordinator;
        _audio = audio;
        _whisper = whisper;
        _rerunOnboarding = rerunOnboarding;
        _logger = logger ?? NullLogger<PreferencesWindowController>.Instance;

        _window = new NSWindow(
            new CGRect(0, 0, 880, 620),
            NSWindowStyle.Titled | NSWindowStyle.Closable | NSWindowStyle.Miniaturizable | NSWindowStyle.Resizable,
            NSBackingStore.Buffered,
            false)
        {
            Title = "KusPus",
            MinSize = new CGSize(820, 600),
        };
        _window.ReleaseWhenClosed(false);
        _window.Center();
        // §3.1 / §8.5: close hides; only Quit exits.
        _window.WindowShouldClose = _ =>
        {
            OnHiding();
            _window.OrderOut(null);
            return false;
        };

        _root = new ThemedRootView();
        _window.ContentView = _root;
        _root.AppearanceChanged += () => DispatchQueue.MainQueue.DispatchAsync(RebuildContent);

        BuildShell();
        RebuildContent();

        _prefsSub = _prefs.Changes.Subscribe(s => DispatchQueue.MainQueue.DispatchAsync(() => OnSettingsChanged(s)));

        // ⌘1–⌘6 switch tabs (same order as the sidebar) while this window is key.
        _shortcutMonitor = NSEvent.AddLocalMonitorForEventsMatchingMask(NSEventMask.KeyDown, e =>
        {
            if (e.Window != _window || (e.ModifierFlags & NSEventModifierMask.DeviceIndependentModifierFlagsMask) != NSEventModifierMask.CommandKeyMask)
            {
                return e;
            }
            var chars = e.CharactersIgnoringModifiers;
            if (chars is { Length: 1 } && chars[0] >= '1' && chars[0] <= '6')
            {
                SelectTab(TabKeys[chars[0] - '1']);
                return null!;
            }
            return e;
        });
    }

    /// <summary>Opens the window on the named tab and brings it to the front.</summary>
    public void ShowOn(string tabKey)
    {
        SelectTab(tabKey);
        if (!_window.IsVisible)
        {
            _window.Center();
        }
        if (_window.IsMiniaturized)
        {
            _window.Deminiaturize(null);
        }
        NSApplication.SharedApplication.Activate();
        _window.MakeKeyAndOrderFront(null);
        if (_currentTab == "general")
        {
            StartPermissionPolling();
        }
        if (_currentTab == "history")
        {
            // New dictations since the tab was last rendered.
            _ = ReloadHistoryAsync();
        }
    }

    public void Dispose()
    {
        _prefsSub?.Dispose();
        if (_shortcutMonitor is not null)
        {
            NSEvent.RemoveMonitor(_shortcutMonitor);
        }
        OnHiding();
        _window.OrderOut(null);
        _window.Close();
    }

    // ── shell: sidebar + scrolling content ──────────────────────────────────

    private void BuildShell()
    {
        _sidebar.TranslatesAutoresizingMaskIntoConstraints = false;
        _root.AddSubview(_sidebar);

        _scroll.TranslatesAutoresizingMaskIntoConstraints = false;
        _scroll.HasVerticalScroller = true;
        _scroll.HasHorizontalScroller = false;
        _scroll.AutohidesScrollers = true;
        _scroll.DrawsBackground = false;
        _scroll.BorderType = NSBorderType.NoBorder;
        _document.TranslatesAutoresizingMaskIntoConstraints = false;
        _scroll.DocumentView = _document;
        _root.AddSubview(_scroll);

        NSLayoutConstraint.ActivateConstraints(
        [
            _sidebar.LeadingAnchor.ConstraintEqualTo(_root.LeadingAnchor),
            _sidebar.TopAnchor.ConstraintEqualTo(_root.TopAnchor),
            _sidebar.BottomAnchor.ConstraintEqualTo(_root.BottomAnchor),
            _sidebar.WidthAnchor.ConstraintEqualTo(200),
            _scroll.LeadingAnchor.ConstraintEqualTo(_sidebar.TrailingAnchor),
            _scroll.TrailingAnchor.ConstraintEqualTo(_root.TrailingAnchor),
            _scroll.TopAnchor.ConstraintEqualTo(_root.TopAnchor),
            _scroll.BottomAnchor.ConstraintEqualTo(_root.BottomAnchor),
            _document.LeadingAnchor.ConstraintEqualTo(_scroll.ContentView.LeadingAnchor),
            _document.TrailingAnchor.ConstraintEqualTo(_scroll.ContentView.TrailingAnchor),
            _document.TopAnchor.ConstraintEqualTo(_scroll.ContentView.TopAnchor),
            _document.WidthAnchor.ConstraintEqualTo(_scroll.ContentView.WidthAnchor),
        ]);
    }

    /// <summary>(Re)builds sidebar + every tab. Called on first show and on a theme flip.</summary>
    private void RebuildContent()
    {
        bool meterWasRunning = _meter?.IsRunning == true;
        StopAudioMeter();
        StopPermissionPolling();
        // A picker still in listen mode owns a local event monitor and has disabled the
        // global chord — tear that down before the view is thrown away.
        _hotkeyPicker?.CancelListenMode();
        _hotkeyPicker = null;

        _root.Layer!.BackgroundColor = ThemeColors.Cg("AppBg");
        _sidebar.WantsLayer = true;
        _sidebar.Layer!.BackgroundColor = ThemeColors.Cg("Sidebar");

        foreach (var v in _sidebar.Subviews.ToArray())
        {
            v.RemoveFromSuperview();
        }
        foreach (var v in _document.Subviews.ToArray())
        {
            v.RemoveFromSuperview();
        }
        _tabButtons.Clear();
        _tabContent.Clear();
        _tabBottom.Clear();

        BuildSidebar();

        // Plain host (not an NSStackView): a stack that detaches hidden views would drop
        // the width constraint of every hidden tab, so re-shown tabs shrink to content.
        var host = new FlippedView { TranslatesAutoresizingMaskIntoConstraints = false };
        _tabContent["general"] = BuildGeneralTab();
        _tabContent["audio"] = BuildAudioTab();
        _tabContent["models"] = BuildModelsTab();
        _tabContent["history"] = BuildHistoryTab();
        _tabContent["privacy"] = BuildPrivacyTab();
        _tabContent["about"] = BuildAboutTab();
        _document.AddSubview(host);
        NSLayoutConstraint.ActivateConstraints(
        [
            host.LeadingAnchor.ConstraintEqualTo(_document.LeadingAnchor, 32),
            host.TrailingAnchor.ConstraintEqualTo(_document.TrailingAnchor, -32),
            host.TopAnchor.ConstraintEqualTo(_document.TopAnchor, 24),
            host.BottomAnchor.ConstraintEqualTo(_document.BottomAnchor, -32),
        ]);
        foreach (var key in TabKeys)
        {
            var tab = _tabContent[key];
            host.AddSubview(tab);
            NSLayoutConstraint.ActivateConstraints(
            [
                tab.LeadingAnchor.ConstraintEqualTo(host.LeadingAnchor),
                tab.TrailingAnchor.ConstraintEqualTo(host.TrailingAnchor),
                tab.TopAnchor.ConstraintEqualTo(host.TopAnchor),
            ]);
            // Hidden views still take part in layout, so only the visible tab may pin the
            // host's bottom — SelectTab activates that one constraint.
            _tabBottom[key] = tab.BottomAnchor.ConstraintEqualTo(host.BottomAnchor);
        }

        SelectTab(_currentTab, force: true);
        if (meterWasRunning && _currentTab == "audio")
        {
            StartAudioMeter();
        }
    }

    private void BuildSidebar()
    {
        var divider = new NSView { WantsLayer = true, TranslatesAutoresizingMaskIntoConstraints = false };
        divider.Layer!.BackgroundColor = ThemeColors.Cg("BorderDivider");
        _sidebar.AddSubview(divider);

        var tabs = UI.VStack(4);
        (string Key, string Label, string Symbol)[] defs =
        [
            ("general", "General", "gearshape"),
            ("audio", "Audio", "mic"),
            ("models", "Models", "cpu"),
            ("history", "History", "clock"),
            ("privacy", "Privacy", "lock.shield"),
            ("about", "About", "info.circle"),
        ];
        for (int i = 0; i < defs.Length; i++)
        {
            var (key, label, symbol) = defs[i];
            var tab = new SidebarTab(label, symbol, $"⌘{i + 1}", () => SelectTab(key));
            _tabButtons.Add(tab);
            tabs.AddArrangedSubview(tab);
            tab.WidthAnchor.ConstraintEqualTo(tabs.WidthAnchor).Active = true;
        }
        _sidebar.AddSubview(tabs);

        var footer = BuildBrandFooter();
        _sidebar.AddSubview(footer);

        NSLayoutConstraint.ActivateConstraints(
        [
            divider.TrailingAnchor.ConstraintEqualTo(_sidebar.TrailingAnchor),
            divider.TopAnchor.ConstraintEqualTo(_sidebar.TopAnchor),
            divider.BottomAnchor.ConstraintEqualTo(_sidebar.BottomAnchor),
            divider.WidthAnchor.ConstraintEqualTo(1),
            tabs.LeadingAnchor.ConstraintEqualTo(_sidebar.LeadingAnchor, 12),
            tabs.TrailingAnchor.ConstraintEqualTo(_sidebar.TrailingAnchor, -12),
            tabs.TopAnchor.ConstraintEqualTo(_sidebar.TopAnchor, 12),
            footer.LeadingAnchor.ConstraintEqualTo(_sidebar.LeadingAnchor, 20),
            footer.TrailingAnchor.ConstraintEqualTo(_sidebar.TrailingAnchor, -16),
            footer.BottomAnchor.ConstraintEqualTo(_sidebar.BottomAnchor, -16),
        ]);
    }

    /// <summary>
    /// Brand lockup at the foot of the sidebar: the mark on its dark tile + mint wordmark,
    /// the brand line, and the version (links to About). Brand voice: quietly confident,
    /// private by nature — the line is the privacy promise in the user's own terms.
    /// </summary>
    private NSStackView BuildBrandFooter()
    {
        var tile = new FlippedView { TranslatesAutoresizingMaskIntoConstraints = false };
        tile.Layer!.BackgroundColor = ThemeColors.IslandTile.CGColor;
        tile.Layer.CornerRadius = 6;
        tile.WidthAnchor.ConstraintEqualTo(24).Active = true;
        tile.HeightAnchor.ConstraintEqualTo(24).Active = true;
        var mark = new NSImageView { ImageScaling = NSImageScale.ProportionallyUpOrDown, Image = UI.BundledImage("icon-idle.svg"), TranslatesAutoresizingMaskIntoConstraints = true };
        mark.Frame = new CoreGraphics.CGRect(4, 4, 16, 16);
        tile.AddSubview(mark);
        tile.AccessibilityElement = false; // decorative next to the wordmark

        var wordmark = UI.Tracked("KusPus", Fonts.Semibold(13), ThemeColors.Get("MintText"), 0.4);
        var lockup = UI.HStack(8, tile, wordmark);

        // Deliberate two-line break: the sidebar is too narrow for one line, and a
        // natural wrap orphans "one else.".
        var tagline = UI.Paragraph("Your words. Your Mac.\nNo one else.", Fonts.Supporting, "SecondaryText", lineSpacing: 2);

        var version = NSBundle.MainBundle.InfoDictionary?["CFBundleShortVersionString"]?.ToString() ?? "dev";
        var versionLink = UI.Button($"Version {version}", UI.ButtonKind.Ghost, () => SelectTab("about"));
        versionLink.Font = Fonts.Numeric(12);
        versionLink.ContentTintColor = ThemeColors.Get("MutedText");
        versionLink.ToolTip = "About KusPus";

        var footer = UI.VStack(8, lockup, tagline, versionLink);
        footer.SetCustomSpacing(4, tagline);
        tagline.WidthAnchor.ConstraintEqualTo(footer.WidthAnchor).Active = true;
        return footer;
    }

    private void SelectTab(string key, bool force = false)
    {
        if (!TabKeys.Contains(key))
        {
            key = "general";
        }
        var previous = _currentTab;
        _currentTab = key;
        for (int i = 0; i < _tabButtons.Count; i++)
        {
            _tabButtons[i].Selected = TabKeys[i] == key;
        }
        foreach (var (k, view) in _tabContent)
        {
            view.Hidden = k != key;
        }
        foreach (var (k, bottom) in _tabBottom)
        {
            bottom.Active = k == key;
        }

        if (previous == "general" && key != "general")
        {
            _hotkeyPicker?.CancelListenMode();
        }
        // The constructor (and appearance rebuilds) select a tab while the window is
        // hidden; only poll TCC while the user can actually see the rows.
        if (key == "general" && _window.IsVisible)
        {
            StartPermissionPolling();
        }
        else
        {
            StopPermissionPolling();
        }

        if (key == "audio")
        {
            _ = OpenAudioTabAsync();
        }
        else
        {
            StopAudioMeter();
        }
        if (key == "models" && (!_modelsRendered || force))
        {
            RenderModelsList();
        }
        if (key != "history" && _hoveredRow is not null)
        {
            _hoveredRow.SetHover(false);
            _hoveredRow = null;
        }
        if (key == "history" && (!_historyRendered || force))
        {
            _ = ReloadHistoryAsync();
        }
        _document.ScrollPoint(new CGPoint(0, 0));
    }

    private void OnHiding()
    {
        _hotkeyPicker?.CancelListenMode();
        _testCts?.Cancel();
        StopAudioMeter();
        StopPermissionPolling();
    }

    private void OnSettingsChanged(AppSettings settings)
    {
        ApplyCrashReportsGating(settings.Privacy.OfflineMode);
        if (_currentTab == "models")
        {
            RenderModelsList();
        }
        _hotkeyPicker?.ApplyHotkeyDisplay(settings.Hotkey);
    }

    // ── section scaffolding ─────────────────────────────────────────────────

    private static NSStackView Tab(params NSView[] sections)
    {
        var stack = UI.VStack(0, sections);
        foreach (var s in sections)
        {
            s.WidthAnchor.ConstraintEqualTo(stack.WidthAnchor).Active = true;
        }
        return stack;
    }


    /// <summary>
    /// Sidebar item. Selected = Surface fill, primary-text label, mint (text-safe) icon — no
    /// stripe. Focusable for Full Keyboard Access (Space/Return selects, focus ring drawn),
    /// exposed to VoiceOver as a radio button with its ⌘ shortcut in the help text.
    /// </summary>
    private sealed class SidebarTab : FlippedView
    {
        private readonly Action _onSelect;
        private readonly NSImageView _icon;
        private readonly NSTextField _label;
        private bool _selected;
        private bool _hover;
        private NSTrackingArea? _tracking;

        public SidebarTab(string label, string symbol, string shortcut, Action onSelect)
        {
            _onSelect = onSelect;
            TranslatesAutoresizingMaskIntoConstraints = false;
            Layer!.CornerRadius = 6;
            HeightAnchor.ConstraintEqualTo(32).Active = true;
            FocusRingType = NSFocusRingType.Default;

            _icon = UI.SymbolView(symbol, 14, "SecondaryText");
            _icon.AccessibilityElement = false;
            _label = UI.Label(label, Fonts.Body, "SecondaryText");
            _label.AccessibilityElement = false;
            AddSubview(_icon);
            AddSubview(_label);
            NSLayoutConstraint.ActivateConstraints(
            [
                _icon.LeadingAnchor.ConstraintEqualTo(LeadingAnchor, 8),
                _icon.CenterYAnchor.ConstraintEqualTo(CenterYAnchor),
                _icon.WidthAnchor.ConstraintEqualTo(20),
                _label.LeadingAnchor.ConstraintEqualTo(_icon.TrailingAnchor, 8),
                _label.CenterYAnchor.ConstraintEqualTo(CenterYAnchor),
            ]);

            AccessibilityElement = true;
            AccessibilityRole = NSAccessibilityRoles.RadioButtonRole;
            AccessibilityLabel = label;
            AccessibilityHelp = $"Shortcut {shortcut}";
            ToolTip = $"{label} ({shortcut})";
            Paint();
        }

        public bool Selected
        {
            get => _selected;
            set
            {
                _selected = value;
                AccessibilityValue = NSNumber.FromBoolean(value);
                Paint();
            }
        }

        public override bool AcceptsFirstResponder() => true;

        public override bool CanBecomeKeyView => true;

        public override CGRect FocusRingMaskBounds => Bounds;

        public override void DrawFocusRingMask() => NSBezierPath.FromRoundedRect(Bounds, 6, 6).Fill();

        public override bool AccessibilityPerformPress()
        {
            _onSelect();
            return true;
        }

        public override void KeyDown(NSEvent theEvent)
        {
            // Space or Return selects, like a native radio button.
            if (theEvent.KeyCode is 49 or 36 or 76)
            {
                _onSelect();
                return;
            }
            base.KeyDown(theEvent);
        }

        public override void UpdateTrackingAreas()
        {
            base.UpdateTrackingAreas();
            if (_tracking is not null)
            {
                RemoveTrackingArea(_tracking);
            }
            _tracking = new NSTrackingArea(Bounds, NSTrackingAreaOptions.MouseEnteredAndExited | NSTrackingAreaOptions.ActiveInKeyWindow | NSTrackingAreaOptions.InVisibleRect, this, null);
            AddTrackingArea(_tracking);
        }

        public override void MouseEntered(NSEvent theEvent)
        {
            _hover = true;
            Paint();
        }

        public override void MouseExited(NSEvent theEvent)
        {
            _hover = false;
            Paint();
        }

        public override void MouseDown(NSEvent theEvent) => _onSelect();

        private void Paint()
        {
            Layer!.BackgroundColor = _selected ? ThemeColors.Cg("Surface") : _hover ? ThemeColors.Cg("HoverSubtle") : NSColor.Clear.CGColor;
            _icon.ContentTintColor = ThemeColors.Get(_selected ? "MintText" : "SecondaryText");
            _label.TextColor = ThemeColors.Get(_selected ? "PrimaryText" : "SecondaryText");
            _label.Font = _selected ? Fonts.RowTitle : Fonts.Body;
        }
    }
}
