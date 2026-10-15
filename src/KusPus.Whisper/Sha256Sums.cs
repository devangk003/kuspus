namespace KusPus.Whisper;

/// <summary>
/// Reads the <c>SHA256SUMS</c> manifest both payload scripts write
/// (<c>&lt;64 hex&gt;  &lt;file name&gt;</c> per line, <c>sha256sum</c>/<c>shasum -a 256</c> format).
/// On macOS the expected whisper hash is read from the copy sealed inside the signed
/// bundle rather than from a compile-time constant, because code signing rewrites
/// the binary after the constant was emitted.
/// </summary>
public static class Sha256Sums
{
    /// <summary>
    /// Returns the lower-case hash recorded for <paramref name="fileName"/> (matched on
    /// the last path segment, so <c>./whisper-cli</c> and <c>whisper-cli</c> both match),
    /// or <c>null</c> when the file doesn't exist or has no such entry.
    /// </summary>
    public static string? FindHash(string sha256sumsPath, string fileName)
    {
        ArgumentException.ThrowIfNullOrEmpty(sha256sumsPath);
        ArgumentException.ThrowIfNullOrEmpty(fileName);
        if (!File.Exists(sha256sumsPath))
        {
            return null;
        }
        foreach (var line in File.ReadLines(sha256sumsPath))
        {
            var hash = ParseLine(line, fileName);
            if (hash is not null)
            {
                return hash;
            }
        }
        return null;
    }

    /// <summary>Parses one manifest line; returns the hash if it names <paramref name="fileName"/>.</summary>
    public static string? ParseLine(string line, string fileName)
    {
        ArgumentNullException.ThrowIfNull(line);
        var trimmed = line.Trim();
        if (trimmed.Length < 66)
        {
            return null;
        }
        var hash = trimmed[..64];
        if (!IsHex(hash))
        {
            return null;
        }
        // Separator is two spaces (text) or " *" (binary mode); accept either.
        var rest = trimmed[64..].TrimStart(' ', '*');
        var name = rest.Replace('\\', '/');
        var lastSlash = name.LastIndexOf('/');
        if (lastSlash >= 0)
        {
            name = name[(lastSlash + 1)..];
        }
        return string.Equals(name, fileName, StringComparison.Ordinal) ? hash.ToLowerInvariant() : null;
    }

    private static bool IsHex(string s)
    {
        foreach (var c in s)
        {
            if (!Uri.IsHexDigit(c))
            {
                return false;
            }
        }
        return true;
    }
}
