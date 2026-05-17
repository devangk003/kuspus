#pragma warning disable CA1848
#pragma warning disable CA1873

using System.IO;
using System.Net.Http;
using System.Windows;
using System.Windows.Threading;
using WpfApplication = System.Windows.Application;
using KusPus.Audio;
using KusPus.Native;
using KusPus.Persistence;
using KusPus.Whisper;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Serilog;
using SerilogExt = Serilog.Extensions.Logging;

namespace KusPus.App;

/// <summary>
/// WPF entry point. Composition root, single-instance guard, logging, exception
/// handlers. See TECH_SPEC §7 (DI) and §10.4 (unhandled exceptions).
/// </summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage(
    "Microsoft.Design", "CA1001:Types that own disposable fields should be disposable",
    Justification = "WPF Application owns its disposable fields via OnExit; making App IDisposable would conflict with WPF's lifecycle model.")]
public partial class App : System.Windows.Application
{
    private SingleInstanceGuard? _instanceGuard;
    private IServiceProvider? _services;
    private AppCoordinator? _coordinator;
    private TrayManager? _tray;
    private FloatingPillWindow? _pill;
    private MainWindow? _mainWindow;
    private CrashReporter? _crashReporter;
    private Microsoft.Extensions.Logging.ILogger? _log;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        _instanceGuard = SingleInstanceGuard.AcquireOrSignal();
        if (!_instanceGuard.IsOwner)
        {
            // Another instance is running. Spec §8.10 calls for a bring-to-front
            // broadcast; deferred to Phase 9 when MainWindow exists to receive it.
            Shutdown();
            return;
        }

        AppDomain.CurrentDomain.UnhandledException += OnUnhandledException;
        DispatcherUnhandledException += OnDispatcherUnhandled;
        TaskScheduler.UnobservedTaskException += OnUnobservedTask;

        Directory.CreateDirectory(AppPaths.LogsDir);
        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Debug()
            .WriteTo.File(
                path: Path.Combine(AppPaths.LogsDir, "kuspus-.log"),
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 5,
                fileSizeLimitBytes: 5 * 1024 * 1024,
                formatProvider: System.Globalization.CultureInfo.InvariantCulture)
            .CreateLogger();

        var services = new ServiceCollection();
        services.AddLogging(b =>
        {
            // MEL has its OWN minimum-level filter that defaults to Information,
            // independent of Serilog's MinimumLevel. Without this, LogDebug calls
            // are filtered out by MEL before Serilog ever sees them.
            b.SetMinimumLevel(LogLevel.Debug);
            b.AddProvider(new SerilogExt.SerilogLoggerProvider(Log.Logger));
        });
        ConfigureServices(services);
        _services = services.BuildServiceProvider();

        _log = _services.GetRequiredService<ILoggerFactory>().CreateLogger("KusPus.App");
        _log.LogInformation("KusPus starting up. Logs at {Path}.", AppPaths.LogsDir);

        // Theme brushes installed before any window is constructed so XAML can
        // resolve {DynamicResource AppBg} etc. at MainWindow.InitializeComponent.
        // Apply also handles subsequent replacements when the user flips the theme.
        var initialMode = ThemeApply.Resolve(_services.GetRequiredService<IPrefsStore>().Current.Ui.Theme);
        ThemeTokens.Apply(Resources, initialMode);

        // Crash reporter — listens to Privacy toggles via PrefsStore.Changes and
        // initialises / tears down Sentry accordingly. Started before the coordinator
        // so a startup failure in any later wire-up still ships a scrubbed report
        // when the user has opted in.
        _crashReporter = _services.GetRequiredService<CrashReporter>();
        _crashReporter.Start();

        _coordinator = _services.GetRequiredService<AppCoordinator>();
        _pill = new FloatingPillWindow();
        _pill.SetLogger(_services.GetRequiredService<ILoggerFactory>().CreateLogger<FloatingPillWindow>());
        _pill.SetCloseAction(Shutdown);
        _pill.Bind(_coordinator.State);
        _pill.BindLevels(_services.GetRequiredService<IAudioRecorder>().Levels);

        // MainWindow is created at startup but stays hidden until the user opens it
        // via the tray "Preferences…" item. Hides on close — only the tray's Quit
        // fully exits (APP_DESIGN §3.1 / §8.5).
        _mainWindow = new MainWindow(
            _services.GetRequiredService<IPrefsStore>(),
            _services.GetRequiredService<IHotkeyEngine>(),
            _services.GetRequiredService<IModelManager>(),
            _services.GetRequiredService<IHistoryStore>(),
            _coordinator,
            _services.GetService<ILogger<MainWindow>>());

        // Pill's hover-extended Settings button opens Preferences too (audit follow-
        // up). Wired here, not above, because _mainWindow has to exist first.
        _pill.SetSettingsAction(() => _mainWindow.ShowOn("general"));

        _tray = new TrayManager(
            _coordinator,
            onPreferences: () => _mainWindow.ShowOn("general"),
            onQuit: Shutdown);

        _coordinator.Start();

