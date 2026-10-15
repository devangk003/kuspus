#pragma warning disable CA1848
#pragma warning disable CA1873

using AppKit;
using CoreAnimation;
using CoreFoundation;
using CoreGraphics;
using Foundation;
using KusPus.Core.Platform;
using KusPus.Core.State;
using KusPus.Mac.Platform;
using KusPus.Persistence;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace KusPus.Mac.App;

/// <summary>
/// The notch island — KusPus's macOS dictation indicator, modelled on Dynamic Island and
/// The Boring Notch. It replaces the bottom-centre floating pill.
///
/// <list type="bullet">
/// <item>A fixed-size, transparent, non-activating <c>NSPanel</c> parked over the notch at
///   <c>MainMenu + 3</c> (joins all Spaces, survives full-screen apps). Nothing in the
///   window is ever resized; the black island shape animates inside it.</item>
/// <item>Idle: the island is exactly the notch rect, solid black — invisible. On screens
///   without a notch it is a 185 pt black tab hanging from the menu bar.</item>
/// <item>Armed / Recording / Transcribing: compact "wings" (46 pt, Dynamic-Island compact
///   Live-Activity proportions) grow out of both sides of the notch at notch height — the
///   state-tinted brand mark in a small rounded tile on the left, a 7-bar mini visualiser
///   on the right (dots while armed, live while recording, a travelling shimmer while
///   transcribing).</item>
/// <item>Confirmed: the bars give way to a mint checkmark that pops in, then the wings
///   retract. Error: the tile shows the red-badged mark and the notch drops a one-line
///   panel (red triangle + reason) for 3 s.</item>
/// <item>Hover (300 ms dwell) / pinned: the island widens around the tile, the right wing
///   shows "Hold" + a keycap with the chord (or the live bars while dictating), and one
///   control row drops below the notch — Record pill, Mic chooser chip, then Settings,
///   Wand (dormant), Pin (toggle with a pressed state) and Quit on an 8 pt rhythm, under a
///   centred "KusPus" wordmark.</item>
/// <item>Shape: The Boring Notch's <c>NotchShape</c> — concave "ears" at the top corners,
///   convex bottom corners; radii 6/14 closed, 19/24 with the drop-down open.</item>
/// </list>
/// All coordinates inside the panel use a flipped (top-left origin) root view. Screen
/// coordinates are Cocoa (bottom-left origin).
/// </summary>
internal sealed class NotchPanelController : IDisposable
{
    // ── geometry (pt) ────────────────────────────────────────────────────────
    private const double WingMax = 160;
    private const double Pad = 16;
    private const double StateWing = 46;
    private const double HoverWing = 100;
    private const double DropOpen = 64;
    private const double WordmarkTop = 6;
    private const double WordmarkHeight = 16;
    private const double ControlRowTop = 28;
    private const double ControlHeight = 26;
    private const double ControlInset = 12;
    private const double ControlGap = 8;
    private const double ClusterGap = 4;
    private const double ClusterGlyph = 12;
    private const double KeycapHeight = 20;
    private const double KeycapPadX = 6;
    private const double HintGap = 6;
    private const double MicChipMinWidth = 120;
    private const double RecordGlyphSize = 8;
    private const double ClosedTopRadius = 6;
    private const double ClosedBottomRadius = 14;
    private const double OpenTopRadius = 19;
    private const double OpenBottomRadius = 24;

    private const int BarCount = 7;
    private const double BarWidth = 3;
    private const double BarGap = 2;
    private const double BarMinHeight = 3;
    private const double BarMaxInset = 14;
    private const double TrackWidth = (BarCount * BarWidth) + ((BarCount - 1) * BarGap);
    private const double TileSize = 22;
    private const double TileRadius = 6;
    private const double MarkSize = 16;
    private const double CheckSize = 18;
    private const double ErrorDrop = 40;
    private const double ErrorIconSize = 14;
    private const double ErrorRowGap = 8;
    private const double ErrorRowPad = 32;

    // ── timing ───────────────────────────────────────────────────────────────
    private const double OpenMs = 220;
    private const double CloseMs = 160;
    private const double CrossfadeMs = 150;
    private const double CheckPopMs = 250;
    private const double ConfirmHoldSec = 0.7;
    private const double ErrorHoldSec = 3.0;
    private const double HoverDwellMs = 300;
    private const double HoverBandBelowNotch = 10;
    private const double HoverMargin = 4;

    private enum PillVisual { Hidden, Idle, Armed, Recording, Transcribing, Confirmed, Error }
    private enum VisualizerMode { Off, Dots, Recording, Shimmer }

    private readonly IPrefsStore _prefs;
    private readonly IAudioRecorder _audio;
    private readonly Action _onRecordToggle;
    private readonly Action _onOpenSettings;
    private readonly Action _onClose;
    private readonly ILogger _logger;

    private readonly NotchPanel _panel;
    private readonly ThemedRootView _root;
    private readonly IslandView _island;
    private readonly CAShapeLayer _mask = new();
    private readonly FlippedView _tile;
    private readonly NSImageView _tileMark;
    private readonly NSImage? _markIdle;
    private readonly NSImage? _markRecording;
    private readonly NSImage? _markError;
    private readonly FlippedView _barsHost;
    private readonly CALayer[] _bars = new CALayer[BarCount];
    private readonly NSImageView _checkmark;
    private readonly FlippedView _errorRow;
    private readonly NSImageView _errorIcon;
    private readonly NSTextField _errorLabel;
    private readonly FlippedView _dropContent;
    private readonly FlippedView _hintGroup;
    private readonly NSTextField _hintWord;
    private readonly FlippedView _keycap;
    private readonly NSTextField _keycapLabel;
    private readonly HoverButton _wandButton;
    private readonly HoverButton _pinButton;
    private readonly NSImageView _pinGlyph;
    private readonly NSTextField _wordmark;
    private readonly NSTextField _wingLabel;
    private readonly FlippedView _controls;
    private readonly HoverButton _recordButton;
    private readonly CALayer _recordGlyph = new();
    private readonly NSTextField _recordLabel;
    private readonly HoverButton _micButton;
    private readonly NSTextField _micLabel;
    private readonly NSImageView _micIcon;
    private readonly NSImageView _micChevron;
    private readonly HoverButton _settingsButton;
    private readonly HoverButton _closeButton;

    private readonly Tween _tween = new();
    // 60 Hz while something is moving (recording bars, shimmer, hover, dock); stopped when
    // the island is idle so a menu-bar app doesn't wake 60×/s forever (battery, App Nap).
    // Restarted by WakeFrames() from state, hover, dock and picker changes.
    private NSTimer? _frameTimer;
    private int _idleFrames;
    private const int IdleFramesBeforeSleep = 30; // ~0.5 s, lets hover reconcile catch a missed exit
    private readonly Random _rng = new();
    private readonly double[] _levels = new double[BarCount];
    private readonly double[] _targets = new double[BarCount];
    private readonly double[] _reduced = new double[BarCount];
    private double _checkScale = 1;

    private NotchInfo _notch;
    private double _wing;
    private double _drop;
    private double _topRadius = ClosedTopRadius;
    private double _bottomRadius = ClosedBottomRadius;

