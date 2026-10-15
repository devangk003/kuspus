using AppKit;
using CoreAnimation;
using CoreGraphics;
using Foundation;

namespace KusPus.Mac.App;

/// <summary>
/// Root content view for every KusPus window. Raises <see cref="AppearanceChanged"/>
/// when the effective appearance flips so the owner can re-read <see cref="ThemeColors"/>
/// (the AppKit equivalent of WPF's DynamicResource re-resolution).
/// </summary>
internal sealed class ThemedRootView : NSView
{
    public ThemedRootView()
    {
        WantsLayer = true;
    }

    public event Action? AppearanceChanged;

    public override bool IsFlipped => true;

    public override void ViewDidChangeEffectiveAppearance()
    {
        base.ViewDidChangeEffectiveAppearance();
        AppearanceChanged?.Invoke();
    }
}

/// <summary>Top-left-origin container so layout code reads like WPF.</summary>
internal class FlippedView : NSView
{
    public FlippedView()
    {
        WantsLayer = true;
    }

    public override bool IsFlipped => true;
}

/// <summary>
/// Surface card — the <c>RowCard</c> / <c>Surface.*</c> styles: layer background,
/// 1 px border, corner radius, padding via an inner vertical stack.
/// </summary>
internal sealed class CardView : FlippedView
{
    private readonly string _bgKey;
    private readonly string _borderKey;

    public CardView(NSView content, string bgKey = "Surface", string borderKey = "BorderSubtle",
        double radius = 8, double padX = 16, double padY = 16, double borderWidth = 1)
    {
        _bgKey = bgKey;
        _borderKey = borderKey;
        Layer!.CornerRadius = (nfloat)radius;
        Layer.BorderWidth = (nfloat)borderWidth;
        TranslatesAutoresizingMaskIntoConstraints = false;
        content.TranslatesAutoresizingMaskIntoConstraints = false;
        AddSubview(content);
        NSLayoutConstraint.ActivateConstraints(
        [
            content.LeadingAnchor.ConstraintEqualTo(LeadingAnchor, (nfloat)padX),
            content.TrailingAnchor.ConstraintEqualTo(TrailingAnchor, (nfloat)(-padX)),
            content.TopAnchor.ConstraintEqualTo(TopAnchor, (nfloat)padY),
            content.BottomAnchor.ConstraintEqualTo(BottomAnchor, (nfloat)(-padY)),
        ]);
        Retheme();
    }

    public NSColor? BackgroundOverride { get; set; }
    public NSColor? BorderOverride { get; set; }

    public void Retheme()
    {
        Layer!.BackgroundColor = (BackgroundOverride ?? ThemeColors.Get(_bgKey)).CGColor;
        Layer.BorderColor = (BorderOverride ?? ThemeColors.Get(_borderKey)).CGColor;
    }

    public override void ViewDidChangeEffectiveAppearance()
    {
        base.ViewDidChangeEffectiveAppearance();
        Retheme();
    }
}

/// <summary>7×7 status dot with a soft glow (Dot.Mint / Dot.Amber / Dot.Red).</summary>
internal sealed class DotView : NSView
{
    private readonly string _colorKey;

    public DotView(string colorKey, double size = 7)
    {
        _colorKey = colorKey;
        WantsLayer = true;
        TranslatesAutoresizingMaskIntoConstraints = false;
        WidthAnchor.ConstraintEqualTo((nfloat)size).Active = true;
        HeightAnchor.ConstraintEqualTo((nfloat)size).Active = true;
        Layer!.CornerRadius = (nfloat)(size / 2);
        Layer.ShadowOffset = CGSize.Empty;
        Layer.ShadowRadius = 3;
        Layer.ShadowOpacity = 0.6f;
        Retheme();
    }

    public void Retheme()
    {
        var c = ThemeColors.Get(_colorKey);
        Layer!.BackgroundColor = c.CGColor;
        Layer.ShadowColor = c.CGColor;
    }

    public override void ViewDidChangeEffectiveAppearance()
    {
        base.ViewDidChangeEffectiveAppearance();
        Retheme();
    }
}

