#pragma warning disable CA1848 // LoggerMessage delegates — same rationale as the other App-layer logging
#pragma warning disable CA1873

using System.Net.Http;
using KusPus.Core.Networking;
using KusPus.Persistence;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace KusPus.App;

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

    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var uri = request.RequestUri
            ?? throw new InvalidOperationException("HTTP request has no RequestUri.");

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

        return base.SendAsync(request, cancellationToken);
    }
}
