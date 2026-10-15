#pragma warning disable CA1848
#pragma warning disable CA1873

using System.Net.Http;
using AppKit;
using CoreFoundation;
using Foundation;
using KusPus.Coordination;
using KusPus.Core.Platform;
using KusPus.Mac.Platform;
using KusPus.Parakeet;
using KusPus.Persistence;
using KusPus.Whisper;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ObjCRuntime;
using Serilog;
using SerilogExt = Serilog.Extensions.Logging;

namespace KusPus.Mac.App;

/// <summary>
/// AppKit entry point and composition root — the macOS mirror of the WPF <c>App</c>
/// class. Order of operations matches <c>App.OnStartup</c>: single-instance guard →
/// logging → DI → theme → crash reporter → coordinator → UI surfaces → onboarding.
/// </summary>
[Register("AppDelegate")]
[System.Diagnostics.CodeAnalysis.SuppressMessage("Naming", "CA1711:Identifiers should not have incorrect suffix", Justification = "AppKit convention: the NSApplicationDelegate subclass is called AppDelegate.")]
public sealed class AppDelegate : NSApplicationDelegate
{
    private IServiceProvider? _services;
    private AppCoordinator? _coordinator;
    private CrashReporter? _crashReporter;
    private StatusItemController? _statusItem;
    private NotchPanelController? _pill;
    private PreferencesWindowController? _preferences;
    private NSObject? _showObserver;

    /// <summary>The log file sink; Preferences → Clear logs restarts it.</summary>
    internal static RestartableFileSink? LogFileSink { get; private set; }
    private Microsoft.Extensions.Logging.ILogger? _log;

    public override void DidFinishLaunching(NSNotification notification)
    {
        if (!SingleInstance.TryAcquire())
        {
            // Another KusPus is running: bring it forward and leave. (The Windows app
            // only exits here; this also delivers its deferred bring-to-front.)
            NSApplication.SharedApplication.Terminate(this);
            return;
        }

        AppDomain.CurrentDomain.UnhandledException += OnUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTask;

        Directory.CreateDirectory(MacPaths.LogsDir);
        LogFileSink = new RestartableFileSink(() => new LoggerConfiguration()
            .MinimumLevel.Debug()
            .WriteTo.File(
                path: Path.Combine(MacPaths.LogsDir, "kuspus-.log"),
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 5,
                fileSizeLimitBytes: 5 * 1024 * 1024,
                formatProvider: System.Globalization.CultureInfo.InvariantCulture)
            .CreateLogger());
        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Debug()
            .WriteTo.Sink(LogFileSink)
            .CreateLogger();

        var services = new ServiceCollection();
        services.AddLogging(b =>
        {
            b.SetMinimumLevel(LogLevel.Debug);
            b.AddProvider(new SerilogExt.SerilogLoggerProvider(Log.Logger));
        });
        ConfigureServices(services);
        _services = services.BuildServiceProvider();

        _log = _services.GetRequiredService<ILoggerFactory>().CreateLogger("KusPus.Mac.App");
        _log.LogInformation("KusPus (macOS) starting up. Logs at {Path}.", MacPaths.LogsDir);

        var prefs = _services.GetRequiredService<IPrefsStore>();
        MacTheme.Apply(prefs.Current.Ui.Theme);
        // PrefsStore.Changes fires on whatever thread completed SaveAsync (often a
        // thread-pool thread). Always hop to the main queue ASYNCHRONOUSLY: a synchronous
        // InvokeOnMainThread here deadlocks if the saver is itself blocking the main thread.
        prefs.Changes.Subscribe(s => DispatchQueue.MainQueue.DispatchAsync(() => MacTheme.Apply(s.Ui.Theme)));
        InstallMainMenu();

        _crashReporter = _services.GetRequiredService<CrashReporter>();
        _crashReporter.Start();

        _coordinator = _services.GetRequiredService<AppCoordinator>();

        // Once onboarding is behind us the engine itself asks for Input Monitoring when
        // missing; during first-run onboarding the permission step drives the prompt.
        if (_services.GetRequiredService<IHotkeyEngine>() is MacHotkeyEngine macHotkey)
        {
            macHotkey.RequestPermissionOnStart = prefs.Current.Onboarding.Completed;
        }

        var audio = _services.GetRequiredService<IAudioRecorder>();
        audio.SetInputDeviceId(prefs.Current.Audio.InputDeviceId);
        prefs.Changes.Subscribe(s => audio.SetInputDeviceId(s.Audio.InputDeviceId));

        _preferences = new PreferencesWindowController(
            prefs,
            _services.GetRequiredService<IHotkeyEngine>(),
            _services.GetRequiredService<IModelManager>(),
            _services.GetRequiredService<IHistoryStore>(),
            _coordinator,
            audio,
            _services.GetRequiredService<ITranscriber>(),
            rerunOnboarding: RunOnboardingAsync,
            _services.GetService<ILogger<PreferencesWindowController>>());

        _pill = new NotchPanelController(
            prefs,
            audio,
            onRecordToggle: _coordinator.ToggleFromTray,
            onOpenSettings: () => _preferences.ShowOn("general"),
            onClose: () => NSApplication.SharedApplication.Terminate(this),
            logger: _services.GetService<ILogger<NotchPanelController>>());

        _statusItem = new StatusItemController(
            prefs,
            _services.GetRequiredService<IModelManager>(),
            _coordinator,
            onToggleRecorder: _coordinator.ToggleFromTray,
            onOpenTab: tab => _preferences.ShowOn(tab),
            onQuit: () => NSApplication.SharedApplication.Terminate(this));

        _coordinator.Start();
        _showObserver = SingleInstance.Listen(() => DispatchQueue.MainQueue.DispatchAsync(() => _preferences?.ShowOn("general")));


        if (!prefs.Current.Onboarding.Completed)
        {
            // Deferred one turn so the run loop is live before the modal session.
            // The pill's first Show() waits until the modal closes (App.BindPillAndShow).
            BeginInvokeOnMainThread(() =>
            {
                RunOnboarding();
                BindPillAndShow();
            });
        }
        else
        {
            BindPillAndShow();
        }
    }