/// <summary>Label-building and layout helpers. Thin wrappers so tab code stays declarative.</summary>
internal static class UI
{
    public static NSTextField Label(string text, NSFont font, string colorKey, bool wraps = false)
    {
        var tf = wraps ? NSTextField.CreateWrappingLabel(text) : NSTextField.CreateLabel(text);
        tf.Font = font;
        tf.TextColor = ThemeColors.Get(colorKey);
        tf.TranslatesAutoresizingMaskIntoConstraints = false;
        if (!wraps)
        {
            tf.LineBreakMode = NSLineBreakMode.TruncatingTail;
            tf.UsesSingleLineMode = true;
        }
        return tf;
    }

    /// <summary>
    /// Wrapping body copy with explicit line spacing: SF's default line height is ~1.2×,
    /// <paramref name="lineSpacing"/> of 4 pt at 13 pt lands near the 1.5× readability target.
    /// </summary>
    public static NSTextField Paragraph(string text, NSFont font, string colorKey, double lineSpacing = 4)
    {
        var tf = Label(text, font, colorKey, wraps: true);
        var style = new NSMutableParagraphStyle { LineSpacing = (nfloat)lineSpacing, LineBreakMode = NSLineBreakMode.ByWordWrapping };
        tf.AttributedStringValue = new NSAttributedString(text, new NSStringAttributes
        {
            Font = font,
            ForegroundColor = ThemeColors.Get(colorKey),
            ParagraphStyle = style,
        });
        return tf;
    }

    /// <summary>Badge / caption text with letter-spacing (small caps-style labels read better tracked out).</summary>
    public static NSTextField Tracked(string text, NSFont font, NSColor color, double tracking)
    {
        var tf = Label(text, font, "PrimaryText");
        tf.AttributedStringValue = new NSAttributedString(text, new NSStringAttributes
        {
            Font = font,
            ForegroundColor = color,
            KerningAdjustment = (float)tracking,
        });
        return tf;
    }

    public static NSTextField SectionHeader(string text) => Label(text, Fonts.SectionHeader, "PrimaryText");
    public static NSTextField RowTitle(string text) => Label(text, Fonts.RowTitle, "PrimaryText");
    public static NSTextField RowSubtitle(string text) => Label(text, Fonts.RowSubtitle, "MutedText", wraps: true);
    public static NSTextField Eyebrow(string text, string colorKey = "MutedText") => Label(text, Fonts.Eyebrow, colorKey);
    public static NSTextField MonoSm(string text, string colorKey = "MutedText") => Label(text, Fonts.MonoSm, colorKey);
    public static NSTextField Hint(string text) => Label(text, Fonts.Hint, "DisabledText", wraps: true);

    public static NSStackView VStack(double spacing, params NSView[] views)
    {
        var s = NSStackView.FromViews(views);
        s.Orientation = NSUserInterfaceLayoutOrientation.Vertical;
        s.Alignment = NSLayoutAttribute.Leading;
        s.Spacing = (nfloat)spacing;
        s.Distribution = NSStackViewDistribution.Fill;
        s.TranslatesAutoresizingMaskIntoConstraints = false;
        return s;
    }

    public static NSStackView HStack(double spacing, params NSView[] views)
    {
        var s = NSStackView.FromViews(views);
        s.Orientation = NSUserInterfaceLayoutOrientation.Horizontal;
        s.Alignment = NSLayoutAttribute.CenterY;
        s.Spacing = (nfloat)spacing;
        s.Distribution = NSStackViewDistribution.Fill;
        s.TranslatesAutoresizingMaskIntoConstraints = false;
        return s;
    }

    /// <summary>Two-column row: left stretches, right hugs — the WPF <c>Grid * / Auto</c> idiom.</summary>
    public static NSStackView Row(NSView left, NSView right, double spacing = 16)
    {
        var row = HStack(spacing, left, right);
        left.SetContentHuggingPriorityForOrientation(1, NSLayoutConstraintOrientation.Horizontal);
        right.SetContentHuggingPriorityForOrientation(1000, NSLayoutConstraintOrientation.Horizontal);
        right.SetContentCompressionResistancePriority(1000, NSLayoutConstraintOrientation.Horizontal);
        return row;
    }

