using AppKit;
using CoreGraphics;
using Foundation;

namespace KusPus.Mac.App;

/// <summary>
/// The Settings window's single layout system (System Settings pattern): a section is a
/// title plus one grouped card; a card is a stack of rows separated by inset hairlines;
/// a row is title + optional secondary line on the left and one control on the right.
///
/// Grid: 8 pt base, 4 for tight pairs. Row padding 16 h / 12 v, min height 44, card radius
/// 10, separators inset 16. Colour: neutral surfaces; mint only for the primary action,
/// selection and brand; status always as icon + text.
/// </summary>
internal static class Settings
{
    public const double RowPadX = 16;
    public const double RowPadY = 12;
    public const double RowMinHeight = 44;
    public const double CardRadius = 10;
    public const double SectionGap = 32;
    public const double HeaderGap = 8;

    /// <summary>Section: Title-role header, 8 pt, the group(s); 32 pt after.</summary>
    public static NSStackView Section(string? title, params NSView[] content)
    {
        var stack = UI.VStack(8);
        if (title is not null)
        {
            var header = UI.Label(title, Fonts.Title, "PrimaryText");
            stack.AddArrangedSubview(header);
            stack.SetCustomSpacing((nfloat)HeaderGap, header);
        }
        foreach (var c in content)
        {
            stack.AddArrangedSubview(c);
            c.WidthAnchor.ConstraintEqualTo(stack.WidthAnchor).Active = true;
        }
        stack.EdgeInsets = new NSEdgeInsets(0, 0, (nfloat)SectionGap, 0);
        return stack;
    }

    /// <summary>Section header with a secondary caption line under it (e.g. "NVIDIA Parakeet" + licence/runtime).</summary>
    public static NSStackView Section(string title, string caption, params NSView[] content)
    {
        var header = UI.VStack(4,
            UI.Label(title, Fonts.Title, "PrimaryText"),
            UI.Label(caption, Fonts.Supporting, "MutedText"));
        var stack = UI.VStack(8, header);
        stack.SetCustomSpacing((nfloat)HeaderGap, header);
        foreach (var c in content)
        {
            stack.AddArrangedSubview(c);
            c.WidthAnchor.ConstraintEqualTo(stack.WidthAnchor).Active = true;
        }
        stack.EdgeInsets = new NSEdgeInsets(0, 0, (nfloat)SectionGap, 0);
        return stack;
    }

    /// <summary>Page intro: one Body line + one Supporting line, no card. Used at the top of a tab.</summary>
    public static NSStackView Intro(string lead, NSView? detail = null)
    {
        var stack = UI.VStack(4, UI.Paragraph(lead, Fonts.Body, "PrimaryText", lineSpacing: 2));
        if (detail is not null)
        {
            stack.AddArrangedSubview(detail);
        }
        foreach (var v in stack.ArrangedSubviews)
        {
            v.WidthAnchor.ConstraintEqualTo(stack.WidthAnchor).Active = true;
        }
        stack.EdgeInsets = new NSEdgeInsets(0, 0, 24, 0);
        return stack;
    }

    /// <summary>One grouped card: rows stacked edge to edge; each row draws its own top separator.</summary>
    public static CardView Group(params SettingsRow[] rows)
    {
        var stack = UI.VStack(0);
        for (int i = 0; i < rows.Length; i++)
        {
            rows[i].ShowSeparator = i > 0;
            stack.AddArrangedSubview(rows[i]);
            rows[i].WidthAnchor.ConstraintEqualTo(stack.WidthAnchor).Active = true;
        }
        var card = new CardView(stack, "Surface", "BorderSubtle", radius: CardRadius, padX: 0, padY: 0);
        card.Layer!.MasksToBounds = true;
        return card;
    }

    /// <summary>Status as icon + text (never colour alone): ok / warning / error / neutral.</summary>
    public static NSStackView Status(StatusKind kind, string text)
    {
        var (symbol, key) = kind switch
        {
            StatusKind.Ok => ("checkmark.circle.fill", "MintText"),
            StatusKind.Warning => ("exclamationmark.triangle.fill", "WarningText"),
            StatusKind.Error => ("xmark.octagon.fill", "ErrorText"),
            _ => ("circle", "MutedText"),
        };
        var icon = UI.SymbolView(symbol, 12, key);
        icon.AccessibilityElement = false; // the text says it
        var label = UI.Label(text, Fonts.Medium(12), kind == StatusKind.Neutral ? "SecondaryText" : key);
        var stack = UI.HStack(4, icon, label);
        stack.SetContentHuggingPriorityForOrientation(1000, NSLayoutConstraintOrientation.Horizontal);
        return stack;
    }