    /// <summary>
    /// Minimal main menu so standard key equivalents work in our windows (⌘C/⌘V/⌘A in the
    /// History search field, ⌘W to close Preferences, ⌘Q). LSUIElement apps show no menu
    /// bar, but AppKit still routes key equivalents through NSApp.mainMenu.
    /// </summary>
    private void InstallMainMenu()
    {
        var main = new NSMenu();

        var appItem = new NSMenuItem();
        var appMenu = new NSMenu("KusPus");
        appMenu.AddItem(new NSMenuItem("Preferences…", ",", (_, _) => _preferences?.ShowOn("general")));
        appMenu.AddItem(NSMenuItem.SeparatorItem);
        appMenu.AddItem(new NSMenuItem("Quit KusPus", "q", (_, _) => NSApplication.SharedApplication.Terminate(this)));
        appItem.Submenu = appMenu;
        main.AddItem(appItem);

        var editItem = new NSMenuItem();
        var edit = new NSMenu("Edit");
        edit.AddItem(new NSMenuItem("Undo", new Selector("undo:"), "z"));
        edit.AddItem(new NSMenuItem("Redo", new Selector("redo:"), "Z"));
        edit.AddItem(NSMenuItem.SeparatorItem);
        edit.AddItem(new NSMenuItem("Cut", new Selector("cut:"), "x"));
        edit.AddItem(new NSMenuItem("Copy", new Selector("copy:"), "c"));
        edit.AddItem(new NSMenuItem("Paste", new Selector("paste:"), "v"));
        edit.AddItem(new NSMenuItem("Select All", new Selector("selectAll:"), "a"));
        editItem.Submenu = edit;
        main.AddItem(editItem);

        var windowItem = new NSMenuItem();
        var window = new NSMenu("Window");
        window.AddItem(new NSMenuItem("Close", new Selector("performClose:"), "w"));
        windowItem.Submenu = window;
        main.AddItem(windowItem);

        NSApplication.SharedApplication.MainMenu = main;
    }

    /// <summary>Runs the onboarding modal on the main thread (first launch, or About → Run again).</summary>
    private void RunOnboarding()
    {
        if (_services is null)
        {
            return;
        }
        using var onboarding = new OnboardingWindowController(
            _services.GetRequiredService<IPrefsStore>(),
            _services.GetRequiredService<IHotkeyEngine>(),
            _services.GetRequiredService<IAudioRecorder>(),
            _services.GetRequiredService<ITranscriber>(),
            _services.GetRequiredService<IModelManager>(),
            _services.GetService<ILogger<OnboardingWindowController>>());
        onboarding.RunModal();
    }

    private Task RunOnboardingAsync()
    {
        RunOnboarding();
        return Task.CompletedTask;
    }

    private void BindPillAndShow()
    {
        if (_pill is null || _coordinator is null || _services is null)
        {
            return;
        }
        _pill.Bind(_coordinator.State);
        _pill.BindLevels(_services.GetRequiredService<IAudioRecorder>().Levels);
    }