    private DateTime _lastFrameTime = DateTime.MinValue;
    private DateTime _nextTargetAt = DateTime.MinValue;
    private double _shimmerPhase;
    private float[]? _lastRealLevels;
    private bool _isRecording;
    private VisualizerMode _visualizerMode = VisualizerMode.Off;
    private PillVisual _current = PillVisual.Hidden;
    private NSTimer? _postPasteTimer;
    private NSTimer? _hoverTimer;
    private NSTimer? _nudgeTimer;
    private NSPopover? _nudge;
    private bool _isPinned;
    private bool _pickerOpen;
    private bool _dockOpen;
    private bool _reduceAnimations;
    private bool _hovering;
    private IDisposable? _stateSub;
    private IDisposable? _levelsSub;
    private IDisposable? _prefsSub;
    private NSObject? _reduceMotionObserver;
    private NSObject? _screenObserver;
    private IReadOnlyList<CoreAudioDevices.InputDevice>? _cachedMics;

    public NotchPanelController(
        IPrefsStore prefs,
        IAudioRecorder audio,
        Action onRecordToggle,
        Action onOpenSettings,
        Action onClose,
        ILogger<NotchPanelController>? logger = null)
    {
        _prefs = prefs;
        _audio = audio;
        _onRecordToggle = onRecordToggle;
        _onOpenSettings = onOpenSettings;
        _onClose = onClose;
        _logger = logger ?? NullLogger<NotchPanelController>.Instance;

        _notch = NotchGeometry.For(ForegroundScreenLocator.ScreenForForegroundApp());
        _panel = new NotchPanel(new CGRect(0, 0, PanelWidth(), PanelHeight()));
        _root = new ThemedRootView();
        _panel.ContentView = _root;

        // ── island: black, masked to the notch shape ─────────────────────
        _island = new IslandView(this);
        _island.Layer!.BackgroundColor = ThemeColors.IslandBlack.CGColor;
        _island.Layer.Mask = _mask;
        _root.AddSubview(_island);

        // ── left wing: brand mark in a rounded tile (album-art style) ────
        _markIdle = UI.BundledImage("icon-idle.svg");
        _markRecording = UI.BundledImage("icon-recording.svg");
        _markError = UI.BundledImage("icon-error.svg");
        _tile = new FlippedView { AlphaValue = 0 };
        _tile.Layer!.BackgroundColor = ThemeColors.IslandTile.CGColor;
        _tile.Layer.CornerRadius = (nfloat)TileRadius;
        _tile.Layer.MasksToBounds = true;
        _tileMark = new NSImageView { ImageScaling = NSImageScale.ProportionallyUpOrDown, Image = _markIdle };
        _tileMark.TranslatesAutoresizingMaskIntoConstraints = true;
        _tile.AddSubview(_tileMark);
        _island.AddSubview(_tile);

        // Expanded state: "KusPus" sits beside the tile in the left wing (brand lockup).
        _wingLabel = UI.Tracked("KusPus", Fonts.Semibold(13), ThemeColors.Get("Mint"), 0.4); // ≈11:1 on black
        _wingLabel.TranslatesAutoresizingMaskIntoConstraints = true;
        _wingLabel.AlphaValue = 0;
        _island.AddSubview(_wingLabel);

        // ── right wing: mini visualiser / checkmark ──────────────────────
        _barsHost = new FlippedView { AlphaValue = 0 };
        var mint = ThemeColors.Get("Mint").CGColor;
        for (int i = 0; i < BarCount; i++)
        {
            var bar = new CALayer { CornerRadius = (nfloat)(BarWidth / 2), BackgroundColor = mint };
            _barsHost.Layer!.AddSublayer(bar);
            _bars[i] = bar;
            _levels[i] = 0;
            _targets[i] = 0;
        }
        _island.AddSubview(_barsHost);

        _checkmark = UI.SymbolView("checkmark.circle.fill", CheckSize, "Mint");
        _checkmark.TranslatesAutoresizingMaskIntoConstraints = true;
        _checkmark.AlphaValue = 0;
        _island.AddSubview(_checkmark);

        // ── error row: drops below the notch (text can't straddle the notch) ─
        _errorRow = new FlippedView { AlphaValue = 0 };
        _errorIcon = UI.SymbolView("exclamationmark.triangle.fill", ErrorIconSize, "ErrorRed");
        _errorIcon.ContentTintColor = ThemeColors.PillErrorAccent;
        _errorIcon.TranslatesAutoresizingMaskIntoConstraints = true;
        _errorLabel = UI.Label(string.Empty, Fonts.Medium(12.5), "PrimaryText");
        _errorLabel.TextColor = ThemeColors.IslandText;
        _errorLabel.TranslatesAutoresizingMaskIntoConstraints = true;
        _errorRow.AddSubview(_errorIcon);
        _errorRow.AddSubview(_errorLabel);
        _island.AddSubview(_errorRow);

        // ── right wing while expanded: "Hold" + keycap with the chord ────
        _hintGroup = new FlippedView { AlphaValue = 0 };
        _hintWord = UI.Label("Hold", Fonts.Medium(11), "PrimaryText");
        _hintWord.TextColor = ThemeColors.IslandTextMuted;
        _hintWord.TranslatesAutoresizingMaskIntoConstraints = true;
        _keycap = new FlippedView();
        _keycap.Layer!.BackgroundColor = ThemeColors.IslandMicBg.CGColor;
        _keycap.Layer.BorderColor = ThemeColors.IslandKeycapBorder.CGColor;
        _keycap.Layer.BorderWidth = 1;
        _keycap.Layer.CornerRadius = 5;
        _keycapLabel = UI.Label(string.Empty, Fonts.Mono(11), "PrimaryText");
        _keycapLabel.TextColor = ThemeColors.IslandText;
        _keycapLabel.Alignment = NSTextAlignment.Center;
        _keycapLabel.TranslatesAutoresizingMaskIntoConstraints = true;
        _keycap.AddSubview(_keycapLabel);
        _hintGroup.AddSubview(_hintWord);
        _hintGroup.AddSubview(_keycap);
        _island.AddSubview(_hintGroup);

        // ── drop-down: one control row under the notch ───────────────────
        _dropContent = new FlippedView { AlphaValue = 0 };
        _island.AddSubview(_dropContent);

        // Tagline: centred line under the notch (the name lives in the left wing).
        _wordmark = UI.Label("Your words. Your Mac. No one else.", Fonts.Medium(12), "PrimaryText");
        _wordmark.TextColor = ThemeColors.IslandTextMuted;
        _wordmark.Alignment = NSTextAlignment.Center;
        _wordmark.TranslatesAutoresizingMaskIntoConstraints = true;
        _dropContent.AddSubview(_wordmark);

        _controls = new FlippedView();
        _dropContent.AddSubview(_controls);

        // Record pill — the only record control (label flips to "Stop" while recording).
        _recordButton = new HoverButton(80, ControlHeight, ControlHeight / 2, _ => OnRecordToggleClick(), "Toggle Recording [BETA]")
        {
            HoverColor = ThemeColors.IslandHoverBg,
            RestingColor = ThemeColors.IslandMicBg,
        };
        _recordButton.Repaint();
        _recordGlyph.Frame = new CGRect(ControlInset, (ControlHeight - RecordGlyphSize) / 2, RecordGlyphSize, RecordGlyphSize);
        _recordButton.Layer!.AddSublayer(_recordGlyph);
        _recordLabel = UI.Label("Record", Fonts.Medium(11.5), "PrimaryText");
        _recordLabel.TextColor = ThemeColors.IslandText;
        _recordLabel.TranslatesAutoresizingMaskIntoConstraints = true;
        _recordButton.AddSubview(_recordLabel);
        _controls.AddSubview(_recordButton);

        // Mic chooser chip — flexes to fill the middle.
        _micButton = new HoverButton(MicChipMinWidth, ControlHeight, ControlHeight / 2, OnMicChooserClick, "Choose microphone")
        {
            HoverColor = ThemeColors.IslandHoverBg,
            RestingColor = ThemeColors.IslandMicBg,
        };
        _micButton.Repaint();
        _micIcon = UI.SymbolView("mic", 11, "SecondaryText");
        _micIcon.ContentTintColor = ThemeColors.IslandTextMuted;
        _micIcon.TranslatesAutoresizingMaskIntoConstraints = true;
        _micLabel = UI.Label("Default device", Fonts.Medium(11), "PrimaryText");
        _micLabel.TextColor = ThemeColors.IslandText;
        _micLabel.TranslatesAutoresizingMaskIntoConstraints = true;
        _micChevron = UI.SymbolView("chevron.down", 9, "MutedText");
        _micChevron.ContentTintColor = ThemeColors.IslandTextMuted;
        _micChevron.TranslatesAutoresizingMaskIntoConstraints = true;
        _micButton.AddSubview(_micIcon);
        _micButton.AddSubview(_micLabel);
        _micButton.AddSubview(_micChevron);
        _controls.AddSubview(_micButton);

        // Right cluster: Settings · Wand (dormant) · Pin (toggle) · Quit.
        _settingsButton = new HoverButton(ControlHeight, ControlHeight, 8, _ => { _logger.LogDebug("Settings button clicked — opening Preferences."); _onOpenSettings(); }, "Open Preferences") { HoverColor = ThemeColors.IslandHoverBg };
        _settingsButton.AddSubview(CenteredSymbol(_settingsButton, "gearshape", ClusterGlyph));
        _controls.AddSubview(_settingsButton);

        _wandButton = new HoverButton(ControlHeight, ControlHeight, 8, _ => { }, "Refine text — coming soon") { AlphaValue = (nfloat)0.35, Interactive = false, HoverColor = ThemeColors.IslandHoverBg };
        _wandButton.AddSubview(CenteredSymbol(_wandButton, "wand.and.stars", ClusterGlyph));
        _controls.AddSubview(_wandButton);

        _pinButton = new HoverButton(ControlHeight, ControlHeight, 8, _ => OnPinClick(), "Pin (keep open)") { HoverColor = ThemeColors.IslandHoverBg };
        _pinGlyph = CenteredSymbol(_pinButton, "pin.fill", ClusterGlyph);
        _pinButton.AddSubview(_pinGlyph);
        _controls.AddSubview(_pinButton);

        _closeButton = new HoverButton(ControlHeight, ControlHeight, 8, _ => { _logger.LogInformation("Close button clicked — invoking shutdown action."); _onClose(); }, "Quit KusPus")
        {
            HoverColor = NSColor.FromRgba(0xFF, 0x4D, 0x4F, 0x33),
        };
        _closeButton.AddSubview(CenteredSymbol(_closeButton, "xmark", ClusterGlyph));
        _controls.AddSubview(_closeButton);

        UpdateHint();
        UpdateRecordGlyph(PillVisual.Idle);
        LayoutAll();

        WakeFrames();

        // Mic chooser cache (warm in background like the WPF SetBridges path).
        RefreshMicCache();
        _prefsSub = _prefs.Changes.Subscribe(_ => DispatchQueue.MainQueue.DispatchAsync(() =>
        {
            UpdateMicChooserLabel();
            UpdateHint();
            UpdateReduceAnimations();
        }));
        // Posted on the workspace's own notification centre, not the default one.
        _reduceMotionObserver = NSWorkspace.SharedWorkspace.NotificationCenter.AddObserver(
            new NSString("NSWorkspaceAccessibilityDisplayOptionsDidChangeNotification"),
            _ => UpdateReduceAnimations());
        // Displays plugged/unplugged or resolution changes move the notch.
        _screenObserver = NSNotificationCenter.DefaultCenter.AddObserver(
            NSApplication.DidChangeScreenParametersNotification,
            _ => DispatchQueue.MainQueue.DispatchAsync(OnScreenParametersChanged));
        UpdateReduceAnimations();
    }

