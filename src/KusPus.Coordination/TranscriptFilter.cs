using System.Text.RegularExpressions;

namespace KusPus.Coordination;

/// <summary>
/// Strips whisper.cpp's non-speech markers so silence never pastes
/// <c>[BLANK_AUDIO]</c> into the user's document. Whisper emits bracketed or
/// parenthesised tokens for noise/silence (<c>[BLANK_AUDIO]</c>, <c>[ Silence ]</c>,
/// <c>(silence)</c>, <c>[MUSIC]</c>, <c>(applause)</c>). Only a fixed allowlist of
/// marker words is removed — a spoken parenthetical such as "the API (version two)"
/// is left alone.
/// </summary>
public static partial class TranscriptFilter
{
    [GeneratedRegex(
        @"\s*[\[\(]\s*(?:BLANK_AUDIO|BLANK|silence|music|noise|background noise|inaudible|unintelligible|applause|laughter|laughs|typing|clicking|static)\s*[\]\)]\s*",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex Markers();

    // whisper-cli --output-txt writes one *segment* per line; segment breaks are timing
    // artefacts, not paragraphs, and a pasted newline can submit a chat message or run
    // a command in a terminal.
    [GeneratedRegex(@"\s*[\r\n]+\s*", RegexOptions.CultureInvariant)]
    private static partial Regex LineBreaks();

    /// <summary>Returns the trimmed text with non-speech markers removed; empty when nothing is left.</summary>
    public static string Clean(string transcript)
    {
        ArgumentNullException.ThrowIfNull(transcript);
        var cleaned = Markers().Replace(transcript, " ");
        cleaned = LineBreaks().Replace(cleaned.Trim(), " ");
        return cleaned;
    }
}