    private static void ConfigureServices(IServiceCollection services)
    {
        services.AddSingleton<IPrefsStore>(sp =>
            new PrefsStore(MacPaths.SettingsPath, sp.GetService<ILogger<PrefsStore>>()));
        services.AddSingleton<IHistoryStore>(sp =>
            new HistoryStore(MacPaths.HistoryDbPath, sp.GetService<ILogger<HistoryStore>>()));

        // Every outbound request goes through the egress allowlist, same as Windows.
        services.AddSingleton(sp =>
        {
            var handler = new EgressAllowlistHandler(
                sp.GetRequiredService<IPrefsStore>(),
                sp.GetService<ILogger<EgressAllowlistHandler>>())
            {
                InnerHandler = EgressAllowlistHandler.CreateInnerHandler(),
            };
            return new HttpClient(handler);
        });

        services.AddSingleton<IModelManager>(sp =>
            new ModelManager(
                MacPaths.ModelsDir,
                sp.GetRequiredService<HttpClient>(),
                logger: sp.GetService<ILogger<ModelManager>>(),
                bundledModelsDirectory: MacPaths.BundledModelsDir));
        services.AddSingleton<IProcessContainer>(sp =>
            new MacProcessContainer(sp.GetService<ILogger<MacProcessContainer>>()));
        // Two engines behind one ITranscriber: whisper-cli subprocess for ggml models,
        // in-process sherpa-onnx for Parakeet. The router picks by ModelDescriptor.Engine.
        services.AddSingleton(sp =>
            new WhisperRunner(
                MacPaths.WhisperDir,
                MacPaths.ExpectedWhisperSha256,
                onProcessStarted: sp.GetRequiredService<IProcessContainer>().Contain,
                logger: sp.GetService<ILogger<WhisperRunner>>(),
                exeName: WhisperRunner.MacExeName));
        services.AddSingleton(sp => new ParakeetTranscriber(logger: sp.GetService<ILogger<ParakeetTranscriber>>()));
        services.AddSingleton<ITranscriber>(sp =>
            new EngineRouter(sp.GetRequiredService<WhisperRunner>(), sp.GetRequiredService<ParakeetTranscriber>()));

        services.AddSingleton<IAudioRecorder>(sp =>
            new MacAudioRecorder(MacPaths.TempDir, sp.GetService<ILogger<MacAudioRecorder>>()));

        services.AddSingleton<IClipboardWriter, MacPasteboardWriter>();
        services.AddSingleton<IPasteEngine>(sp =>
            new MacPasteEngine(sp.GetRequiredService<IClipboardWriter>(), sp.GetService<ILogger<MacPasteEngine>>()));
        services.AddSingleton<IHotkeyEngine>(sp =>
            new MacHotkeyEngine(sp.GetService<ILogger<MacHotkeyEngine>>()));

        services.AddSingleton(sp => new CrashReporter(
            sp.GetRequiredService<IPrefsStore>(),
            sp.GetService<ILoggerFactory>()));

        services.AddSingleton(sp => new AppCoordinator(
            sp.GetRequiredService<IHotkeyEngine>(),
            sp.GetRequiredService<IAudioRecorder>(),
            sp.GetRequiredService<ITranscriber>(),
            sp.GetRequiredService<IPasteEngine>(),
            sp.GetRequiredService<IHistoryStore>(),
            sp.GetRequiredService<IModelManager>(),
            sp.GetRequiredService<IPrefsStore>(),
            new MainThreadDispatcher(),
            MacPaths.FailedDir,
            sp.GetService<ILogger<AppCoordinator>>()));
    }

    public override bool ApplicationShouldHandleReopen(NSApplication sender, bool hasVisibleWindows)
    {
        // Clicking the app in Finder/Launchpad while running: show Preferences.
        _preferences?.ShowOn("general");
        return false;
    }

    public override void WillTerminate(NSNotification notification)
    {
        _log?.LogInformation("KusPus shutting down.");
        _crashReporter?.Dispose();
        _coordinator?.Dispose();
        _statusItem?.Dispose();
        _pill?.Dispose();
        _preferences?.Dispose();
        (_services as IDisposable)?.Dispose();
        Log.CloseAndFlush();
    }

    // ── unhandled exceptions (TECH_SPEC §10.4) ──────────────────────────────

    private void OnUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        if (e.ExceptionObject is not Exception ex)
        {
            return;
        }
        _log?.LogCritical(ex, "Unhandled AppDomain exception.");
        TryReportToSentry(ex);
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
            _log?.LogWarning(sentryEx, "Sentry CaptureException failed.");
        }
    }
}

/// <summary>
/// Single-instance guard: if another process with our bundle id is already running,
/// activate it and report <c>false</c>. Replaces the Windows named mutex.
/// </summary>
internal static class SingleInstance
{
    public static bool TryAcquire()
    {
        var bundleId = NSBundle.MainBundle.BundleIdentifier;
        if (string.IsNullOrEmpty(bundleId))
        {
            return true; // `dotnet run` without a bundle — no way to detect, allow.
        }
        var others = NSRunningApplication.GetRunningApplications(bundleId)
            .Where(a => a.ProcessIdentifier != Environment.ProcessId)
            .ToArray();
        if (others.Length == 0)
        {
            return true;
        }
        // Activating an LSUIElement app shows nothing (and ActivateIgnoringOtherWindows is
        // ignored on macOS 14+). Ask the running instance to show itself instead.
        NSDistributedNotificationCenter.DefaultCenter.PostNotificationName(ShowNotification, bundleId, null, deliverImmediately: true);
        return false;
    }

    /// <summary>Posted by a second launch; the running instance answers by showing Preferences.</summary>
    public const string ShowNotification = "com.devangk003.kuspus.show";

    public static NSObject Listen(Action onShow)
    {
        var bundleId = NSBundle.MainBundle.BundleIdentifier;
        return NSDistributedNotificationCenter.DefaultCenter.AddObserver(
            new NSString(ShowNotification), _ => onShow(), bundleId is null ? null : new NSString(bundleId));
    }
}
