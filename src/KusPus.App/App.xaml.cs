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
            _services.GetService<ILogger<MainWindow>>());

        _tray = new TrayManager(
            _coordinator,
            onPreferences: () => _mainWindow.ShowOn("general"),
            onQuit: Shutdown);

        _coordinator.Start();
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

        // Networking — Phase 11 will wrap the HttpClient with the egress-allowlist handler.
        services.AddSingleton(_ => new HttpClient());

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

    private void OnUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        if (e.ExceptionObject is Exception ex)
        {
            _log?.LogCritical(ex, "Unhandled AppDomain exception.");
        }
    }

    private void OnDispatcherUnhandled(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        _log?.LogCritical(e.Exception, "Unhandled dispatcher exception.");
        // Don't crash the app on a UI-thread exception in v1; log and swallow.
        e.Handled = true;
    }

    private void OnUnobservedTask(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        _log?.LogWarning(e.Exception, "Unobserved task exception.");
        e.SetObserved();
    }
}