    /// <summary>Small sentence-case badge. Accent = text-safe mint on a faint tint; neutral = grey.</summary>
    public static CardView Badge(string text, bool accent)
    {
        var label = UI.Label(text, Fonts.Caption, accent ? "MintText" : "SecondaryText");
        var badge = new CardView(label, accent ? "MintTint" : "SurfaceInput", accent ? "MintBorder" : "BorderSubtle", radius: 4, padX: 8, padY: 2);
        badge.SetContentHuggingPriorityForOrientation(1000, NSLayoutConstraintOrientation.Horizontal);
        return badge;
    }

    /// <summary>Text link (borderless button) in the text-safe mint, with an optional trailing ↗.</summary>
    public static NSButton Link(string title, Action action, bool external = false)
    {
        var b = UI.Button(external ? title + " ↗" : title, UI.ButtonKind.Ghost, action);
        b.Font = Fonts.Supporting;
        b.ContentTintColor = ThemeColors.Get("MintText");
        AxLabel(b, title + (external ? ", opens in browser" : string.Empty));
        return b;
    }

    /// <summary>
    /// Sets the VoiceOver label. NSButton re-declares <c>AccessibilityLabel</c> read-only
    /// (NSAccessibilityButton protocol), so set it through the NSView setter.
    /// </summary>
    public static void AxLabel(NSView view, string label) => view.AccessibilityLabel = label;

    /// <summary>"Details" disclosure: chevron + label, exposes expanded state to VoiceOver.</summary>
    public static NSButton Disclosure(bool expanded, Action toggle)
    {
        var b = UI.Button(expanded ? "Hide details" : "Details", UI.ButtonKind.Ghost, toggle);
        b.Font = Fonts.Supporting;
        b.Image = UI.Symbol(expanded ? "chevron.down" : "chevron.right", 10);
        b.ImagePosition = NSCellImagePosition.ImageLeading;
        b.ContentTintColor = ThemeColors.Get("SecondaryText");
        b.AccessibilityExpanded = expanded;
        return b;
    }

    /// <summary>Destructive row action: bordered button with error-coloured text (the confirm sheet carries the weight).</summary>
    public static NSButton Destructive(string title, Action action)
    {
        var b = UI.Button(title, UI.ButtonKind.Secondary, action);
        // Bordered buttons ignore ContentTintColor for the title; colour the title itself.
        var style = new NSMutableParagraphStyle { Alignment = NSTextAlignment.Center };
        b.AttributedTitle = new NSAttributedString(title, new NSStringAttributes
        {
            Font = b.Font,
            ForegroundColor = ThemeColors.Get("ErrorText"),
            ParagraphStyle = style,
        });
        return b;
    }
}

internal enum StatusKind { Neutral, Ok, Warning, Error }

/// <summary>
/// One settings row: [optional leading view] title + secondary on the left, one trailing
/// control on the right. 16/12 padding, 44 pt minimum. The trailing control inherits the
/// row title as its accessibility label so toggles and popups announce what they change.
/// Extra content (e.g. an expanded details block) goes under the title column via <see cref="AddBelow"/>.
/// </summary>
internal sealed class SettingsRow : FlippedView
{
    private readonly NSView _separator;
    private readonly NSStackView _textColumn;

    public SettingsRow(string title, string? secondary = null, NSView? trailing = null, NSView? titleAccessory = null)
        : this(BuildTitle(title, titleAccessory), secondary is null ? null : UI.Paragraph(secondary, Fonts.Supporting, "MutedText", lineSpacing: 2), trailing, title)
    {
    }

