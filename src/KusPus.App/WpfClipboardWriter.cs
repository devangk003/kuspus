using KusPus.Native;

namespace KusPus.App;

/// <summary>
/// WPF-backed <see cref="IClipboardWriter"/>. PasteEngine handles the 3× retry-on-
/// ExternalException loop per TECH_SPEC §16 step 1 — this writer just calls
/// <see cref="System.Windows.Clipboard.SetText(string)"/> once and lets the
/// exception propagate so PasteEngine sees the retryable signal.
/// </summary>
internal sealed class WpfClipboardWriter : IClipboardWriter
{
    public void SetText(string text)
    {
        System.Windows.Clipboard.SetText(text);
    }
}
