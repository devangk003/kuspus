using System.Runtime.InteropServices;

namespace KusPus.Mac.Platform;

/// <summary>
/// Resolves which virtual keycode types a given character on the current keyboard layout.
/// CGEvent keycodes are physical positions: under Dvorak (and other layouts without a
/// "⌘ QWERTY" variant) kVK_ANSI_V is the key labelled "K", so a hard-coded ⌘ + 0x09
/// arrives as ⌘K. Text Input Sources must be queried on the main thread.
/// </summary>
internal static class MacKeyboardLayout
{
    private const string Carbon = "/System/Library/Frameworks/Carbon.framework/Carbon";
    private const string CoreFoundation = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";

    private const ushort KeyActionDisplay = 3;           // kUCKeyActionDisplay
    private const uint NoDeadKeys = 1;                   // kUCKeyTranslateNoDeadKeysMask
    private const ushort MaxKeyCode = 0x7F;
    // UCKeyTranslate takes (EventModifiers >> 8); cmdKey = 0x0100. Translating with ⌘ held
    // matters for layouts like "Dvorak – QWERTY ⌘" that switch to QWERTY under Command,
    // which is exactly what apps match shortcuts against.
    private const uint CommandModifierState = 0x0100 >> 8;

    [DllImport(Carbon)]
    private static extern IntPtr TISCopyCurrentKeyboardLayoutInputSource();

    [DllImport(Carbon)]
    private static extern IntPtr TISCopyCurrentASCIICapableKeyboardLayoutInputSource();

    [DllImport(Carbon)]
    private static extern IntPtr TISGetInputSourceProperty(IntPtr source, IntPtr key);

    [DllImport(Carbon)]
    private static extern byte LMGetKbdType();

    [DllImport(Carbon)]
    private static extern int UCKeyTranslate(
        IntPtr keyLayout, ushort virtualKeyCode, ushort keyAction, uint modifierKeyState,
        uint keyboardType, uint keyTranslateOptions, ref uint deadKeyState,
        nuint maxStringLength, out nuint actualStringLength, [Out] char[] unicodeString);

    [DllImport(CoreFoundation)]
    private static extern IntPtr CFDataGetBytePtr(IntPtr data);

    [DllImport(CoreFoundation)]
    private static extern void CFRelease(IntPtr cf);

    /// <summary>
    /// Keycode that produces <paramref name="ch"/> with ⌘ held on the current layout, or
    /// <paramref name="fallback"/> when it can't be determined (no layout data, e.g. some
    /// input methods, or the character isn't on the layout).
    /// </summary>
    public static ushort KeyCodeFor(char ch, ushort fallback)
    {
        try
        {
            var key = UnicodeKeyLayoutDataKey();
            if (key == IntPtr.Zero)
            {
                return fallback;
            }
            // Prefer the active layout; IMEs (Japanese, Chinese…) have no uchr data, so
            // fall back to the ASCII-capable layout the system pairs with them.
            return Find(TISCopyCurrentKeyboardLayoutInputSource(), key, ch)
                ?? Find(TISCopyCurrentASCIICapableKeyboardLayoutInputSource(), key, ch)
                ?? fallback;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return fallback;
        }
    }

    private static ushort? Find(IntPtr source, IntPtr key, char ch)
    {
        if (source == IntPtr.Zero)
        {
            return null;
        }
        try
        {
            var data = TISGetInputSourceProperty(source, key); // CFDataRef, not owned
            if (data == IntPtr.Zero)
            {
                return null;
            }
            var layout = CFDataGetBytePtr(data);
            if (layout == IntPtr.Zero)
            {
                return null;
            }
            uint kbdType = LMGetKbdType();
            var buf = new char[4];
            char target = char.ToLowerInvariant(ch);
            for (ushort code = 0; code <= MaxKeyCode; code++)
            {
                uint dead = 0;
                if (UCKeyTranslate(layout, code, KeyActionDisplay, CommandModifierState, kbdType, NoDeadKeys,
                        ref dead, (nuint)buf.Length, out var len, buf) == 0
                    && len == 1 && char.ToLowerInvariant(buf[0]) == target)
                {
                    return code;
                }
            }
            return null;
        }
        finally
        {
            CFRelease(source);
        }
    }

    private static IntPtr _layoutDataKey;

    // kTISPropertyUnicodeKeyLayoutData is an exported CFStringRef global, not a function.
    private static IntPtr UnicodeKeyLayoutDataKey()
    {
        if (_layoutDataKey != IntPtr.Zero)
        {
            return _layoutDataKey;
        }
        var lib = ObjCRuntime.Dlfcn.dlopen(Carbon, 0);
        if (lib == IntPtr.Zero)
        {
            return IntPtr.Zero;
        }
        _layoutDataKey = ObjCRuntime.Dlfcn.GetIntPtr(lib, "kTISPropertyUnicodeKeyLayoutData");
        return _layoutDataKey;
    }
}
