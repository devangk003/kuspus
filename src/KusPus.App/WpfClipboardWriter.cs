using KusPus.Native;

namespace KusPus.App;

/// <summary>
/// WPF-backed <see cref="IClipboardWriter"/>. <see cref="System.Windows.Clipboard.SetText(string)"/>
/// requires an STA thread; this writer marshals to the dispatcher so callers can
/// invoke from any thread (including thread-pool callers from <c>Task.Run</c> in
/// <see cref="AppCoordinator"/>). PasteEngine handles the 3× retry-on-ExternalException
/// loop per TECH_SPEC §16 step 1.
/// </summary>
internal sealed class WpfClipboardWriter : IClipboardWriter
{
    public void SetText(string text)
    {
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess())
        {
            // Already on the UI/STA thread (or no app context — testing).
            System.Windows.Clipboard.SetText(text);
        }
        else
        {
            // Marshal to the UI thread; rethrow inner exception so PasteEngine's
            // ExternalException-retry loop sees the real cause.
            dispatcher.Invoke(() => System.Windows.Clipboard.SetText(text));
        }
    }
}