    public static NSView Spacer()
    {
        var v = new NSView { TranslatesAutoresizingMaskIntoConstraints = false };
        v.SetContentHuggingPriorityForOrientation(1, NSLayoutConstraintOrientation.Horizontal);
        v.SetContentHuggingPriorityForOrientation(1, NSLayoutConstraintOrientation.Vertical);
        return v;
    }

    public static T Width<T>(T view, double width) where T : NSView
    {
        view.WidthAnchor.ConstraintEqualTo((nfloat)width).Active = true;
        return view;
    }

    public static T Height<T>(T view, double height) where T : NSView
    {
        view.HeightAnchor.ConstraintEqualTo((nfloat)height).Active = true;
        return view;
    }

    public static NSView Divider()
    {
        var v = new NSView { WantsLayer = true, TranslatesAutoresizingMaskIntoConstraints = false };
        v.Layer!.BackgroundColor = ThemeColors.Cg("BorderDivider");
        v.HeightAnchor.ConstraintEqualTo(1).Active = true;
        return v;
    }

    public static NSSwitch Toggle(bool on, Action<bool> changed)
    {
        var sw = new NSSwitch { State = on ? 1 : 0, TranslatesAutoresizingMaskIntoConstraints = false };
        sw.ControlSize = NSControlSize.Small;
        sw.Activated += (_, _) => changed(sw.State != 0);
        return sw;
    }

    public enum ButtonKind { Primary, Secondary, Ghost, Danger }

    /// <summary>Btn.Primary / Secondary / Ghost / Danger from Styles/Buttons.xaml on a native push button.</summary>
    public static NSButton Button(string title, ButtonKind kind, Action action)
    {
        var b = NSButton.CreateButton(title, action);
        b.TranslatesAutoresizingMaskIntoConstraints = false;
        b.BezelStyle = NSBezelStyle.Rounded;
        b.ControlSize = NSControlSize.Regular;
        b.Font = Fonts.Body;
        ApplyKind(b, kind);
        return b;
    }

    public static void ApplyKind(NSButton b, ButtonKind kind)
    {
        b.Bordered = kind != ButtonKind.Ghost;
        b.BezelColor = kind == ButtonKind.Primary ? ThemeColors.Get("Mint") : null;
        b.ContentTintColor = kind switch
        {
            ButtonKind.Danger => ThemeColors.Get("ErrorText"),
            ButtonKind.Primary => ThemeColors.OnMint,
            _ => null,
        };
    }

    /// <summary>Btn.IconGhost — 28×28 borderless SF Symbol button.</summary>
    public static NSButton IconButton(string symbol, string tooltip, Action action, string tintKey = "MutedText", double pointSize = 13)
    {
        var img = Symbol(symbol, pointSize);
        var b = NSButton.CreateButton(img, action);
        b.Bordered = false;
        b.Title = string.Empty;
        b.ImagePosition = NSCellImagePosition.ImageOnly;
        b.ContentTintColor = ThemeColors.Get(tintKey);
        b.ToolTip = tooltip;
        b.TranslatesAutoresizingMaskIntoConstraints = false;
        Width(b, 28);
        Height(b, 28);
        return b;
    }

    public static NSImage Symbol(string name, double pointSize, double weight = 5)
    {
        var img = NSImage.GetSystemSymbol(name, null) ?? new NSImage();
        var cfg = NSImageSymbolConfiguration.Create((nfloat)pointSize, weight, NSImageSymbolScale.Medium);
        var configured = img.GetImage(cfg) ?? img;
        configured.Template = true;
        return configured;
    }

    public static NSImageView SymbolView(string name, double pointSize, string tintKey)
    {
        var iv = NSImageView.FromImage(Symbol(name, pointSize));
        iv.ContentTintColor = ThemeColors.Get(tintKey);
        iv.TranslatesAutoresizingMaskIntoConstraints = false;
        return iv;
    }

    /// <summary>Keycap chip — mono 11 Medium on KeycapBg with KeycapBorder (Styles "Keycap").</summary>
    public static CardView Keycap(string label, double padX = 14, double padY = 7)
    {
        var text = Label(label, Fonts.Mono(11), "PrimaryText");
        return new CardView(text, "KeycapBg", "KeycapBorder", radius: 6, padX: padX, padY: padY);
    }

