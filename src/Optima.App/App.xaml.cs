using System.Windows;
using System.Windows.Threading;
using Optima.App.Logging;
using Optima.App.Services;
using Optima.App.ViewModels;
using Optima.App.Views;
using Optima.Core.Abstractions;
using Optima.Core.Configuration;
using Optima.Core.Launch;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Serilog;
using Serilog.Core;
using Serilog.Events;

namespace Optima.App;

public partial class App : Application
{
    private ServiceProvider? _services;
    private GlobalHotkeys? _hotkeys;
    private TrayService? _tray;
    private AppShutdown? _shutdown;
    private ConsoleWindow? _console;
    private OverlayController? _overlay;
    private ThemeService? _theme;
    private Services.SplashHost? _splash;
    private Core.Health.CrashMarker? _crashMarker;
    private LaunchOrchestrator? _orchestrator;
    private bool _fatal;
    private bool _reportingActionFailure;

    // One instance at a time: a second launch must never stack a second Optima, it should
    // bring the running one back instead (the exit path can stall, so users relaunch).
    private const string SingleInstanceMutexName = "Local\\Optima.SingleInstance";
    private const string SingleInstanceActivateEventName = "Local\\Optima.SingleInstance.Activate";
    private static Mutex? _singleInstanceMutex;
    private static EventWaitHandle? _activateEvent;

    public static LoggingLevelSwitch LogLevelSwitch { get; } = new(LogEventLevel.Information);
    public static InAppLogSink LogSink { get; } = new();

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        StartupTrace.Mark("app");
        // First of all: everything below can fail (a data folder that cannot be written, a log that
        // cannot be opened), and a failure nobody listens for is a start that ends with no window,
        // no message and no log line.
        DispatcherUnhandledException += OnDispatcherUnhandledException;

        // Before anything else, and before the log is opened: a second instance must exit
        // quietly without touching files the first one owns.
        if (!TryAcquireSingleInstance())
        {
            SignalExistingInstance();
            Shutdown(0);
            return;
        }
        StartActivationListener();

        var paths = new AppPaths();
        paths.EnsureCreated();
        // The first JSON read of a type pays for the reflection over it, about a tenth of a second
        // for the files startup reads. Paid on the pool, while this thread starts the splash, the
        // log and the services.
        _ = Task.Run(() => WarmUpJson(paths));

        // The splash owns the screen while the host builds; skipped for a tray autostart,
        // where no window is wanted at all.
        var startInTrayArg = e.Args.Any(a => string.Equals(a, "--tray", StringComparison.OrdinalIgnoreCase));
        if (!startInTrayArg)
        {
            // The splash is up long before the settings service exists, so the one preference it
            // depends on is read straight from the file: without it, a PC with Windows animations
            // off shows a still splash even though the user told Optima to animate regardless.
            Motion.SetMode(ReadAnimationMode(paths.ConfigFile));
            // On its own thread, so it animates through everything below. Whether it moves at all
            // follows the motion preference only: at launch nothing of Optima is in the
            // foreground yet, and that must not freeze the splash.
            _splash = Services.SplashHost.Show(
                Motion.Allowed, "v" + (typeof(App).Assembly.GetName().Version?.ToString(3) ?? "0.0.0"));
            _splash?.SetStatus("starting", 0.1);
        }

        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.ControlledBy(LogLevelSwitch)
            .Enrich.FromLogContext()
            .WriteTo.File(
                System.IO.Path.Combine(paths.LogsDirectory, "optima-.log"),
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 14,
                outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff} [{Level:u5}] {SourceContext}: {Message:lj}{NewLine}{Exception}")
            .WriteTo.Sink(LogSink)
            .WriteTo.Debug()
            .CreateLogger();