    public SettingsRow(NSView titleView, NSView? secondaryView, NSView? trailing, string? accessibilityTitle = null)
    {
        TranslatesAutoresizingMaskIntoConstraints = false;
        HeightAnchor.ConstraintGreaterThanOrEqualTo((nfloat)Settings.RowMinHeight).Active = true;

        _separator = new NSView { WantsLayer = true, TranslatesAutoresizingMaskIntoConstraints = false };
        _separator.Layer!.BackgroundColor = ThemeColors.Cg("Separator");
        AddSubview(_separator);

        _textColumn = UI.VStack(4, titleView);
        if (secondaryView is not null)
        {
            _textColumn.AddArrangedSubview(secondaryView);
        }
        foreach (var v in _textColumn.ArrangedSubviews)
        {
            v.WidthAnchor.ConstraintLessThanOrEqualTo(_textColumn.WidthAnchor).Active = true;
        }

        var row = trailing is null ? UI.HStack(16, _textColumn) : UI.Row(_textColumn, trailing);
        row.Alignment = NSLayoutAttribute.CenterY;
        AddSubview(row);
        Secondary = secondaryView as NSTextField;
        if (trailing is not null && accessibilityTitle is not null && string.IsNullOrEmpty(trailing.AccessibilityLabel))
        {
            trailing.AccessibilityLabel = accessibilityTitle;
        }

        NSLayoutConstraint.ActivateConstraints(
        [
            _separator.LeadingAnchor.ConstraintEqualTo(LeadingAnchor, (nfloat)Settings.RowPadX),
            _separator.TrailingAnchor.ConstraintEqualTo(TrailingAnchor, (nfloat)(-Settings.RowPadX)),
            _separator.TopAnchor.ConstraintEqualTo(TopAnchor),
            _separator.HeightAnchor.ConstraintEqualTo(1),
            row.LeadingAnchor.ConstraintEqualTo(LeadingAnchor, (nfloat)Settings.RowPadX),
            row.TrailingAnchor.ConstraintEqualTo(TrailingAnchor, (nfloat)(-Settings.RowPadX)),
            row.TopAnchor.ConstraintGreaterThanOrEqualTo(TopAnchor, (nfloat)Settings.RowPadY),
            row.BottomAnchor.ConstraintLessThanOrEqualTo(BottomAnchor, (nfloat)(-Settings.RowPadY)),
            row.CenterYAnchor.ConstraintEqualTo(CenterYAnchor),
        ]);
    }

    /// <summary>A row whose whole content is a custom view (padded like every other row).</summary>
    public static SettingsRow Custom(NSView content) => new(content);

    private SettingsRow(NSView content)
    {
        TranslatesAutoresizingMaskIntoConstraints = false;
        HeightAnchor.ConstraintGreaterThanOrEqualTo((nfloat)Settings.RowMinHeight).Active = true;
        _separator = new NSView { WantsLayer = true, TranslatesAutoresizingMaskIntoConstraints = false };
        _separator.Layer!.BackgroundColor = ThemeColors.Cg("Separator");
        AddSubview(_separator);
        _textColumn = UI.VStack(0);
        content.TranslatesAutoresizingMaskIntoConstraints = false;
        AddSubview(content);
        NSLayoutConstraint.ActivateConstraints(
        [
            _separator.LeadingAnchor.ConstraintEqualTo(LeadingAnchor, (nfloat)Settings.RowPadX),
            _separator.TrailingAnchor.ConstraintEqualTo(TrailingAnchor, (nfloat)(-Settings.RowPadX)),
            _separator.TopAnchor.ConstraintEqualTo(TopAnchor),
            _separator.HeightAnchor.ConstraintEqualTo(1),
            content.LeadingAnchor.ConstraintEqualTo(LeadingAnchor, (nfloat)Settings.RowPadX),
            content.TrailingAnchor.ConstraintEqualTo(TrailingAnchor, (nfloat)(-Settings.RowPadX)),
            content.TopAnchor.ConstraintEqualTo(TopAnchor, (nfloat)Settings.RowPadY),
            content.BottomAnchor.ConstraintEqualTo(BottomAnchor, (nfloat)(-Settings.RowPadY)),
        ]);
    }

    /// <summary>The secondary label when it was built from a string (so callers can update it).</summary>
    public NSTextField? Secondary { get; }

    public bool ShowSeparator
    {
        get => !_separator.Hidden;
        set => _separator.Hidden = !value;
    }

    /// <summary>Adds full-width content under the title/secondary lines (details, results).</summary>
    public void AddBelow(NSView view)
    {
        _textColumn.AddArrangedSubview(view);
        view.WidthAnchor.ConstraintEqualTo(_textColumn.WidthAnchor).Active = true;
    }

    private static NSView BuildTitle(string title, NSView? accessory)
    {
        var label = UI.Label(title, Fonts.RowTitle, "PrimaryText");
        if (accessory is null)
        {
            return label;
        }
        var stack = UI.HStack(8, label, accessory);
        return stack;
    }
}
