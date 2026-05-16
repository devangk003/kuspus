#pragma warning disable CA1848 // LoggerMessage delegates — same low-volume rationale as other App-layer logging
#pragma warning disable CA1873

using System.Net.Http;
using KusPus.Core.Settings;
using KusPus.Core.Telemetry;
using KusPus.Persistence;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Sentry;

namespace KusPus.App;

/// <summary>
/// Opt-in crash reporter per TECH_SPEC §19 + PRD §10.3. Wraps the Sentry SDK in a
/// lifecycle that respects two toggles:
/// <list type="bullet">
///   <item><see cref="PrivacySettings.CrashReportsOptIn"/> — user opt-in.</item>
///   <item><see cref="PrivacySettings.OfflineMode"/> — global killswitch overrides
///   opt-in.</item>
/// </list>
/// Sentry is only initialised when <c>opted-in &amp;&amp; !offline</c>. Either toggle
/// flipping at runtime tears the SDK down via <see cref="SentrySdk.Close"/> (§19
/// "Forced disable") and a later re-enable re-initialises it.
///
/// The DSN is read from the <c>KUSPUS_SENTRY_DSN</c> env var, falling back to
/// <see cref="DefaultDsn"/> which targets the KusPus EU Sentry project. DSNs are
/// not secrets — they identify the project, not authenticate to it.
///
/// Sentry's internal HTTP transport is routed through <see cref="EgressAllowlistHandler"/>
/// so PRD §10.2 ("All HttpClient instances in the codebase route through a single
/// factory") holds for Sentry too: a runtime Offline-Mode flip blocks in-flight
/// envelope uploads in addition to tearing the SDK down, and any future Sentry
/// SDK update that pings a new host gets caught by the allowlist instead of
/// silently exfiltrating.
///
/// <see cref="BeforeSend"/> applies <see cref="CrashScrubber"/>: drops any event
/// whose Tags/Extra contains a sensitive key, and rewrites paths +
/// <see cref="Environment.UserName"/> occurrences in stack frames, exception
/// messages, the event message, and breadcrumbs.
/// </summary>
public sealed class CrashReporter : IDisposable
{
    private const string DsnEnvVar = "KUSPUS_SENTRY_DSN";

    // Public-by-design Sentry DSN for the project's EU ingest. Override locally
    // via the KUSPUS_SENTRY_DSN env var when testing against a different project.
    internal const string DefaultDsn =
        "https://4f7006d798a746a1009972022f06cd67@o4511400964849664.ingest.de.sentry.io/4511400971010128";

    private readonly IPrefsStore _prefs;
    private readonly ILogger<CrashReporter> _logger;
    private readonly ILoggerFactory? _loggerFactory;
    private readonly string _dsn;
    private IDisposable? _prefsSubscription;
    private IDisposable? _sdkSession;
    private bool _isActive;

    public CrashReporter(IPrefsStore prefs, ILoggerFactory? loggerFactory = null)
    {
        _prefs = prefs;
        _loggerFactory = loggerFactory;
        _logger = loggerFactory?.CreateLogger<CrashReporter>() ?? NullLogger<CrashReporter>.Instance;
        _dsn = Environment.GetEnvironmentVariable(DsnEnvVar) ?? DefaultDsn;
    }

    public bool IsActive => _isActive;

    /// <summary>
    /// Evaluates current settings, initialises Sentry if needed, and starts watching
    /// <see cref="IPrefsStore.Changes"/> so later toggle flips re-evaluate.
    /// </summary>
    public void Start()
    {
        ApplySettings(_prefs.Current);
        _prefsSubscription = _prefs.Changes.Subscribe(ApplySettings);
    }

    public void Dispose()
    {
        _prefsSubscription?.Dispose();
        _prefsSubscription = null;
        ShutdownSdk();
    }

    private void ApplySettings(AppSettings s)
    {
        bool shouldRun = s.Privacy.CrashReportsOptIn && !s.Privacy.OfflineMode;
        if (shouldRun && !_isActive)
        {
            InitSdk();
        }
        else if (!shouldRun && _isActive)
        {
            ShutdownSdk();
        }
    }