        // Before the host is built: a service that cannot be constructed is a failure too, and it
        // used to happen before anything was listening.
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            var exception = args.ExceptionObject as Exception;
            if (exception is not null)
            {
                _crashMarker?.RecordFatal("a background thread", exception, DateTimeOffset.Now);
            }
            Log.Fatal(exception, "Unhandled AppDomain exception");
        };
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            Log.Error(args.Exception, "Unobserved task exception");
            args.SetObserved();
        };

        ReportPreviousRun(paths);
        // On the pool: one registry read that nothing at startup waits for.
        _ = Task.Run(Services.AutostartService.RepointIfStale);

        _splash?.SetStatus("building services", 0.3);
        // A plain container, with Serilog as the only logger. The generic host this replaces also
        // set up configuration files with their watchers, console and event-log logging and a
        // Ctrl+C lifetime; nothing here is a hosted service, and building all that cost a tenth
        // of a second on every start.
        var services = new ServiceCollection();
        services.AddSingleton<ILoggerFactory>(new Serilog.Extensions.Logging.SerilogLoggerFactory());
        services.AddSingleton(typeof(ILogger<>), typeof(Logger<>));
        AppServices.Register(services, paths);
        _services = services.BuildServiceProvider();
        StartupTrace.Mark("services");
        // The status rows wait on this probe, and it is the slowest thing startup asks Windows
        // (the optional-feature query alone takes a third of a second). Started now, it is done
        // or nearly so by the time anything reads it; the service hands every caller the same task.
        _ = _services.GetRequiredService<ISystemInfoService>().GetVirtualizationStateAsync();

        // From here on the issue list reads every log line, the ones already written included.
        // Started first: that is what loads the issues the user chose to ignore, and the lines
        // already written (how the last run ended, for one) must be read against that list.
        var issues = _services.GetRequiredService<Core.Health.IssueEngine>();
        issues.Start();
        LogSink.Attach(issues.Ingest);

        Log.Information("Optima starting (version {Version})",
            typeof(App).Assembly.GetName().Version);

        var settingsService = _services.GetRequiredService<SettingsService>();
        _theme = new ThemeService(settingsService);
        // Theme must be on the wall before the first window paints. The synchronous read keeps
        // startup on this thread instead of blocking it on a thread pool hop.
        _splash?.SetStatus("reading settings", 0.65);
        var initialSettings = settingsService.GetSettings();
        _theme.Initialize(initialSettings);
        try
        {
            _splash?.SetAccent((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(initialSettings.AccentColor));
        }
        catch (FormatException)
        {
            // The splash keeps its default accent.
        }
        Motion.SetMode(initialSettings.EffectiveAnimations);
        settingsService.SettingsChanged += (_, s) => Dispatcher.BeginInvoke(() => Motion.SetMode(s.EffectiveAnimations));

        _splash?.SetStatus("preparing the window", 0.85);
        var mainViewModel = _services.GetRequiredService<MainViewModel>();
        var window = new MainWindow { DataContext = mainViewModel };
        StartupTrace.Mark("window built");
        MainWindow = window;
        // The hidden console window must not keep the app alive after the main window closes.
        ShutdownMode = ShutdownMode.OnMainWindowClose;
        var startInTray = e.Args.Any(a => string.Equals(a, "--tray", StringComparison.OrdinalIgnoreCase));
        if (!startInTray)
        {
            window.Show();
            StartupTrace.Mark("window shown");
            _ = Dispatcher.BeginInvoke(DispatcherPriority.ContextIdle, () => StartupTrace.Mark("first idle"));
            // The splash sits on top; its centre square opens onto the window that is already
            // rendered underneath, so there is no flash of empty desktop.
            _splash?.SetStatus("ready", 1);
            var bounds = window.WindowState == WindowState.Maximized
                ? new Rect(SystemParameters.WorkArea.Location, SystemParameters.WorkArea.Size)
                : new Rect(window.Left, window.Top, window.ActualWidth, window.ActualHeight);
            _ = _splash?.OpenIntoAsync(bounds).ContinueWith(_ => StartupTrace.Mark("splash gone"), TaskScheduler.Default);
        }
        else
        {
            // Never activated, so the window would otherwise count as foreground and keep
            // the ambient drift ticking on a window nobody can see.
            Motion.SetForeground(false);
        }
        if (e.Args.Any(a => string.Equals(a, "--glass-lab", StringComparison.OrdinalIgnoreCase)))
        {
            new GlassLabWindow().Show();
        }

        // The window paints here. Everything below used to run before its first frame, because a
        // frame is only drawn once this method gives the thread back. Loaded priority is below
        // rendering and above input, so the frame comes first and no click or key is handled
        // before the hotkeys, the tray and the close handling exist.
        await Dispatcher.Yield(DispatcherPriority.Loaded);
        // A first frame that failed has been reported and the app is closing: start nothing more.
        if (_fatal)
        {
            return;
        }

        _hotkeys = new GlobalHotkeys(window);
        _hotkeys.ConsoleRequested += ToggleConsole;
        _hotkeys.KillGameRequested += KillGameFromHotkey;

        _overlay = new OverlayController(
            _services.GetRequiredService<OverlayViewModel>(),
            _services.GetRequiredService<IGameWindowLocator>(),
            _services.GetRequiredService<SettingsService>(),
            _services.GetRequiredService<LaunchOrchestrator>(),
            _services.GetRequiredService<INetworkQualityMonitor>());
        _hotkeys.OverlayRequested += () => _overlay!.Toggle();

        _shutdown = new AppShutdown(window, _services.GetRequiredService<IDriverInstaller>(), _services.GetRequiredService<SettingsService>());
        // An update ends with the setup replacing this process: out without the exit questions.
        _services.GetRequiredService<UpdateViewModel>().ExitForUpdate = () => _shutdown.ShutdownNow(0);

        _tray = new TrayService(window, _services.GetRequiredService<SettingsService>(), _shutdown);
        var orchestrator = _orchestrator = _services.GetRequiredService<LaunchOrchestrator>();
        _tray.AttachOrchestrator(orchestrator);
        orchestrator.ProgressChanged += (_, progress) => Dispatcher.BeginInvoke(() =>
            window.AmbientLayer.State = progress.Phase switch
            {
                LaunchPhase.Failed => Controls.AmbientState.Attention,
                LaunchPhase.Idle or LaunchPhase.Completed => Controls.AmbientState.Rest,
                _ => Controls.AmbientState.Session,
            });
        _tray.TerminateGameRequested += KillGameFromHotkey;
        _tray.NavigateRequested += page =>
        {
            _tray!.ShowMainWindow();
            _ = mainViewModel.NavigateCommand.ExecuteAsync(page);
        };

        _services.GetRequiredService<Optima.Core.Crashes.CrashSentinel>().Start();
        // The Watchdog's stats arm: public-profile deltas around each run (needs the
        // player's in-game name in Settings; without it, it never touches the network).
        _services.GetRequiredService<Optima.Core.Stats.SessionStatsEnricher>().Start();
        var discord = _services.GetRequiredService<Services.DiscordPresenceService>();
        discord.AttachLauncherWindow(window);
        // On the pool: the settings are already cached, so this would run start to finish on this
        // thread, building the Discord client and sending the first two web requests of the process.
        _ = Task.Run(discord.StartAsync);

        var hardware = _services.GetRequiredService<IPerformanceMonitor>();
        void SyncHardwareMonitor()
        {
            var wanted = window.IsVisible && window.WindowState != WindowState.Minimized;
            _ = RunQuietlyAsync(wanted ? hardware.StartAsync() : hardware.StopAsync(), "Hardware monitor toggle");
        }
        window.IsVisibleChanged += (_, _) => SyncHardwareMonitor();
        window.StateChanged += (_, _) => SyncHardwareMonitor();
        SyncHardwareMonitor();

        var presence = _services.GetRequiredService<Optima.Core.Monitoring.GamePresenceService>();
        var crashRelaunch = _services.GetRequiredService<Optima.Core.Launch.CrashAutoRelaunchService>();
        crashRelaunch.Start(presence);
        var sessionTweaks = _services.GetRequiredService<Optima.Core.Launch.SessionTweakService>();
        sessionTweaks.Start();
        _ = SyncSessionTweaksAsync(sessionTweaks);
        _services.GetRequiredService<Optima.Core.Launch.PriorityGuardService>().Start(presence);
        _services.GetRequiredService<Optima.Monitoring.Metrics.StandbyCleanerService>().Start();
        _services.GetRequiredService<Optima.Core.Launch.TimerResolutionService>().Start();
        _services.GetRequiredService<Optima.Core.Launch.BackgroundDemotionService>().Start();
        _services.GetRequiredService<Optima.Core.Launch.GameExtrasService>().Start();
        // Repairs are decided on the moment: whether the window is on screen, whether a game runs.
        // Each time one of those changes, what was waiting for it is looked at again.
        var repairs = _services.GetRequiredService<Core.Health.RepairRunner>();
        void LookForRepairs() => _ = Task.Run(() => repairs.EvaluateAsync());
        void SyncRepairMoment()
        {
            Services.RepairMoment.WindowVisible = window.IsVisible && window.WindowState != WindowState.Minimized;
            LookForRepairs();
        }
        // The status loop rests while the window is away, so its rows are read once as it returns.
        void SyncWindowMoment()
        {
            var wasVisible = Services.RepairMoment.WindowVisible;
            SyncRepairMoment();
            if (Services.RepairMoment.WindowVisible && !wasVisible)
            {
                _ = mainViewModel.Status.RefreshLiveAsync();
            }
        }
        window.IsVisibleChanged += (_, _) => SyncWindowMoment();
        window.StateChanged += (_, _) => SyncWindowMoment();
        orchestrator.SessionEnded += LookForRepairs;
        settingsService.SettingsChanged += (_, _) => LookForRepairs();

        // Whatever Optima repaired without being asked, it says: in the window when the window is
        // there, from the tray when it is not, and not at all over a game.
        var toasts = _services.GetRequiredService<Services.ToastService>();
        issues.AttemptRecorded += attempt =>
        {
            if (attempt.Trigger == Core.Health.RepairTrigger.User)
            {
                return;
            }
            var (title, text, kind) = Services.RepairNotice.Describe(attempt);
            if (Services.RepairMoment.WindowVisible)
            {
                toasts.Show(title, text, kind, "details", () => _ = mainViewModel.NavigateCommand.ExecuteAsync("DEBUG"));
            }
            else if (!orchestrator.IsSessionActive && presence.Current == Optima.Core.Monitoring.GamePresence.NotRunning)
            {
                Dispatcher.BeginInvoke(() => _tray?.ShowBalloon(title, text));
            }
        };

        repairs.Start();
        SyncRepairMoment();

        // Protected play is started at PLAY, where its one administrator prompt belongs, and here
        // for a game that watch mode found running, where nothing may prompt. A session that is not
        // protected is said at once and wherever the player is looking, the tray included: before a
        // tournament match is the only time that sentence is of any use.
        var protection = _services.GetRequiredService<Optima.Core.Protection.ShieldLoader>();
        _services.GetRequiredService<Optima.Core.Launch.GameWatchService>().WatchSessionStarted
            += () => _ = protection.StartAsync("watch", allowPrompt: false);
        orchestrator.SessionEnded += protection.SessionEnded;
        protection.Changed += state =>
        {
            // Anyone who looks at the tray can tell that the module is running.
            var running = state.Kind == Optima.Core.Protection.ShieldPresence.Protected;
            Dispatcher.BeginInvoke(() => _tray?.SetTip(running ? "Optima · Shield running" : "Optima"));
            if (state.Kind != Optima.Core.Protection.ShieldPresence.Unprotected)
            {
                return;
            }
            const string title = "This session is not protected";
            if (Services.RepairMoment.WindowVisible)
            {
                toasts.Show(title, state.Message, Services.ToastKind.Warn, "settings",
                    () => _ = mainViewModel.NavigateCommand.ExecuteAsync("SETTINGS"));
            }
            else
            {
                Dispatcher.BeginInvoke(() => _tray?.ShowBalloon(title, state.Message));
            }
        };

        presence.PresenceChanged += change =>
        {
            if (change.Current == Optima.Core.Monitoring.GamePresence.NotRunning)
            {
                LookForRepairs();
            }
            SetOwnPriority(gameOnScreen: change.Current == Optima.Core.Monitoring.GamePresence.InGame);
            // Decoration stops for the whole run: a drifting backdrop behind a game is cost with no
            // one watching it. Starting counts — the emulator is already up at that point.
            var running = change.Current != Optima.Core.Monitoring.GamePresence.NotRunning;
            Dispatcher.BeginInvoke(() => Motion.SetGameRunning(running));
        };

        _ = ReportStartupAsync(mainViewModel.InitializeAsync());
        _startupComplete = true;
    }

    // From here on a failed file or database action is that action's failure, not the app's.
    private bool _startupComplete;

    /// <summary>Writes the startup timeline once the pages have their data and the splash has had time to leave.</summary>
    private static async Task ReportStartupAsync(Task initialization)
    {
        await initialization;
        StartupTrace.Mark("ready");
        await Task.Delay(TimeSpan.FromSeconds(3));
        StartupTrace.Report();
    }

    /// <summary>
    /// Marks this run as started and says how the one before it ended. A process that dies takes
    /// its last log lines with it, so the previous run's fatal error is written into this run's
    /// log, where it can be read and copied.
    /// </summary>
    private void ReportPreviousRun(AppPaths paths)
    {
        _crashMarker = new Core.Health.CrashMarker(paths.HealthDirectory);
        var previous = _crashMarker.Begin(
            typeof(App).Assembly.GetName().Version?.ToString(3) ?? "0.0.0", DateTimeOffset.Now);
        if (previous.Fatal is { } fatal)
        {
            Log.Error("The previous run of Optima ended on a fatal error at {At}, on {Origin}: {Summary}\n{Detail}",
                fatal.At.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss"), fatal.Origin, fatal.Summary, fatal.FullText);
        }
        else if (previous.EndedUncleanly)
        {
            Log.Warning("The previous run of Optima ({Version}, started {StartedAt}) did not shut down normally: "
                + "it was ended from outside, or the PC lost power",
                previous.Version, previous.StartedAt?.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss"));
        }
    }

    /// <summary>
    /// Reads the two files startup is about to load with the serializer it will use, so that the
    /// real loads find its type metadata built. Never through <see cref="JsonStore"/>: a warm-up
    /// must not be what sets a damaged file aside. Whatever goes wrong here, the real load reports.
    /// </summary>
    private static void WarmUpJson(AppPaths paths)
    {
        try
        {
            if (System.IO.File.Exists(paths.ConfigFile))
            {
                var settings = System.Text.Json.JsonSerializer.Deserialize<Core.Models.AppSettings>(
                    System.IO.File.ReadAllText(paths.ConfigFile), JsonStore.Options);
                System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(settings, JsonStore.Options);
            }
            if (System.IO.File.Exists(paths.IssuesFile))
            {
                System.Text.Json.JsonSerializer.Deserialize<Core.Health.IssueStateData>(
                    System.IO.File.ReadAllText(paths.IssuesFile), JsonStore.Options);
            }
        }
        catch (Exception)
        {
        }
    }

    /// <summary>
    /// The animation choice, read before anything else is loaded so that the splash already obeys
    /// it. "Do what Windows says", the default, when it cannot be read. A configuration from before
    /// the choice existed has the old checkbox in its place, and unticked meant "always on".
    /// </summary>
    private static string ReadAnimationMode(string configFile)
    {
        try
        {
            if (!System.IO.File.Exists(configFile))
            {
                return Optima.Core.Theming.MotionPolicy.System;
            }
            using var document = System.Text.Json.JsonDocument.Parse(System.IO.File.ReadAllText(configFile));
            if (document.RootElement.TryGetProperty("animations", out var mode)
                && mode.ValueKind == System.Text.Json.JsonValueKind.String)
            {
                return mode.GetString() ?? Optima.Core.Theming.MotionPolicy.System;
            }
            return document.RootElement.TryGetProperty("followWindowsMotion", out var follow)
                && follow.ValueKind == System.Text.Json.JsonValueKind.False
                    ? Optima.Core.Theming.MotionPolicy.On
                    : Optima.Core.Theming.MotionPolicy.System;
        }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            return Optima.Core.Theming.MotionPolicy.System;
        }
    }

    private static async Task RunQuietlyAsync(Task task, string what)
    {
        try
        {
            await task;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "{What} failed", what);
        }
    }

    private async Task SyncSessionTweaksAsync(Core.Launch.SessionTweakService sessionTweaks)
    {
        try
        {
            var settingsService = _services!.GetRequiredService<SettingsService>();
            var sync = async () =>
            {
                var s = await settingsService.GetSettingsAsync();
                var ids = new List<string>();
                if (s.SessionTweakGameBarOff)
                {
                    ids.Add("session-gamebar-off");
                }
                if (s.SessionTweakFseOff)
                {
                    ids.Add("session-fse-off");
                }
                sessionTweaks.EnabledIds = ids;
            };
            await sync();
            settingsService.SettingsChanged += async (_, _) => await sync();
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Session tweak sync failed");
        }
    }

    private static void SetOwnPriority(bool gameOnScreen)
    {
        try
        {
            using var self = System.Diagnostics.Process.GetCurrentProcess();
            var target = gameOnScreen
                ? System.Diagnostics.ProcessPriorityClass.BelowNormal
                : System.Diagnostics.ProcessPriorityClass.Normal;
            if (self.PriorityClass != target)
            {
                self.PriorityClass = target;
                Log.Information("Optima process priority set to {Priority}", target);
            }
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "Could not change the Optima process priority");
        }
    }

    private void ToggleConsole()
    {
        if (_console is { IsVisible: true })
        {
            _console.Hide();
            return;
        }
        // Ownerless on purpose: the console must be able to sit on top of the game, not the app.
        _console ??= new ConsoleWindow { DataContext = _services!.GetRequiredService<LogStreamViewModel>() };
        _console.Show();
    }

    private void KillGameFromHotkey()
    {
        var play = _services!.GetRequiredService<PlayViewModel>();
        _ = play.KillGameCommand.ExecuteAsync(null);
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        // The message box below keeps the dispatcher running, so whatever threw can throw again on
        // its next tick or frame. The first error is the one that is reported; a repeat must not
        // run a second restore or put a second box on top of the first.
        if (_fatal)
        {
            e.Handled = true;
            return;
        }
        // A file that would not open, a full disk, a database that is locked: the one action that
        // ran into it has failed and nothing else is wrong. Every button that saves something ends
        // up here when it does, and closing the app for it, with a game possibly running beside it,
        // costs far more than the save that was lost. Only once startup has finished: a failure in
        // the middle of it leaves half an app, and that one has to be reported and closed. (Not
        // "once the window is loaded": started in the tray the window never loads.)
        if (e.Exception is System.IO.IOException or UnauthorizedAccessException or System.Data.Common.DbException
            && _startupComplete)
        {
            Log.Error(e.Exception, "An action failed on a file or the database; Optima carries on");
            e.Handled = true;
            if (MainWindow is { IsVisible: true } window && !_reportingActionFailure)
            {
                _reportingActionFailure = true;
                try
                {
                    MessageBox.Show(window,
                        "That could not be saved or read: " + e.Exception.Message + "\n\n" +
                        "Nothing else is affected. Details were written to the log folder.",
                        "Optima", MessageBoxButton.OK, MessageBoxImage.Warning);
                }
                finally
                {
                    _reportingActionFailure = false;
                }
            }
            return;
        }
        _fatal = true;
        _crashMarker?.RecordFatal("the UI thread", e.Exception, DateTimeOffset.Now);
        Log.Fatal(e.Exception, "Unhandled UI exception");
        // A startup that fails still has the splash up, topmost and in the middle of the screen,
        // which is exactly where the message box opens.
        _splash?.Close();
        // Never leave temporary system changes behind, even on a UI crash (§18).
        TryEmergencyRestore();
        MessageBox.Show(
            "Optima hit an unexpected error and will close.\n\n" +
            "Any temporary system changes have been rolled back. Details were written to the log folder.",
            "Optima", MessageBoxButton.OK, MessageBoxImage.Error);
        e.Handled = true;
        if (_shutdown is not null)
        {
            _shutdown.ShutdownNow(1);
        }
        else
        {
            Shutdown(1);
        }
    }

    private void TryEmergencyRestore()
    {
        try
        {
            var recovery = _services?.GetService<IRecoveryService>();
            if (recovery is null)
            {
                return;
            }
            // Off the dispatcher: this often runs because the UI thread is already unhappy, and
            // the restore's continuations must never need it. Bounded so a wedged device call
            // cannot hang exit.
            var restore = Task.Run(async () =>
            {
                var pending = await recovery.GetPendingAsync().ConfigureAwait(false);
                if (pending is not null)
                {
                    await recovery.RestoreAsync(pending).ConfigureAwait(false);
                }
            });
            if (!restore.Wait(TimeSpan.FromSeconds(10)))
            {
                Log.Warning("Emergency restore timed out; the recovery prompt will appear on next start");
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Emergency restore failed; the recovery prompt will appear on next start");
        }
    }

    private bool TryAcquireSingleInstance()
    {
        bool createdNew;
        try
        {
            _singleInstanceMutex = new Mutex(initiallyOwned: true, SingleInstanceMutexName, out createdNew);
        }
        catch (UnauthorizedAccessException)
        {
            // The mutex exists and is not ours to open: Optima is running as administrator and
            // this start is not. That is "already running", not a crash with no window.
            return false;
        }
        if (createdNew)
        {
            return true;
        }
        _singleInstanceMutex.Dispose();
        _singleInstanceMutex = null;
        return false;
    }

    private static void SignalExistingInstance()
    {
        try
        {
            using var existing = EventWaitHandle.OpenExisting(SingleInstanceActivateEventName);
            existing.Set();
        }
        catch
        {
            // The owner may be exiting right now; the user can just start Optima again.
        }
    }

    private void StartActivationListener()
    {
        _activateEvent = new EventWaitHandle(false, EventResetMode.AutoReset, SingleInstanceActivateEventName);
        var dispatcher = Dispatcher;
        Task.Run(async () =>
        {
            while (_activateEvent is { } evt)
            {
                try
                {
                    await Task.Run(evt.WaitOne).ConfigureAwait(true);
                }
                catch (ObjectDisposedException)
                {
                    return;
                }
                _ = dispatcher.BeginInvoke(() =>
                {
                    var window = MainWindow;
                    if (window is null)
                    {
                        return;
                    }
                    window.Show();
                    window.WindowState = WindowState.Normal;
                    window.Activate();
                });
            }
        });
    }

    private void ReleaseSingleInstance()
    {
        _activateEvent?.Dispose();
        _activateEvent = null;
        try
        {
            _singleInstanceMutex?.ReleaseMutex();
        }
        catch (ApplicationException)
        {
            // The mutex may be held by a different thread; abandoning it is fine on process exit.
        }
        _singleInstanceMutex?.Dispose();
        _singleInstanceMutex = null;
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _hotkeys?.Dispose();
        _overlay?.Dispose();
        _tray?.Dispose();
        _shutdown?.Dispose();
        _theme?.Dispose();
        // A session that is still running ends here, while the services it needs are still up: its
        // capture stops, the system is put back and the session is saved. Left running, it lost its
        // record and left the next start asking about a shutdown that had been a normal one.
        // Bounded like the emergency restore, so a wedged device call cannot hang the exit.
        // Not after a fatal error: the emergency restore has put the system back by then, and a
        // second restore of the same snapshot fails on a display layout that is already undone.
        try
        {
            if (!_fatal && _orchestrator?.StopAsync(TimeSpan.FromSeconds(10)).GetAwaiter().GetResult() == false)
            {
                Log.Warning("The running session did not end within 10 seconds; the recovery prompt will appear on next start");
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Ending the running session reported an error");
        }
        // Optima Shield never outlives Optima. It is asked to send its final report and exit, and
        // would notice by itself a few seconds later if it were not.
        try
        {
            _services?.GetService<Optima.Core.Protection.ShieldLoader>()?.Stop();
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Ending the protected session reported an error");
        }
        var servicesStopped = true;
        try
        {
            // Bounded: a service that hangs while it stops must never keep a dead instance alive,
            // which is how users ended up launching a second Optima over the first.
            servicesStopped = _services?.DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(5)) != false;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Stopping the services reported an error");
        }
        if (!servicesStopped)
        {
            Log.Error("Stopping the services stalled past 5 seconds; forcing exit");
        }
        ReleaseSingleInstance();
        _crashMarker?.End();
        Log.Information("Optima exited");
        Log.CloseAndFlush();
        if (!servicesStopped)
        {
            Environment.Exit(e.ApplicationExitCode);
        }
        base.OnExit(e);
    }
}
