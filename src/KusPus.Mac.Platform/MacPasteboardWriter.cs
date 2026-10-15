using AppKit;
using Foundation;
using KusPus.Core.Platform;

namespace KusPus.Mac.Platform;

/// <summary>
/// <see cref="IClipboardWriter"/> over <c>NSPasteboard.generalPasteboard</c>. Must run on
/// the main thread (AppKit requirement); callers from <c>Task.Run</c> are marshalled.
///
/// Also declares the de-facto-standard clipboard-manager hints
/// <c>org.nspasteboard.TransientType</c> and <c>org.nspasteboard.ConcealedType</c>
/// (see nspasteboard.org) so Maccy/Paste/Alfred/Raycast skip transcripts — the
/// Mac counterpart of excluding from Windows Clipboard History.
/// </summary>
public sealed class MacPasteboardWriter : IClipboardWriter
{
    private const string TransientType = "org.nspasteboard.TransientType";
    private const string ConcealedType = "org.nspasteboard.ConcealedType";

    public void SetText(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (NSThread.IsMain)
        {
            Write(text);
        }
        else
        {
            NSApplication.SharedApplication.InvokeOnMainThread(() => Write(text));
        }
    }

    private static void Write(string text)
    {
        string stringType = NSPasteboardType.String.GetConstant()!;
        var pb = NSPasteboard.GeneralPasteboard;
        pb.ClearContents();
        pb.DeclareTypes([stringType, TransientType, ConcealedType], null);
        pb.SetStringForType(text, stringType);
        pb.SetStringForType(string.Empty, TransientType);
        pb.SetStringForType(string.Empty, ConcealedType);
    }
}
