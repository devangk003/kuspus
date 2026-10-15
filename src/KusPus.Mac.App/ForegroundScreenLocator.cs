using System.Runtime.InteropServices;
using AppKit;
using CoreGraphics;
using Foundation;
using ObjCRuntime;

namespace KusPus.Mac.App;

/// <summary>
/// Finds the screen the user is working on so the pill can follow the active app
/// across monitors (the Windows app uses <c>MonitorFromWindow(GetForegroundWindow())</c>).
/// macOS has no cross-process window handle, so we read the frontmost app's on-screen
/// window bounds via <c>CGWindowListCopyWindowInfo</c> — bounds and owner pid are
/// readable without Screen Recording permission (only window <em>names</em> are
/// gated). Falls back to the screen under the mouse cursor.
/// </summary>
internal static class ForegroundScreenLocator
{
    private const string CoreGraphicsLib = "/System/Library/Frameworks/CoreGraphics.framework/CoreGraphics";

    [DllImport(CoreGraphicsLib)]
    private static extern IntPtr CGWindowListCopyWindowInfo(CGWindowListOption option, uint relativeToWindow);

    [DllImport("/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation")]
    private static extern void CFRelease(IntPtr cf);

    public static NSScreen ScreenForForegroundApp()
    {
        var app = NSWorkspace.SharedWorkspace.FrontmostApplication;
        if (app is not null && app.ProcessIdentifier != Environment.ProcessId)
        {
            var screen = ScreenForPid(app.ProcessIdentifier);
            if (screen is not null)
            {
                return screen;
            }
        }
        return ScreenUnderMouse();
    }

    public static NSScreen ScreenUnderMouse()
    {
        var mouse = NSEvent.CurrentMouseLocation;
        foreach (var s in NSScreen.Screens)
        {
            // Inclusive edges: a cursor pushed into the menu bar sits at y == MaxY, which
            // CGRect.Contains excludes — that fell through to MainScreen on multi-monitor.
            var f = s.Frame;
            if (mouse.X >= f.X && mouse.X <= f.GetMaxX() && mouse.Y >= f.Y && mouse.Y <= f.GetMaxY())
            {
                return s;
            }
        }
        return NSScreen.MainScreen ?? NSScreen.Screens[0];
    }

    /// <summary>Stable per-screen key (<c>NSScreenNumber</c>), used for the remembered pill positions.</summary>
    public static string ScreenKey(NSScreen screen)
    {
        using var key = new NSString("NSScreenNumber");
        var number = screen.DeviceDescription?[key];
        return number?.ToString() ?? screen.LocalizedName;
    }

    private static NSScreen? ScreenForPid(int pid)
    {
        var list = CGWindowListCopyWindowInfo(
            CGWindowListOption.OnScreenOnly | CGWindowListOption.ExcludeDesktopElements, 0);
        if (list == IntPtr.Zero)
        {
            return null;
        }
        try
        {
            var windows = Runtime.GetNSObject<NSArray>(list);
            if (windows is null)
            {
                return null;
            }
            // Main-display height for the CG (y-down) → Cocoa (y-up) flip.
            var primary = NSScreen.Screens[0].Frame;
            for (nuint i = 0; i < windows.Count; i++)
            {
                var info = windows.GetItem<NSDictionary>(i);
                if (info is null)
                {
                    continue;
                }
                if (Int(info, "kCGWindowOwnerPID") != pid || Int(info, "kCGWindowLayer") != 0)
                {
                    continue;
                }
                using var boundsKey = new NSString("kCGWindowBounds");
                if (info[boundsKey] is not NSDictionary bounds)
                {
                    continue;
                }
                double x = Dbl(bounds, "X"), y = Dbl(bounds, "Y"), w = Dbl(bounds, "Width"), h = Dbl(bounds, "Height");
                if (w < 50 || h < 50)
                {
                    continue; // menu bar extras, tooltips
                }
                var center = new CGPoint(x + (w / 2), primary.Height - (y + (h / 2)));
                foreach (var s in NSScreen.Screens)
                {
                    if (s.Frame.Contains(center))
                    {
                        return s;
                    }
                }
            }
            return null;
        }
        finally
        {
            CFRelease(list);
        }
    }

    private static int Int(NSDictionary d, string key)
    {
        using var k = new NSString(key);
        return d[k] is NSNumber n ? n.Int32Value : -1;
    }

    private static double Dbl(NSDictionary d, string key)
    {
        using var k = new NSString(key);
        return d[k] is NSNumber n ? n.DoubleValue : 0;
    }
}