    public static NSImage? BundledImage(string resourceName)
    {
        var path = NSBundle.MainBundle.PathForResource(Path.GetFileNameWithoutExtension(resourceName), Path.GetExtension(resourceName).TrimStart('.'));
        return path is null ? null : new NSImage(path);
    }

    public static void OpenUrl(string url)
    {
        using var nsUrl = new NSUrl(url);
        NSWorkspace.SharedWorkspace.OpenUrl(nsUrl);
    }

    public static string FormatBytes(long bytes)
    {
        if (bytes < 1024)
        {
            return $"{bytes} bytes";
        }
        if (bytes < 1024 * 1024)
        {
            return $"{bytes / 1024.0:F1} KB";
        }
        return $"{bytes / 1024.0 / 1024.0:F1} MB";
    }

    public static string FormatSize(long bytes) =>
        bytes >= 1024L * 1024 * 1024
            ? $"{bytes / 1024.0 / 1024 / 1024:F1} GB"
            : $"{bytes / 1024 / 1024} MB";

    public static string FormatRelative(DateTimeOffset ts)
    {
        var delta = DateTimeOffset.UtcNow - ts.ToUniversalTime();
        if (delta.TotalSeconds < 60)
        {
            return "just now";
        }
        if (delta.TotalMinutes < 60)
        {
            return $"{(int)delta.TotalMinutes}m ago";
        }
        if (delta.TotalHours < 24)
        {
            return $"{(int)delta.TotalHours}h ago";
        }
        if (delta.TotalDays < 7)
        {
            return $"{(int)delta.TotalDays}d ago";
        }
        return ts.LocalDateTime.ToString("yyyy-MM-dd HH:mm", System.Globalization.CultureInfo.InvariantCulture);
    }

    public static string ShortModelId(string raw) =>
        raw.StartsWith("ggml-", StringComparison.Ordinal) ? raw["ggml-".Length..] : raw;
}

/// <summary>
/// Hover-tinted icon button used across the pill chrome (pin, wand, record, settings,
/// close). Layer background flips to ButtonHoverBg on hover (or a custom colour).
/// </summary>
internal sealed class HoverButton : FlippedView
{
    private readonly Action<NSEvent> _onClick;
    private bool _hover;

    public HoverButton(double width, double height, double radius, Action<NSEvent> onClick, string tooltip)
    {
        _onClick = onClick;
        Frame = new CGRect(0, 0, width, height);
        Layer!.CornerRadius = (nfloat)radius;
        ToolTip = tooltip;
        // Added once (InVisibleRect tracks bounds changes) — see PillRootView.
        AddTrackingArea(new NSTrackingArea(Bounds,
            NSTrackingAreaOptions.MouseEnteredAndExited | NSTrackingAreaOptions.ActiveAlways | NSTrackingAreaOptions.InVisibleRect,
            this, null));
    }

    /// <summary>Hover fill; null means ButtonHoverBg from the theme.</summary>
    public NSColor? HoverColor { get; set; }

    /// <summary>Fill when not hovered (e.g. a toggle's pressed state); null means clear.</summary>
    public NSColor? RestingColor { get; set; }

    public bool Interactive { get; set; } = true;

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

    public override void MouseDown(NSEvent theEvent)
    {
        // Swallow so the pill's drag handler never sees button clicks (WPF IsClickOnButton).
    }

    public override void MouseUp(NSEvent theEvent)
    {
        if (Interactive && Bounds.Contains(ConvertPointFromView(theEvent.LocationInWindow, null)))
        {
            _onClick(theEvent);
        }
    }

    /// <summary>Re-applies the resting/hover fill (call after changing <see cref="RestingColor"/>).</summary>
    public void Repaint() => Paint();

    private void Paint()
    {
        Layer!.BackgroundColor = _hover && Interactive
            ? (HoverColor ?? ThemeColors.Get("ButtonHoverBg")).CGColor
            : (RestingColor ?? NSColor.Clear).CGColor;
    }
}