    // ── public binding API ───────────────────────────────────────────────────

    public void Bind(IObservable<CoordinatorSnapshot> state)
    {
        _stateSub = state.Subscribe(snap => DispatchQueue.MainQueue.DispatchAsync(() => Render(snap)));
    }

    public void BindLevels(IObservable<float[]> levels)
    {
        _levelsSub = levels.Subscribe(arr => _lastRealLevels = arr);
    }

    public void Dispose()
    {
        _stateSub?.Dispose();
        _levelsSub?.Dispose();
        _prefsSub?.Dispose();
        _reduceMotionObserver?.Dispose();
        _screenObserver?.Dispose();
        _frameTimer?.Invalidate();
        _frameTimer = null;
        _tween.Dispose();
        _postPasteTimer?.Invalidate();
        _hoverTimer?.Invalidate();
        _nudgeTimer?.Invalidate();
        _panel.OrderOut(null);
        _panel.Close();
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    private double PanelWidth() => _notch.Width + (2 * (WingMax + OpenTopRadius + Pad));

    private double PanelHeight() => _notch.Height + DropOpen + Pad;

    private static NSImageView CenteredSymbol(NSView parent, string symbol, double pointSize)
    {
        var iv = UI.SymbolView(symbol, pointSize, "SecondaryText");
        iv.ContentTintColor = ThemeColors.IslandTextMuted;
        iv.TranslatesAutoresizingMaskIntoConstraints = true;
        iv.Frame = new CGRect(0, 0, parent.Frame.Width, parent.Frame.Height);
        iv.AutoresizingMask = NSViewResizingMask.WidthSizable | NSViewResizingMask.HeightSizable;
        return iv;
    }

    private void UpdateHint()
    {
        var chord = HotkeyGlyphs.Format(_prefs.Current.Hotkey);
        bool has = !string.IsNullOrEmpty(chord);
        _hintWord.StringValue = has ? "Hold" : "Set hotkey";
        _keycapLabel.StringValue = chord;
        _keycap.Hidden = !has;
        _keycap.ToolTip = has ? $"Hold {chord} to dictate" : null;
        _hintWord.ToolTip = has ? null : "Set a hotkey in Preferences";
        LayoutAll();
    }

    /// <summary>
    /// The Boring Notch's <c>NotchShape</c> in flipped coordinates: concave quad-curve
    /// "ears" at the two top corners (so the island visually grows out of the screen
    /// edge) and convex bottom corners. The body spans <c>topR … w − topR</c>.
    /// </summary>
    private static CGPath NotchPath(double w, double h, double topR, double bottomR)
    {
        topR = Math.Min(topR, w / 2);
        bottomR = Math.Min(bottomR, Math.Max(0, (w / 2) - topR));
        bottomR = Math.Min(bottomR, Math.Max(0, h - topR));
        var p = new CGPath();
        p.MoveToPoint(0, 0);
        p.AddQuadCurveToPoint((nfloat)topR, 0, (nfloat)topR, (nfloat)topR);
        p.AddLineToPoint((nfloat)topR, (nfloat)(h - bottomR));
        p.AddQuadCurveToPoint((nfloat)topR, (nfloat)h, (nfloat)(topR + bottomR), (nfloat)h);
        p.AddLineToPoint((nfloat)(w - topR - bottomR), (nfloat)h);
        p.AddQuadCurveToPoint((nfloat)(w - topR), (nfloat)h, (nfloat)(w - topR), (nfloat)(h - bottomR));
        p.AddLineToPoint((nfloat)(w - topR), (nfloat)topR);
        p.AddQuadCurveToPoint((nfloat)(w - topR), 0, (nfloat)w, 0);
        p.CloseSubpath();
        return p;
    }

    // ── layout (flipped coordinates) ─────────────────────────────────────────

    internal void LayoutAll()
    {
        double nw = _notch.Width;
        double nh = _notch.Height;
        double topR = _topRadius;
        double w = nw + (2 * (_wing + topR));
        double h = nh + _drop;
        double cx = _panel.Frame.Width / 2;
        double bodyLeft = topR;                 // island-local x where the left wing starts
        double notchLeft = topR + _wing;        // island-local x of the notch's left edge
        double notchRight = notchLeft + nw;
        double bodyRight = notchRight + _wing;

        Layers.NoAnim(() =>
        {
            _island.Frame = new CGRect(cx - (w / 2), 0, w, h);
            _mask.Frame = _island.Bounds;
            _mask.Path = NotchPath(w, h, topR, _bottomRadius);

            // Left wing: tile centred in the wing, mark centred in the tile.
            // Tile alone is centred in the wing; while the panel opens the lockup widens to
            // tile + "KusPus" (progress follows the wing tween so nothing jumps).
            _wingLabel.SizeToFit();
            double labelW = _wingLabel.Frame.Width;
            bool brandInWing = (_dockOpen || _isPinned) && _current != PillVisual.Error;
            double p = brandInWing ? Math.Clamp((_wing - StateWing) / (HoverWing - StateWing), 0, 1) : 0;
            double lockupW = TileSize + (p * (8 + labelW));
            // Compact: centred in the wing. Open: pinned to the island edge with the same
            // inset as the control row, mirrored by the right-wing group.
            double centredX = bodyLeft + ((_wing - lockupW) / 2);
            double lockupX = centredX + (p * ((bodyLeft + ControlInset) - centredX));
            _tile.Frame = new CGRect(lockupX, (nh - TileSize) / 2, TileSize, TileSize);
            _wingLabel.Frame = new CGRect(lockupX + TileSize + 8, (nh - _wingLabel.Frame.Height) / 2, labelW, _wingLabel.Frame.Height);
            _tileMark.Frame = new CGRect((TileSize - MarkSize) / 2, (TileSize - MarkSize) / 2, MarkSize, MarkSize);

            // Right wing: mini bar track centred in the wing, full notch height.
            double trackLeft = notchRight + ((_wing - TrackWidth) / 2);
            _barsHost.Frame = new CGRect(trackLeft, 0, TrackWidth, nh);
            LayoutBars(nh);
            LayoutCheckmark();

            double bodyW = bodyRight - bodyLeft;

            // Error row: icon + reason centred in the drop area under the notch.
            _errorLabel.SizeToFit();
            double errTextW = Math.Max(0, Math.Min(_errorLabel.Frame.Width, bodyW - ErrorRowPad - ErrorIconSize - ErrorRowGap));
            double errRowW = ErrorIconSize + ErrorRowGap + errTextW;
            _errorRow.Frame = new CGRect(bodyLeft + ((bodyW - errRowW) / 2), nh, errRowW, ErrorDrop);
            _errorIcon.Frame = new CGRect(0, (ErrorDrop - ErrorIconSize) / 2, ErrorIconSize, ErrorIconSize);
            _errorLabel.Frame = new CGRect(ErrorIconSize + ErrorRowGap, (ErrorDrop - _errorLabel.Frame.Height) / 2, errTextW, _errorLabel.Frame.Height);

            // Right wing while expanded: "Hold" + keycap centred as one group.
            _hintWord.SizeToFit();
            _keycapLabel.SizeToFit();
            double keycapW = _keycap.Hidden ? 0 : _keycapLabel.Frame.Width + (2 * KeycapPadX);
            double groupW = _hintWord.Frame.Width + (_keycap.Hidden ? 0 : HintGap + keycapW);
            _hintGroup.Frame = new CGRect(notchRight + _wing - ControlInset - groupW, 0, groupW, nh);
            _hintWord.Frame = new CGRect(0, (nh - _hintWord.Frame.Height) / 2, _hintWord.Frame.Width, _hintWord.Frame.Height);
            _keycap.Frame = new CGRect(_hintWord.Frame.Width + HintGap, (nh - KeycapHeight) / 2, keycapW, KeycapHeight);
            _keycapLabel.Frame = new CGRect(KeycapPadX, (KeycapHeight - _keycapLabel.Frame.Height) / 2, Math.Max(0, keycapW - (2 * KeycapPadX)), _keycapLabel.Frame.Height);

            // Control row under the notch: Record pill · Mic chip (flex) · Settings · Wand · Pin · Quit.
            _dropContent.Frame = new CGRect(bodyLeft, nh, bodyW, Math.Max(0, _drop));
            _wordmark.SizeToFit();
            _wordmark.Frame = new CGRect((bodyW - _wordmark.Frame.Width) / 2, WordmarkTop + ((WordmarkHeight - _wordmark.Frame.Height) / 2), _wordmark.Frame.Width, _wordmark.Frame.Height);
            _controls.Frame = new CGRect(0, ControlRowTop, bodyW, ControlHeight);
            double x = ControlInset;
            _recordLabel.SizeToFit();
            double recordW = ControlInset + RecordGlyphSize + HintGap + _recordLabel.Frame.Width + ControlInset;
            _recordButton.Frame = new CGRect(x, 0, recordW, ControlHeight);
            _recordLabel.Frame = new CGRect(ControlInset + RecordGlyphSize + HintGap, (ControlHeight - _recordLabel.Frame.Height) / 2, _recordLabel.Frame.Width, _recordLabel.Frame.Height);
            x += recordW + ControlGap;

            double right = bodyW - ControlInset;
            right -= ControlHeight;
            _closeButton.Frame = new CGRect(right, 0, ControlHeight, ControlHeight);
            right -= ClusterGap + ControlHeight;
            _pinButton.Frame = new CGRect(right, 0, ControlHeight, ControlHeight);
            right -= ClusterGap + ControlHeight;
            _wandButton.Frame = new CGRect(right, 0, ControlHeight, ControlHeight);
            right -= ClusterGap + ControlHeight;
            _settingsButton.Frame = new CGRect(right, 0, ControlHeight, ControlHeight);

            double micW = Math.Max(0, right - ControlGap - x);
            _micButton.Frame = new CGRect(x, 0, micW, ControlHeight);
            _micIcon.Frame = new CGRect(10, (ControlHeight - 12) / 2, 12, 12);
            _micChevron.Frame = new CGRect(micW - 10 - 9, (ControlHeight - 10) / 2, 9, 10);
            _micLabel.SizeToFit();
            double micLabelX = 10 + 12 + HintGap;
            double micLabelW = Math.Max(0, micW - micLabelX - HintGap - 9 - 10);
            _micLabel.Frame = new CGRect(micLabelX, (ControlHeight - _micLabel.Frame.Height) / 2, micLabelW, _micLabel.Frame.Height);
        });
    }

    /// <summary>Checkmark centred on the bar track, scaled by the pop tween.</summary>
    private void LayoutCheckmark()
    {
        var track = _barsHost.Frame;
        double size = CheckSize * _checkScale;
        _checkmark.Frame = new CGRect(track.GetMidX() - (size / 2), track.GetMidY() - (size / 2), size, size);
    }

    private void LayoutBars(double trackHeight)
    {
        double maxH = Math.Max(BarMinHeight, trackHeight - BarMaxInset);
        for (int i = 0; i < BarCount; i++)
        {
            double hh = BarMinHeight + (_levels[i] * (maxH - BarMinHeight));
            _bars[i].Frame = new CGRect(i * (BarWidth + BarGap), (trackHeight - hh) / 2, BarWidth, hh);
        }
    }

    // ── state rendering ──────────────────────────────────────────────────────

    private void Render(CoordinatorSnapshot snapshot)
    {
        _logger.LogDebug("Island render: state={State} hold={Hold} postPaste={Post}.", snapshot.State, snapshot.IsHoldMode, snapshot.PostPaste);

        if (snapshot.PostPaste is { } post)
        {
            if (post.Pasted)
            {
                ShowConfirmed();
            }
            else
            {
                ShowError(post.ErrorReason ?? "Something went wrong");
            }
            return;
        }

        if (snapshot.State is AppState.Armed or AppState.Recording)
        {
            _postPasteTimer?.Invalidate();
            _postPasteTimer = null;
            EnsureOnForegroundScreen();
        }

        switch (snapshot.State)
        {
            case AppState.Armed:
                TransitionTo(PillVisual.Armed);
                break;
            case AppState.Recording:
                TransitionTo(PillVisual.Recording);
                break;
            case AppState.Transcribing:
                TransitionTo(PillVisual.Transcribing);
                break;
            default:
                if (_postPasteTimer is null)
                {
                    TransitionTo(PillVisual.Idle);
                }
                break;
        }
    }

    private void TransitionTo(PillVisual next)
    {
        if (next == _current)
        {
            return;
        }
        _logger.LogDebug("Island visual {From} → {To}.", _current, next);
        WakeFrames();
        var previous = _current;
        _current = next;
        ApplyIdlePassthrough();
        _isRecording = next == PillVisual.Recording;
        var previousMode = _visualizerMode;
        _visualizerMode = next switch
        {
            PillVisual.Armed => VisualizerMode.Dots,
            PillVisual.Recording => VisualizerMode.Recording,
            PillVisual.Transcribing => VisualizerMode.Shimmer,
            _ => VisualizerMode.Off,
        };
        if (previousMode == VisualizerMode.Shimmer)
        {
            Layers.NoAnim(() =>
            {
                foreach (var bar in _bars)
                {
                    bar.Opacity = 1;
                }
            });
        }
        UpdateRecordGlyph(next);
        _tileMark.Image = next switch
        {
            PillVisual.Recording => _markRecording ?? _markIdle,
            PillVisual.Error => _markError ?? _markIdle,
            _ => _markIdle,
        };
        if (next == PillVisual.Confirmed)
        {
            PopCheckmark();
        }

        UpdateContentVisibility();
        ApplyLayout();

        if (previous == PillVisual.Hidden)
        {
            ShowAtForegroundScreen();
        }
        else if (next == PillVisual.Hidden)
        {
            _panel.OrderOut(null);
        }
    }

    private void UpdateContentVisibility()
    {
        bool wings = _current is PillVisual.Armed or PillVisual.Recording or PillVisual.Transcribing or PillVisual.Confirmed or PillVisual.Error;
        bool bars = _current is PillVisual.Armed or PillVisual.Recording or PillVisual.Transcribing;
        bool check = _current == PillVisual.Confirmed;
        bool error = _current == PillVisual.Error;
        bool dockOpen = (_dockOpen || _isPinned) && !error;
        // The tile is the island's identity: shown for every dictation state and whenever
        // the panel is open. Armed = "listening for the hold threshold": 70 % until audio starts.
        bool tile = wings || dockOpen;
        Layers.Fade(_tile, tile ? (_current == PillVisual.Armed ? 0.7 : 1) : 0, CrossfadeMs, easeOut: tile);
        Layers.Fade(_barsHost, bars ? 1 : 0, check ? CheckPopMs / 2 : CrossfadeMs, easeOut: bars);
        Layers.Fade(_checkmark, check ? 1 : 0, check ? CheckPopMs / 2 : CrossfadeMs, easeOut: check);
        Layers.Fade(_errorRow, error ? 1 : 0, CrossfadeMs, easeOut: error);
        bool dock = dockOpen;
        // The hint shares the right wing with the bars, so it only shows while nothing is
        // being dictated (Armed already shows the dotted bars).
        bool hint = dock && _current == PillVisual.Idle;
        Layers.Fade(_hintGroup, hint ? 1 : 0, CrossfadeMs, easeOut: hint);
        Layers.Fade(_wingLabel, dock ? 1 : 0, dock ? OpenMs : CloseMs / 2, easeOut: dock);
        Layers.Fade(_dropContent, dock ? 1 : 0, dock ? OpenMs : CloseMs, easeOut: dock);
    }

    /// <summary>Success cue: the checkmark scales 0.6 → 1 (no scale under Reduce Motion).</summary>
    private void PopCheckmark()
    {
        if (_reduceAnimations)
        {
            _checkScale = 1;
            Layers.NoAnim(LayoutCheckmark);
            return;
        }
        _tween.Animate("check", 0.6, 1.0, CheckPopMs, easeOut: true, v =>
        {
            _checkScale = v;
            Layers.NoAnim(LayoutCheckmark);
        });
    }

    /// <summary>Single source of truth for the island's size: visual state × dock.</summary>
    private void ApplyLayout()
    {
        bool dock = _dockOpen || _isPinned;
        double stateWing = _current switch
        {
            PillVisual.Armed or PillVisual.Recording or PillVisual.Transcribing or PillVisual.Confirmed => StateWing,
            PillVisual.Error => ErrorWing(),
            _ => 0,
        };
        bool error = _current == PillVisual.Error;
        double wing = dock && !error ? Math.Max(stateWing, HoverWing) : stateWing;
        // The error panel wins over the dock (even pinned); the dock comes back after the hold.
        double drop = error ? ErrorDrop : (dock ? DropOpen : 0);
        double topR = drop > 0 ? OpenTopRadius : ClosedTopRadius;
        double bottomR = drop > 0 ? OpenBottomRadius : ClosedBottomRadius;

        bool growing = wing > _wing || drop > _drop;
        double ms = _reduceAnimations ? 1 : (growing ? OpenMs : CloseMs);
        double fromWing = _wing, fromDrop = _drop, fromTop = _topRadius, fromBottom = _bottomRadius;
        _tween.Animate("size", 0, 1, ms, easeOut: growing, t =>
        {
            _wing = fromWing + ((wing - fromWing) * t);
            _drop = fromDrop + ((drop - fromDrop) * t);
            _topRadius = fromTop + ((topR - fromTop) * t);
            _bottomRadius = fromBottom + ((bottomR - fromBottom) * t);
            LayoutAll();
        });
    }

    /// <summary>Wings stay compact unless the error row needs more room than the body has.</summary>
    private double ErrorWing()
    {
        _errorLabel.SizeToFit();
        double rowW = ErrorIconSize + ErrorRowGap + _errorLabel.Frame.Width;
        return Math.Clamp((rowW + ErrorRowPad - _notch.Width) / 2, StateWing, WingMax);
    }

    // ── post-paste ───────────────────────────────────────────────────────────

    private void ShowConfirmed()
    {
        TransitionTo(PillVisual.Confirmed);
        StartPostPasteHold(ConfirmHoldSec);
    }

    private void ShowError(string reason)
    {
        _errorLabel.StringValue = reason;
        LayoutAll();
        bool alreadyError = _current == PillVisual.Error;
        TransitionTo(PillVisual.Error);
        if (alreadyError)
        {
            // TransitionTo early-returns for the same visual, so the wing wouldn't be
            // re-measured for a longer message.
            ApplyLayout();
        }
        StartPostPasteHold(ErrorHoldSec);
    }

    private void StartPostPasteHold(double seconds)
    {
        _postPasteTimer?.Invalidate();
        _postPasteTimer = Timers.Once(TimeSpan.FromSeconds(seconds), _ =>
        {
            _postPasteTimer = null;
            TransitionTo(PillVisual.Idle);
            ReevaluateHover();
        });
    }

    /// <summary>Hover is ignored while an error shows; re-run it once the island is back to normal.</summary>
    private void ReevaluateHover()
    {
        bool inside = CursorInsideIsland();
        _hovering = !inside;
        OnHoverChanged(inside);
    }

    // ── positioning ──────────────────────────────────────────────────────────

    private void ShowAtForegroundScreen()
    {
        PlaceOnScreen(ForegroundScreenLocator.ScreenForForegroundApp());
        _panel.OrderFrontRegardless();
    }

    /// <summary>Parks the fixed-size panel over the screen's notch (real or virtual).</summary>
    private void PlaceOnScreen(NSScreen screen)
    {
        _notch = NotchGeometry.For(screen);
        double pw = PanelWidth();
        double ph = PanelHeight();
        var frame = new CGRect(_notch.MidX - (pw / 2), screen.Frame.GetMaxY() - ph, pw, ph);
        _logger.LogDebug("Island placing on {Screen}: notch {W:F0}×{H:F0} ({Kind}), panel ({X:F0},{Y:F0}) {PW:F0}×{PH:F0}.",
            ForegroundScreenLocator.ScreenKey(screen), _notch.Width, _notch.Height, _notch.IsReal ? "real" : "virtual", frame.X, frame.Y, pw, ph);
        _panel.SetFrame(frame, true);
        LayoutAll();
        ApplyIdlePassthrough();
    }

    /// <summary>
    /// With no notch and no visible menu bar, the idle tab would sit over app content
    /// (browser tabs, title bars) and swallow clicks. Hide it and let clicks through until
    /// there is something to show (hotkey → Armed/Recording, errors, confirmations).
    /// </summary>
    private void ApplyIdlePassthrough()
    {
        bool passive = _notch.OverlapsContent && _current is PillVisual.Idle && !_dockOpen;
        _panel.IgnoresMouseEvents = passive;
        _panel.AlphaValue = passive ? 0 : 1;
    }

    private void EnsureOnForegroundScreen()
    {
        var fg = ForegroundScreenLocator.ScreenForForegroundApp();
        var current = _panel.Screen;
        if (current is not null && ForegroundScreenLocator.ScreenKey(current) == ForegroundScreenLocator.ScreenKey(fg))
        {
            return;
        }
        PlaceOnScreen(fg);
    }

    private void OnScreenParametersChanged()
    {
        if (_current == PillVisual.Hidden)
        {
            return;
        }
        var screen = _panel.Screen ?? ForegroundScreenLocator.ScreenForForegroundApp();
        PlaceOnScreen(screen);
    }

    /// <summary>Live cursor test against the island's on-screen rect (the notch when idle).</summary>
    private bool CursorInsideIsland()
    {
        var winRect = _root.ConvertRectToView(_island.Frame, null);
        var screenRect = _panel.ConvertRectToScreen(winRect);
        // macOS keeps the cursor out of the hardware notch (it is clamped to the notch's
        // bottom edge), so the closed island gets a hit band below it — Boring Notch's
        // zeroHeightHoverPadding. The open island gets a small margin against flapping.
        // The cursor can also sit exactly on the screen's top edge (y == maxY), which an
        // exclusive Contains would miss, so the rect is padded above as well. Leaving
        // uses a wider margin than entering (hysteresis against edge jitter).
        double padBelow = (_dockOpen || _isPinned) ? HoverMargin : HoverBandBelowNotch;
        double side = _hovering ? HoverMargin * 3 : HoverMargin;
        screenRect = new CGRect(
            screenRect.X - side,
            screenRect.Y - padBelow - (_hovering ? HoverMargin * 2 : 0),
            screenRect.Width + (2 * side),
            screenRect.Height + padBelow + (_hovering ? HoverMargin * 2 : 0) + HoverMargin * 2);
        return screenRect.Contains(NSEvent.CurrentMouseLocation);
    }

    // ── hover / dock ─────────────────────────────────────────────────────────

    /// <summary>
    /// Fed by the island's tracking area (fast path) and by the per-frame reconcile in
    /// <see cref="OnFrame"/>, which catches enter/exit events AppKit drops when the island
    /// grows or shrinks under the cursor. Opening waits for a 300 ms dwell (Boring Notch's
    /// <c>minimumHoverDuration</c>) so a cursor sweep along the top edge doesn't pop it.
    /// </summary>
    internal void OnHoverChanged(bool hovering)
    {
        if (hovering == _hovering)
        {
            return;
        }
        _logger.LogDebug("Island hover → {Hovering}.", hovering);
        _hovering = hovering;
        WakeFrames();
        _hoverTimer?.Invalidate();
        _hoverTimer = null;
        if (_current == PillVisual.Error)
        {
            return; // the error panel is already down; ReevaluateHover runs after the hold
        }
        if (hovering)
        {
            if (_dockOpen || _isPinned)
            {
                return;
            }
            _hoverTimer = Timers.Once(TimeSpan.FromMilliseconds(HoverDwellMs), _ =>
            {
                _hoverTimer = null;
                if (_hovering && !_isPinned)
                {
                    OpenDock();
                }
            });
        }
        else
        {
            if (_pickerOpen || _isPinned)
            {
                return;
            }
            CloseDock();
        }
    }

    private void OpenDock()
    {
        if (_dockOpen || _current == PillVisual.Error)
        {
            return; // never under the error panel; ReevaluateHover retries after the hold
        }
        _dockOpen = true;
        WakeFrames();
        _logger.LogDebug("Island dock open.");
        UpdateContentVisibility();
        ApplyLayout();
    }

    private void CloseDock()
    {
        if (!_dockOpen)
        {
            return;
        }
        _dockOpen = false;
        WakeFrames();
        _logger.LogDebug("Island dock close.");
        UpdateContentVisibility();
        ApplyLayout();
    }

    private void OnPinClick()
    {
        _isPinned = !_isPinned;
        _logger.LogDebug("Pin toggled → {Pinned}.", _isPinned);
        UpdatePinState();
        if (_isPinned)
        {
            OpenDock();
        }
        else if (!_hovering)
        {
            CloseDock();
        }
    }

    /// <summary>Pin is a toggle: pressed = mint tint + mint glyph (matches "keep open").</summary>
    private void UpdatePinState()
    {
        _pinButton.RestingColor = _isPinned ? ThemeColors.IslandPinPressed : null;
        _pinButton.Repaint();
        _pinGlyph.ContentTintColor = _isPinned ? ThemeColors.Get("Mint") : ThemeColors.IslandTextMuted;
        _pinButton.ToolTip = _isPinned ? "Unpin" : "Pin (keep open)";
    }

    /// <summary>Record pill: muted dot + "Record" idle, red rounded square + "Stop" while recording.</summary>
    private void UpdateRecordGlyph(PillVisual state)
    {
        bool recording = state == PillVisual.Recording;
        var color = recording ? NSColor.FromRgb(0xEF, 0x53, 0x50).CGColor : ThemeColors.IslandTextMuted.CGColor;
        Layers.NoAnim(() =>
        {
            _recordGlyph.CornerRadius = (nfloat)(recording ? 2 : RecordGlyphSize / 2);
            _recordGlyph.BackgroundColor = color;
        });
        _recordLabel.StringValue = recording ? "Stop" : "Record";
        LayoutAll();
    }

    private void OnRecordToggleClick()
    {
        _logger.LogDebug("Record toggle clicked → ToggleFromTray.");
        _onRecordToggle();
        if (_current is PillVisual.Idle)
        {
            ShowRecordNudge();
        }
    }

    private void ShowRecordNudge()
    {
        _nudgeTimer?.Invalidate();
        _nudge?.Close();
        var label = UI.Label("Click into your text field", Fonts.Medium(11), "PrimaryText");
        var card = new CardView(label, "Surface", "BorderStrong", radius: 8, padX: 10, padY: 7);
        var vc = new NSViewController { View = card };
        _nudge = new NSPopover { ContentViewController = vc, Behavior = NSPopoverBehavior.ApplicationDefined, Animates = true };
        _nudge.Show(_recordButton.Bounds, _recordButton, NSRectEdge.MinYEdge);
        _nudgeTimer = Timers.Once(TimeSpan.FromSeconds(2), _ =>
        {
            _nudgeTimer = null;
            _nudge?.Close();
            _nudge = null;
        });
    }

    // ── mic chooser ──────────────────────────────────────────────────────────

    private void RefreshMicCache()
    {
        _ = Task.Run(() =>
        {
            try
            {
                var devices = CoreAudioDevices.EnumerateInputDevices();
                DispatchQueue.MainQueue.DispatchAsync(() =>
                {
                    _cachedMics = devices;
                    UpdateMicChooserLabel();
                });
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.Runtime.InteropServices.ExternalException)
            {
                _logger.LogDebug(ex, "Mic enumeration failed; keeping stale cache.");
            }
        });
    }

    private void UpdateMicChooserLabel()
    {
        var currentId = _prefs.Current.Audio.InputDeviceId;
        if (string.IsNullOrEmpty(currentId))
        {
            _micLabel.StringValue = "Default device";
        }
        else
        {
            var devices = _cachedMics ?? CoreAudioDevices.EnumerateInputDevices();
            _micLabel.StringValue = devices.FirstOrDefault(d => d.Uid == currentId)?.Name ?? "Default device";
        }
        LayoutAll();
    }

    private void OnMicChooserClick(NSEvent e)
    {
        var devices = _cachedMics ?? CoreAudioDevices.EnumerateInputDevices();
        var currentId = _prefs.Current.Audio.InputDeviceId;
        var menu = new NSMenu();
        menu.AddItem(MicItem("Default device (follows macOS)", null, string.IsNullOrEmpty(currentId)));
        foreach (var d in devices)
        {
            menu.AddItem(MicItem(d.Name, d.Uid, d.Uid == currentId));
        }
        _pickerOpen = true;
        NSMenu.PopUpContextMenu(menu, e, _micButton);
        // PopUpContextMenu returns when the menu is dismissed.
        _pickerOpen = false;
        WakeFrames();
        RefreshMicCache();
        if (!_hovering && !_isPinned)
        {
            CloseDock();
        }
    }

    private NSMenuItem MicItem(string title, string? uid, bool selected)
    {
        var item = new NSMenuItem(title, (_, _) => _ = SetInputDeviceAsync(uid)) { State = selected ? NSCellStateValue.On : NSCellStateValue.Off };
        return item;
    }

    private async Task SetInputDeviceAsync(string? uid)
    {
        var current = _prefs.Current;
        if (current.Audio.InputDeviceId == uid)
        {
            return;
        }
        try
        {
            await _prefs.UpdateAsync(s => s with { Audio = s.Audio with { InputDeviceId = uid } }).ConfigureAwait(true);
        }
        catch (IOException ex)
        {
            _logger.LogWarning(ex, "PrefsStore.SaveAsync failed for InputDeviceId (island).");
        }
    }

    // ── reduce motion ────────────────────────────────────────────────────────

    private void UpdateReduceAnimations()
    {
        _reduceAnimations = _prefs.Current.Privacy.ReducePillAnimations || NSWorkspace.SharedWorkspace.AccessibilityDisplayShouldReduceMotion;
    }

    // ── 60 Hz frame: visualizer motion model + shimmer + hover reconcile ────

    private void WakeFrames()
    {
        _idleFrames = 0;
        _frameTimer ??= Timers.Repeating(TimeSpan.FromMilliseconds(1000.0 / 60.0), _ => FrameTick());
    }

    private void FrameTick()
    {
        OnFrame();
        bool barsSettled = true;
        for (int i = 0; i < BarCount; i++)
        {
            if (Math.Abs(_levels[i] - _targets[i]) > 0.005)
            {
                barsSettled = false;
                break;
            }
        }
        bool idle = _current is PillVisual.Idle or PillVisual.Hidden
            && !_hovering && !_dockOpen && !_pickerOpen && !_isRecording
            && _visualizerMode == VisualizerMode.Off && barsSettled;
        if (!idle)
        {
            _idleFrames = 0;
            return;
        }
        if (++_idleFrames >= IdleFramesBeforeSleep)
        {
            _frameTimer?.Invalidate();
            _frameTimer = null;
            _lastFrameTime = DateTime.MinValue; // next wake starts with a nominal dt
        }
    }

    private void OnFrame()
    {
        if (_current is PillVisual.Hidden)
        {
            return;
        }
        // Reconcile cached hover with where the cursor really is: the island resizes
        // under the cursor on every open/close, which drops AppKit's enter/exit events.
        if (!_pickerOpen)
        {
            bool inside = CursorInsideIsland();
            if (inside != _hovering)
            {
                OnHoverChanged(inside);
            }
        }
        var now = DateTime.UtcNow;
        double dt = _lastFrameTime == DateTime.MinValue ? 0.016 : Math.Clamp((now - _lastFrameTime).TotalSeconds, 0, 0.064);
        _lastFrameTime = now;

        if (_isRecording && now >= _nextTargetAt)
        {
            _nextTargetAt = now.AddMilliseconds(90 + (_rng.NextDouble() * 60));
            var real = _lastRealLevels;
            if (real is not null && real.Length > 0)
            {
                ReduceBands(real, _reduced);
                for (int i = 0; i < BarCount; i++)
                {
                    double centerWeight = 1.0 - (Math.Abs(i - ((BarCount - 1) / 2.0)) / ((BarCount - 1) / 2.0));
                    double jitter = ((_rng.NextDouble() * 2) - 1) * 0.05;
                    double scaled = _reduced[i] * (0.6 + (centerWeight * 0.6)) * 4.0;
                    _targets[i] = Math.Clamp(scaled + jitter, 0.05, 1.0);
                }
            }
            else
            {
                double speak = 0.55 + (Math.Sin(now.Ticks / 1e7 / 0.38) * 0.25) + ((_rng.NextDouble() - 0.5) * 0.25);
                for (int i = 0; i < BarCount; i++)
                {
                    double centerWeight = 1.0 - (Math.Abs(i - ((BarCount - 1) / 2.0)) / ((BarCount - 1) / 2.0));
                    double baseVal = 0.18 + (centerWeight * 0.45);
                    double jitter = ((_rng.NextDouble() * 1.0) - 0.30) * 0.55;
                    _targets[i] = Math.Clamp((baseVal * speak) + jitter, 0.05, 1.0);
                }
            }
        }

        if (!_isRecording)
        {
            for (int i = 0; i < BarCount; i++)
            {
                _targets[i] = 0; // dots
            }
        }

        if (_barsHost.AlphaValue <= 0.001 && _visualizerMode == VisualizerMode.Off)
        {
            return; // nothing visible to animate
        }

        if (_visualizerMode == VisualizerMode.Shimmer)
        {
            _shimmerPhase += dt * (2 * Math.PI / 1.2);
        }

        Layers.NoAnim(() =>
        {
            for (int i = 0; i < BarCount; i++)
            {
                double rate = _isRecording ? (14 + ((i % 3) * 3)) : 6;
                double k = 1 - Math.Exp(-rate * dt);
                _levels[i] += (_targets[i] - _levels[i]) * k;
                if (_visualizerMode == VisualizerMode.Shimmer)
                {
                    _bars[i].Opacity = _reduceAnimations
                        ? 0.7f
                        : (float)(0.35 + (0.65 * Math.Max(0, Math.Sin(_shimmerPhase - (i * 0.6)))));
                }
            }
            LayoutBars(_notch.Height);
        });
    }

    /// <summary>Averages the recorder's RMS bands (20) down to one value per mini bar.</summary>
    private static void ReduceBands(float[] src, double[] dst)
    {
        int n = dst.Length;
        for (int i = 0; i < n; i++)
        {
            int start = i * src.Length / n;
            int end = Math.Max(start + 1, (i + 1) * src.Length / n);
            double sum = 0;
            for (int j = start; j < end && j < src.Length; j++)
            {
                sum += src[j];
            }
            dst[i] = sum / Math.Max(1, Math.Min(end, src.Length) - start);
        }
    }

    // ── nested: panel + island view ──────────────────────────────────────────

    /// <summary>
    /// Borderless, non-activating, transparent panel above the menu bar (Boring Notch's
    /// <c>BoringNotchWindow</c>). Fully transparent pixels are click-through, so the
    /// unused footprint around the island never blocks the menu bar.
    /// </summary>
    private sealed class NotchPanel : NSPanel
    {
        public NotchPanel(CGRect frame)
            : base(frame, NSWindowStyle.Borderless | NSWindowStyle.NonactivatingPanel, NSBackingStore.Buffered, false)
        {
            CollectionBehavior = NSWindowCollectionBehavior.CanJoinAllSpaces
                | NSWindowCollectionBehavior.Stationary
                | NSWindowCollectionBehavior.IgnoresCycle
                | NSWindowCollectionBehavior.FullScreenAuxiliary;
            // FloatingPanel resets Level to Floating, so the level is set AFTER it (same
            // order as Boring Notch). MainMenu + 3 sits above the menu-bar backdrop (24)
            // and the status items (25), which otherwise paint over the island.
            FloatingPanel = true;
            Level = (NSWindowLevel)((int)NSWindowLevel.MainMenu + 3);
            IsOpaque = false;
            BackgroundColor = NSColor.Clear;
            HasShadow = false;
            HidesOnDeactivate = false;
            MovableByWindowBackground = false;
            IsMovable = false;
            AnimationBehavior = NSWindowAnimationBehavior.None;
            ReleaseWhenClosed(false);
        }

        public override bool CanBecomeKeyWindow => false;

        public override bool CanBecomeMainWindow => false;

        /// <summary>
        /// AppKit keeps ordinary windows out of the menu-bar strip; the island lives
        /// exactly there, so the requested frame is used verbatim.
        /// </summary>
        public override CGRect ConstrainFrameRect(CGRect frameRect, NSScreen? screen) => frameRect;
    }

    /// <summary>The black island: hover tracking over its current (animated) frame.</summary>
    private sealed class IslandView : FlippedView
    {
        private readonly NotchPanelController _owner;

        public IslandView(NotchPanelController owner)
        {
            _owner = owner;
            // Added once: InVisibleRect keeps it in sync with the bounds as the island grows.
            AddTrackingArea(new NSTrackingArea(Bounds,
                NSTrackingAreaOptions.MouseEnteredAndExited | NSTrackingAreaOptions.ActiveAlways | NSTrackingAreaOptions.InVisibleRect,
                this, null));
        }

        public override void MouseEntered(NSEvent theEvent) => _owner.OnHoverChanged(true);

        public override void MouseExited(NSEvent theEvent) => _owner.OnHoverChanged(false);

        public override bool AcceptsFirstMouse(NSEvent? theEvent) => true;

        public override void MouseDown(NSEvent theEvent)
        {
            // Swallow: the island is anchored to the notch, nothing to drag.
        }
    }
}