    private void InitSdk()
    {
        try
        {
            _sdkSession = SentrySdk.Init(o =>
            {
                o.Dsn = _dsn;
                o.AutoSessionTracking = false;
                o.SendDefaultPii = false;
                o.AttachStacktrace = true;
                o.MaxBreadcrumbs = 20;
                // Route Sentry's internal HTTP through our egress chokepoint so
                // PRD §10.2 holds for SDK uploads too. A fresh handler per call —
                // DelegatingHandler is single-use; Sentry can request a new instance
                // when it rebuilds its transport.
                o.CreateHttpMessageHandler = () =>
                    new EgressAllowlistHandler(
                        _prefs,
                        _loggerFactory?.CreateLogger<EgressAllowlistHandler>())
                    {
                        InnerHandler = new SocketsHttpHandler(),
                    };
                o.SetBeforeSend(BeforeSend);
                // SentryEvent.Breadcrumbs is read-only from inside BeforeSend, so
                // scrub each breadcrumb at the moment it's added to the scope.
                o.SetBeforeBreadcrumb((bc, _) => ScrubBreadcrumb(bc));
            });
            _isActive = true;
            _logger.LogInformation("Sentry crash reporter initialised.");
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            // Sentry surfaces DSN parsing / re-init errors as these two types.
            // Treat as non-fatal: app continues, crash reports just don't ship.
            // Other exception types are propagated — they likely indicate a real
            // programming error (e.g., misconfigured options).
            _logger.LogWarning(ex, "Sentry init failed.");
            _isActive = false;
            _sdkSession = null;
        }
    }

    private void ShutdownSdk()
    {
        if (!_isActive)
        {
            return;
        }
        _sdkSession?.Dispose();
        _sdkSession = null;
        SentrySdk.Close();
        _isActive = false;
        _logger.LogInformation("Sentry crash reporter shut down.");
    }

    /// <summary>
    /// Drops events that carry sensitive keys, then rewrites user-profile paths
    /// and <see cref="Environment.UserName"/> across every free-form string in the
    /// event. Returns <c>null</c> to drop. <c>internal</c> for direct testing.
    /// </summary>
    internal static SentryEvent? BeforeSend(SentryEvent evt, SentryHint _)
    {
        ArgumentNullException.ThrowIfNull(evt);

        if (CrashScrubber.ContainsAnySensitiveKey(evt.Tags.Keys)
            || CrashScrubber.ContainsAnySensitiveKey(evt.Extra.Keys))
        {
            return null;
        }

        evt.Message = ScrubMessage(evt.Message);

        if (evt.SentryExceptions is not null)
        {
            foreach (var ex in evt.SentryExceptions)
            {
                ex.Value = CrashScrubber.ScrubString(ex.Value);
                if (ex.Stacktrace is not null)
                {
                    foreach (var frame in ex.Stacktrace.Frames)
                    {
                        frame.AbsolutePath = CrashScrubber.ScrubPath(frame.AbsolutePath);
                        frame.FileName = CrashScrubber.ScrubPath(frame.FileName);
                    }
                }
            }
        }

        // Breadcrumbs were already scrubbed in SetBeforeBreadcrumb when added.
        return evt;
    }

    private static SentryMessage? ScrubMessage(SentryMessage? m)
    {
        if (m is null)
        {
            return null;
        }
        return new SentryMessage
        {
            Message = CrashScrubber.ScrubString(m.Message),
            Formatted = CrashScrubber.ScrubString(m.Formatted),
            Params = m.Params,
        };
    }

    /// <summary>
    /// Returns a scrubbed copy of <paramref name="b"/>. Returns <c>null</c> to drop
    /// the breadcrumb (currently never — we only scrub, we don't filter). Mirrors
    /// the shape Sentry expects from <c>SetBeforeBreadcrumb</c>.
    ///
    /// Sentry 5.0's public <see cref="Breadcrumb"/> constructor sets the timestamp
    /// to "now" — fine because this scrubber runs at breadcrumb-creation time, so
    /// "now" is within microseconds of the original timestamp.
    /// </summary>
    internal static Breadcrumb? ScrubBreadcrumb(Breadcrumb b)
    {
        Dictionary<string, string>? data = null;
        if (b.Data is not null && b.Data.Count > 0)
        {
            data = new Dictionary<string, string>(b.Data.Count, StringComparer.Ordinal);
            foreach (var kv in b.Data)
            {
                data[kv.Key] = CrashScrubber.ScrubString(kv.Value) ?? string.Empty;
            }
        }
        return new Breadcrumb(
            message: CrashScrubber.ScrubString(b.Message) ?? string.Empty,
            type: b.Type ?? "default",
            data: data,
            category: b.Category,
            level: b.Level);
    }
}