/// <summary>
/// NSTimer factory that schedules on the main run loop in <see cref="NSRunLoopMode.Common"/>.
/// <c>NSTimer.Create*ScheduledTimer</c> uses the default mode only, so those timers
/// freeze while a modal session (onboarding) or a menu/drag tracking loop runs.
/// </summary>
internal static class Timers
{
    public static NSTimer Repeating(TimeSpan interval, Action<NSTimer> action)
    {
        var t = NSTimer.CreateRepeatingTimer(interval, action);
        NSRunLoop.Main.AddTimer(t, NSRunLoopMode.Common);
        return t;
    }

    public static NSTimer Once(TimeSpan delay, Action<NSTimer> action)
    {
        var t = NSTimer.CreateTimer(delay, action);
        NSRunLoop.Main.AddTimer(t, NSRunLoopMode.Common);
        return t;
    }
}

/// <summary>
/// Frame-timer tween for window/layout animations (cubic ease, 60 Hz). Tweens are keyed:
/// starting one cancels any in-flight tween with the same key, so a quick hover in/out
/// can't leave two opposite animations fighting over the window frame.
/// </summary>
internal sealed class Tween : IDisposable
{
    // Created on demand and stopped when idle: a permanently running 60 Hz timer keeps a
    // menu-bar app awake (no App Nap) and costs battery for nothing.
    private NSTimer? _timer;
    private readonly List<(string Key, double From, double To, double Start, double Duration, bool EaseOut, Action<double> Apply, Action? Done)> _active = new();

    public void Animate(string key, double from, double to, double durationMs, bool easeOut, Action<double> apply, Action? done = null)
    {
        _active.RemoveAll(a => a.Key == key);
        _active.Add((key, from, to, Now(), durationMs, easeOut, apply, done));
        _timer ??= Timers.Repeating(TimeSpan.FromMilliseconds(1000.0 / 60.0), _ => Tick());
    }

    public void Dispose()
    {
        _timer?.Invalidate();
        _timer = null;
    }

    private static double Now() => System.Diagnostics.Stopwatch.GetTimestamp() * 1000.0 / System.Diagnostics.Stopwatch.Frequency;

    private void Tick()
    {
        if (_active.Count == 0)
        {
            _timer?.Invalidate();
            _timer = null;
            return;
        }
        var now = Now();
        // Oldest first so the most recently started tween for a key would win —
        // keys are unique after Animate's RemoveAll, so order no longer matters.
        foreach (var a in _active.ToArray())
        {
            var t = Math.Clamp((now - a.Start) / a.Duration, 0, 1);
            var e = a.EaseOut ? 1 - Math.Pow(1 - t, 3) : t * t * t;
            a.Apply(a.From + ((a.To - a.From) * e));
            if (t >= 1)
            {
                _active.Remove(a);
                a.Done?.Invoke();
            }
        }
    }
}

/// <summary>Layer helpers — implicit-animation-free property sets and simple fades.</summary>
internal static class Layers
{
    public static void NoAnim(Action a)
    {
        CATransaction.Begin();
        CATransaction.DisableActions = true;
        try
        {
            a();
        }
        finally
        {
            CATransaction.Commit();
        }
    }

    public static void Fade(NSView view, double to, double durationMs, bool easeOut = true)
    {
        var layer = view.Layer;
        if (layer is null)
        {
            view.AlphaValue = (nfloat)to;
            return;
        }
        var anim = CABasicAnimation.FromKeyPath("opacity");
        // Start from what's on screen: AlphaValue is already the previous fade's target, so
        // reversing a fade mid-flight would jump to it first.
        anim.From = NSNumber.FromFloat(layer.PresentationLayer?.Opacity ?? (float)view.AlphaValue);
        anim.To = NSNumber.FromDouble(to);
        anim.Duration = durationMs / 1000.0;
        anim.TimingFunction = CAMediaTimingFunction.FromName(easeOut ? CAMediaTimingFunction.EaseOut : CAMediaTimingFunction.EaseIn);
        NoAnim(() => view.AlphaValue = (nfloat)to);
        layer.AddAnimation(anim, "fade");
    }
}