        // First-launch onboarding. Queued with Background priority so OnStartup
        // returns first and the message loop is fully running before the modal's
        // nested dispatcher frame begins. Skip-on-skip semantics: Onboarding.Completed
        // stays false until the user actually Finishes, so closing the modal early
        // brings it back on the next launch (re-runnable via About → "Run again").
        if (!_services.GetRequiredService<IPrefsStore>().Current.Onboarding.Completed)
        {
            Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
            {
                var window = new OnboardingWindow(
                    _services.GetRequiredService<IPrefsStore>(),
                    _services.GetRequiredService<IHotkeyEngine>(),
                    _services.GetService<ILogger<OnboardingWindow>>());
                window.ShowDialog();
            }));
        }
    }

    private static void ConfigureServices(IServiceCollection services)
    {
        // Persistence
        services.AddSingleton<IPrefsStore>(sp =>
            new PrefsStore(
                AppPaths.SettingsPath,
                sp.GetService<ILogger<PrefsStore>>()));
        services.AddSingleton<IHistoryStore>(sp =>
            new HistoryStore(
                AppPaths.HistoryDbPath,
                sp.GetService<ILogger<HistoryStore>>()));

        // Networking — every outbound request goes through EgressAllowlistHandler so
        // Offline Mode ON / non-allowlisted host produces a blocked HttpRequestException
        // instead of hitting the network. See PRD §10.2 + TECH_SPEC §19. HttpClient owns
        // the handler chain; ServiceProvider disposal at OnExit disposes the singleton.
        services.AddSingleton(sp =>
        {
            var handler = new EgressAllowlistHandler(
                sp.GetRequiredService<IPrefsStore>(),
                sp.GetService<ILogger<EgressAllowlistHandler>>())
            {
                InnerHandler = new SocketsHttpHandler(),
            };
            return new HttpClient(handler);
        });

        // Whisper
        services.AddSingleton<IModelManager>(sp =>
            new ModelManager(
                AppPaths.ModelsDir,
                sp.GetRequiredService<HttpClient>(),
                logger: sp.GetService<ILogger<ModelManager>>()));
        services.AddSingleton<IProcessContainer>(sp =>
            new JobObjectContainer(sp.GetService<ILogger<JobObjectContainer>>()));
        services.AddSingleton<IWhisperRunner>(sp =>
            new WhisperRunner(
                AppPaths.WhisperDir,
                AppPaths.ExpectedWhisperSha256,
                onProcessStarted: sp.GetRequiredService<IProcessContainer>().Contain,
                logger: sp.GetService<ILogger<WhisperRunner>>()));

        // Audio — tempDir from KUSPUS_TEMP_DIR or system %TEMP%.
        services.AddSingleton<IAudioRecorder>(sp =>
            new AudioRecorder(AppPaths.TempDir, sp.GetService<ILogger<AudioRecorder>>()));

        // Native — clipboard writer + paste + hotkey
        services.AddSingleton<IClipboardWriter, WpfClipboardWriter>();
        services.AddSingleton<IPasteEngine>(sp =>
            new PasteEngine(
                sp.GetRequiredService<IClipboardWriter>(),
                sp.GetService<ILogger<PasteEngine>>()));
        services.AddSingleton<IHotkeyEngine>(sp =>
            new HotkeyEngine(sp.GetService<ILogger<HotkeyEngine>>()));

        // Crash reporter (Phase 11) — opt-in Sentry, gated on (CrashReportsOptIn && !OfflineMode).
        // Takes the ILoggerFactory (not just ILogger<CrashReporter>) so it can build
        // a logger for the EgressAllowlistHandler it constructs for Sentry's transport.
        services.AddSingleton(sp => new CrashReporter(
            sp.GetRequiredService<IPrefsStore>(),
            sp.GetService<ILoggerFactory>()));

        // Coordinator
        services.AddSingleton(sp => new AppCoordinator(
            sp.GetRequiredService<IHotkeyEngine>(),
            sp.GetRequiredService<IAudioRecorder>(),
            sp.GetRequiredService<IWhisperRunner>(),
            sp.GetRequiredService<IPasteEngine>(),
            sp.GetRequiredService<IHistoryStore>(),
            sp.GetRequiredService<IModelManager>(),
            sp.GetRequiredService<IPrefsStore>(),
            WpfApplication.Current.Dispatcher,
            sp.GetService<ILogger<AppCoordinator>>()));
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _log?.LogInformation("KusPus shutting down.");
        _crashReporter?.Dispose();
        _coordinator?.Dispose();
        _tray?.Dispose();
        _pill?.Close();
        // MainWindow's normal Close behaviour is to hide (per §3.1/§8.5). At app
        // shutdown we want a real close — ForceClose flips the internal flag.
        _mainWindow?.ForceClose();
        (_services as IDisposable)?.Dispose();
        _instanceGuard?.Dispose();
        Log.CloseAndFlush();
        base.OnExit(e);
    }

    // Unhandled-exception handlers per TECH_SPEC §10.4. Each one (a) writes a
    // Serilog record for the local %LOCALAPPDATA%\KusPus\logs file, then
    // (b) forwards to Sentry IF the user has opted in — gated on
    // _crashReporter?.IsActive so we don't call into a Sentry SDK that's been
    // torn down by a privacy-toggle flip.

    private void OnUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        if (e.ExceptionObject is not Exception ex)
        {
            return;
        }
        _log?.LogCritical(ex, "Unhandled AppDomain exception.");
        TryReportToSentry(ex);
    }

    private void OnDispatcherUnhandled(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        _log?.LogCritical(e.Exception, "Unhandled dispatcher exception.");
        TryReportToSentry(e.Exception);
        // Don't crash the app on a UI-thread exception in v1; log and swallow.
        e.Handled = true;
    }

    private void OnUnobservedTask(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        _log?.LogWarning(e.Exception, "Unobserved task exception.");
        TryReportToSentry(e.Exception);
        e.SetObserved();
    }

    private void TryReportToSentry(Exception ex)
    {
        if (_crashReporter?.IsActive != true)
        {
            return;
        }
        try
        {
            Sentry.SentrySdk.CaptureException(ex);
        }
        catch (Exception sentryEx)
        {
            // Never let a Sentry failure escalate into a recursive unhandled
            // exception — log and move on.
            _log?.LogWarning(sentryEx, "Sentry CaptureException failed.");
        }
    }
}
