namespace KusPus.Core.Networking;

/// <summary>
/// Pure decision function for the v1 egress allowlist. See PRD §10.2
/// ("Egress allowlist — enforced in code") and TECH_SPEC §19. Kept in
/// <c>KusPus.Core</c> so the policy is unit-testable independent of
/// <c>HttpClient</c> / <c>DelegatingHandler</c> plumbing.
///
/// Rules, in evaluation order:
/// <list type="number">
///   <item>Offline Mode ON → block everything.</item>
///   <item>Non-HTTPS → block (HTTP allowlist would be meaningless).</item>
///   <item>Host suffix <c>huggingface.co</c> → allow (model downloads).</item>
///   <item>Sentry ingest host → allow only if crash reports opted in.</item>
///   <item>Otherwise → block.</item>
/// </list>
///
/// A "host suffix match" means an exact match OR <c>host.EndsWith("." + suffix)</c>.
/// PRD §10.2 lists <c>https://ingest.sentry.io/</c>; Sentry's actual DSN host can
/// be either the original US ingest (<c>o&lt;org&gt;.ingest.sentry.io</c>) or a
/// regional ingest (<c>o&lt;org&gt;.ingest.&lt;region&gt;.sentry.io</c> — e.g. <c>de</c>,
/// <c>us</c>). <see cref="IsSentryIngestHost"/> accepts both by requiring a
/// <c>*.sentry.io</c> suffix with an <c>"ingest"</c> label somewhere in the host —
/// covers every documented region without enumerating them.
/// </summary>
public static class EgressPolicy
{
    public static bool IsAllowed(bool offlineMode, bool crashReportsOptedIn, Uri uri)
    {
        ArgumentNullException.ThrowIfNull(uri);

        if (offlineMode)
        {
            return false;
        }
        if (!string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.Ordinal))
        {
            return false;
        }

        var host = uri.Host;
        if (HostMatches(host, "huggingface.co"))
        {
            return true;
        }
        if (IsSentryIngestHost(host))
        {
            return crashReportsOptedIn;
        }
        return false;
    }

    private static bool HostMatches(string host, string suffix) =>
        string.Equals(host, suffix, StringComparison.OrdinalIgnoreCase) ||
        host.EndsWith("." + suffix, StringComparison.OrdinalIgnoreCase);

    private static bool IsSentryIngestHost(string host)
    {
        if (!host.EndsWith(".sentry.io", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }
        foreach (var label in host.Split('.', StringSplitOptions.RemoveEmptyEntries))
        {
            if (string.Equals(label, "ingest", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }
        return false;
    }
}
