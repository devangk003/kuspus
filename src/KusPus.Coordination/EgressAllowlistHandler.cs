#pragma warning disable CA1848 // LoggerMessage delegates — same rationale as the other App-layer logging
#pragma warning disable CA1873

using System.Net.Http;
using KusPus.Core.Networking;
using KusPus.Persistence;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace KusPus.Coordination;

/// <summary>
/// Single egress chokepoint per PRD §10.2 + TECH_SPEC §19. Every outbound HTTP request
/// in KusPus passes through this handler before reaching the network, which consults
/// <see cref="EgressPolicy"/> with the current <see cref="IPrefsStore"/> snapshot:
/// <list type="bullet">
///   <item>Offline Mode ON → block everything.</item>
///   <item>Allowlisted host (huggingface.co, ingest.sentry.io) → forward.</item>
///   <item>Anything else → block.</item>
/// </list>
///
/// Blocks materialise as <see cref="HttpRequestException"/>. Existing callers
/// (<c>ModelManager.DownloadAsync</c>) already catch this and translate it to a
/// user-visible "Network blocked" message via <c>Result.Fail</c>.
///
/// Redirects are followed here, not by the inner handler, so every hop is checked
/// against the policy — otherwise an allowlisted URL could bounce the request to any
/// host. The inner handler must be created with <c>AllowAutoRedirect = false</c>
/// (see <see cref="CreateInnerHandler"/>).
///
/// The blocked-host log line carries only scheme + host (no path, no query) so URLs
/// containing user-identifying data don't end up in <c>kuspus-*.log</c>.
/// </summary>
public sealed class EgressAllowlistHandler : DelegatingHandler
{
    private readonly IPrefsStore _prefs;
    private readonly ILogger<EgressAllowlistHandler> _logger;

    public EgressAllowlistHandler(IPrefsStore prefs, ILogger<EgressAllowlistHandler>? logger = null)
    {
        _prefs = prefs;
        _logger = logger ?? NullLogger<EgressAllowlistHandler>.Instance;
    }

    private const int MaxRedirects = 10;

    /// <summary>Inner handler for this chokepoint: redirects are followed (and checked) by us.</summary>
    public static SocketsHttpHandler CreateInnerHandler() => new() { AllowAutoRedirect = false };

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var current = request;
        for (int hop = 0; ; hop++)
        {
            var uri = current.RequestUri
                ?? throw new InvalidOperationException("HTTP request has no RequestUri.");
            EnsureAllowed(uri);

            var response = await base.SendAsync(current, cancellationToken).ConfigureAwait(false);
            if (!IsRedirect(response) || response.Headers.Location is null)
            {
                return response;
            }
            if (hop >= MaxRedirects)
            {
                response.Dispose();
                throw new HttpRequestException($"Too many redirects (>{MaxRedirects}).");
            }

            var next = response.Headers.Location.IsAbsoluteUri
                ? response.Headers.Location
                : new Uri(uri, response.Headers.Location);
            response.Dispose();
            current = CloneForRedirect(current, next);
        }
    }

    private static bool IsRedirect(HttpResponseMessage r) => (int)r.StatusCode is 301 or 302 or 303 or 307 or 308;

    private static HttpRequestMessage CloneForRedirect(HttpRequestMessage original, Uri target)
    {
        // Only body-less GET/HEAD requests are redirected in practice (downloads); keep
        // the method and headers (Range, Accept-Encoding) so resume keeps working.
        var clone = new HttpRequestMessage(original.Method, target) { Version = original.Version };
        foreach (var header in original.Headers)
        {
            // Never forward credentials to another origin.
            if (string.Equals(header.Key, "Authorization", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            clone.Headers.TryAddWithoutValidation(header.Key, header.Value);
        }
        return clone;
    }

    private void EnsureAllowed(Uri uri)
    {
        var s = _prefs.Current;
        if (!EgressPolicy.IsAllowed(s.Privacy.OfflineMode, s.Privacy.CrashReportsOptIn, uri))
        {
            var reason = s.Privacy.OfflineMode
                ? "Offline Mode is on"
                : "host is not in the egress allowlist";
            _logger.LogWarning(
                "Blocked egress to {Scheme}://{Host}/ — {Reason}.",
                uri.Scheme, uri.Host, reason);
            throw new HttpRequestException($"Network blocked — {reason}.");
        }
    }
}
