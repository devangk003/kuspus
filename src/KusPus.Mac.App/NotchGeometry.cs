using AppKit;
using CoreGraphics;

namespace KusPus.Mac.App;

/// <summary>
/// The notch rectangle of a screen, in Cocoa (bottom-left origin) coordinates.
/// <see cref="IsReal"/> is false for screens without a hardware notch, where the island
/// hangs a "virtual notch" tab from the top-centre of the menu bar instead.
/// </summary>
/// <see cref="OverlapsContent"/> is true for a virtual tab on a screen with no visible menu
/// bar (auto-hidden, full-screen space, secondary display): the tab then sits over app
/// content rather than over menu-bar space.
internal readonly record struct NotchInfo(CGRect Rect, bool IsReal, bool OverlapsContent = false)
{
    public double Width => Rect.Width;
    public double Height => Rect.Height;
    public double MidX => Rect.GetMidX();
    public double MaxY => Rect.GetMaxY();
}

/// <summary>
/// Notch measurement, following The Boring Notch's <c>getClosedNotchSize</c>:
/// width from the auxiliary top-left/right areas (+4 pt so the island overlaps the
/// hardware edges and never shows a hairline), height from the safe-area inset. Screens
/// without a notch get a 185 pt tab as tall as their menu bar.
/// </summary>
internal static class NotchGeometry
{
    private const double VirtualWidth = 185;
    private const double VirtualFallbackHeight = 32;
    private const double EdgeOverlap = 4;

    public static NotchInfo For(NSScreen screen)
    {
        // Always the full frame: VisibleFrame excludes the menu-bar strip the notch lives in.
        var frame = screen.Frame;
        double top = screen.SafeAreaInsets.Top;
        bool real = top > 0;
        bool overlapsContent = false;

        double width;
        double height;
        if (real)
        {
            var left = screen.AuxiliaryTopLeftArea;
            var right = screen.AuxiliaryTopRightArea;
            width = frame.Width - left.Width - right.Width + EdgeOverlap;
            height = top;
            if (width <= EdgeOverlap || width > frame.Width / 2)
            {
                // Auxiliary areas unavailable on this SDK/screen combo — fall back.
                width = VirtualWidth;
            }
        }
        else
        {
            width = VirtualWidth;
            height = frame.GetMaxY() - screen.VisibleFrame.GetMaxY();
            if (height <= 0)
            {
                height = VirtualFallbackHeight;
                overlapsContent = true;
            }
        }

        var rect = new CGRect(frame.GetMidX() - (width / 2), frame.GetMaxY() - height, width, height);
        return new NotchInfo(rect, real, overlapsContent);
    }
}
