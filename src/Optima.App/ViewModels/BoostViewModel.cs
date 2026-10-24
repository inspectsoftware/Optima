using System.Diagnostics;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32.SafeHandles;
using Optima.Core.Configuration;
using Optima.Core.Launch;
using Optima.Core.Abstractions;
using Optima.Core.Boost;
using Optima.Core.Models;
using Optima.Core.Monitoring;
using Optima.Monitoring.Metrics;
using Optima.Platform.Windows.NativeMethods;
using Microsoft.Extensions.Logging;

namespace Optima.App.ViewModels;

/// <summary>
/// BOOST page: what Optima does while the game runs to keep it smooth. Every switch saves as it is
/// flipped, and every section says what it is doing right now rather than what it was asked to do.
/// While the page is on screen it also reads, once a second, what that adds up to: the game's share
/// of the processor and memory, what the demoted programs are left with, what the cleaner freed.
/// Every number is measured; where one is not available the page shows a dash and says why.
/// </summary>
public sealed partial class BoostViewModel : ObservableObject
{
    private readonly SettingsService _settings;
    private readonly PriorityGuardService _guard;
    private readonly StandbyCleanerService _cleaner;
    private readonly TimerResolutionService _timer;
    private readonly ITimerResolution _timerInfo;
    private readonly ITweakService _tweaks;
    private readonly BackgroundDemotionService _demotion;
    private readonly GameExtrasService _extras;
    private readonly IPerformanceMonitor _monitor;
    private readonly ISystemInfoService _systemInfo;
    private readonly GamePresenceService _presence;
    private readonly ILogger<BoostViewModel> _logger;
    private bool _loading;
    private bool _subscribed;

    // The live readout: the latest hardware sample and the demoted programs' processor shares,
    // both taken only while the page is on screen. The shares are null until the first sample.
    private volatile bool _live;
    private HardwareMetrics? _metrics;
    private IReadOnlyList<(ProcessStateSnapshot Process, double? Cpu)>? _heldBack;
    private IReadOnlyList<GpuInfo>? _adapters;

    // Open handles to the demoted processes and their processor time at the last sample, used on
    // the hardware monitor's thread. A handle stays open while its process is demoted, so its id
    // cannot be handed to another process in between; null once the process has ended.
    private readonly object _cpuGate = new();
    private readonly Dictionary<int, (SafeProcessHandle? Handle, TimeSpan? Cpu)> _cpu = [];
    private long _sampledAt;

    public BoostViewModel(
        SettingsService settings,
        PriorityGuardService guard,
        StandbyCleanerService cleaner,
        TimerResolutionService timer,
        ITimerResolution timerInfo,
        ITweakService tweaks,
        BackgroundDemotionService demotion,
        GameExtrasService extras,
        IPerformanceMonitor monitor,
        ISystemInfoService systemInfo,
        GamePresenceService presence,
        ILogger<BoostViewModel> logger)
    {
        _monitor = monitor;
        _systemInfo = systemInfo;
        _presence = presence;
        _extras = extras;
        _demotion = demotion;
        _timer = timer;
        _timerInfo = timerInfo;
        _tweaks = tweaks;
        _settings = settings;
        _guard = guard;
        _cleaner = cleaner;
        _logger = logger;
    }

    private const string StandbyText = "Ticked, and waiting for the master switch above.";

    /// <summary>The master switch. Changed only by the dial, through <see cref="ToggleBoostCommand"/>.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(BoostSummaryText))]
    private bool _boostOn;

    /// <summary>How many features are ticked, which is what the dial counts up while it arms.</summary>
    public int BoostFeatureCount => int.PopCount(ArmedMask);

    /// <summary>
    /// Which features are ticked, one bit each in the page's order: priority guard, memory cleaner,
    /// timer resolution, background programs, core parking, graphics adapter. The dial lights one
    /// gear per bit.
    /// </summary>
    public int ArmedMask
        => (PriorityGuardEnabled ? 1 : 0) | (CleanerEnabled ? 2 : 0) | (TimerEnabled ? 4 : 0)
            | (DemotionEnabled ? 8 : 0) | (KeepCoresAwake ? 16 : 0) | (GpuHighPerformance ? 32 : 0);

    public string BoostSummaryText => BoostFeatureCount switch
    {
        0 => "nothing ticked below yet",
        var n => $"{n} of 6 ticked below",
    };

    protected override void OnPropertyChanged(System.ComponentModel.PropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        var tick = e.PropertyName is nameof(PriorityGuardEnabled) or nameof(CleanerEnabled) or nameof(TimerEnabled)
            or nameof(DemotionEnabled) or nameof(KeepCoresAwake) or nameof(GpuHighPerformance);
        if (tick)
        {
            base.OnPropertyChanged(new System.ComponentModel.PropertyChangedEventArgs(nameof(ArmedMask)));
            base.OnPropertyChanged(new System.ComponentModel.PropertyChangedEventArgs(nameof(BoostFeatureCount)));
            base.OnPropertyChanged(new System.ComponentModel.PropertyChangedEventArgs(nameof(BoostSummaryText)));
        }
        // The pills and the readout follow a tick at once rather than at the service's next status.
        if (!_loading && (tick || e.PropertyName is nameof(GamePriority) or nameof(BoostOn)))
        {
            Refresh();
        }
    }

    [RelayCommand]
    private async Task ToggleBoostAsync()
    {
        BoostOn = !BoostOn;
        ShowAll();
        try
        {
            await _settings.UpdateSettingsAsync(s => s with { BoostEnabled = BoostOn });
            await _guard.CheckNowAsync();
            await _timer.SyncAsync();
            await _demotion.SyncAsync();
            await _extras.SyncAsync();
            // Last, because it is the one that may stop for the administrator prompt. Switching
            // Boost on is a deliberate act, so it is allowed to ask.
            await _cleaner.SyncAsync(allowPrompt: BoostOn);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Switching Boost failed");
        }
        // The dial shows the switch the services act on, also when the switch itself failed.
        BoostOn = _settings.Current?.BoostEnabled ?? false;
        ShowAll();
    }

    private void ShowAll()
    {
        ShowGuard(_guard.Status);
        ShowCleaner(_cleaner.Status);
        ShowTimer(_timer.Status);
        ShowDemotion(_demotion.Status);
        ShowExtras(_extras.Status);
        Refresh();
    }

    public IReadOnlyList<string> GamePriorityOptions { get; } = ["Unchanged", "Normal", "AboveNormal", "High"];

    [ObservableProperty] private string _gamePriority = "Unchanged";
    [ObservableProperty] private bool _priorityGuardEnabled = true;
    [ObservableProperty] private string _guardStateText = string.Empty;
    [ObservableProperty] private string _guardDetailText = string.Empty;

    [ObservableProperty] private bool _cleanerEnabled;
    [ObservableProperty] private string _cleanerFreeBelowMb = StandbyCleanerPolicy.DefaultFreeBelowMb.ToString(CultureInfo.InvariantCulture);
    [ObservableProperty] private string _cleanerStandbyAboveMb = StandbyCleanerPolicy.DefaultStandbyAboveMb.ToString(CultureInfo.InvariantCulture);
    [ObservableProperty] private string _cleanerStateText = string.Empty;
    [ObservableProperty] private string _cleanerDetailText = string.Empty;
    [ObservableProperty] private string _cleanerMemoryText = string.Empty;
    [ObservableProperty] private bool _cleanerNeedsHelper;
    [ObservableProperty] private bool _cleanerBusy;

    public IReadOnlyList<string> TimerChoices { get; } = TimerResolutionPolicy.Choices;

    [ObservableProperty] private bool _timerEnabled;
    [ObservableProperty] private string _timerChoice = TimerResolutionPolicy.OneMillisecond;
    [ObservableProperty] private string _timerStateText = string.Empty;
    [ObservableProperty] private string _timerDetailText = string.Empty;
    [ObservableProperty] private string _timerWindowsText = string.Empty;
    /// <summary>Only Windows 11 has the system-wide switch.</summary>
    [ObservableProperty] private bool _timerGlobalSwitchAvailable;
    [ObservableProperty] private bool _timerGlobalSwitchOn;
    [ObservableProperty] private string _timerGlobalSwitchText = string.Empty;

    [ObservableProperty] private bool _demotionEnabled;
    /// <summary>One process name per line, as the box shows it.</summary>
    [ObservableProperty] private string _demotionNames = string.Empty;
    [ObservableProperty] private string _demotionStateText = string.Empty;
    [ObservableProperty] private string _demotionDetailText = string.Empty;

    [ObservableProperty] private bool _keepCoresAwake;
    [ObservableProperty] private bool _gpuHighPerformance;
    [ObservableProperty] private string _coresDetailText = string.Empty;
    [ObservableProperty] private string _gpuDetailText = string.Empty;

    public async Task InitializeAsync(CancellationToken ct = default)
    {
        var settings = await _settings.GetSettingsAsync(ct);
        _loading = true;
        try
        {
            GamePriority = GamePriorityOptions.Contains(settings.GamePriority) ? settings.GamePriority : "Unchanged";
            PriorityGuardEnabled = settings.BoostPriorityGuardEnabled;
            BoostOn = settings.BoostEnabled;
            CleanerEnabled = settings.BoostStandbyCleanerEnabled;
            CleanerFreeBelowMb = settings.BoostStandbyFreeBelowMb.ToString(CultureInfo.InvariantCulture);
            CleanerStandbyAboveMb = settings.BoostStandbyAboveMb.ToString(CultureInfo.InvariantCulture);
            TimerEnabled = settings.BoostTimerResolutionEnabled;
            TimerChoice = TimerChoices.Contains(settings.BoostTimerResolution) ? settings.BoostTimerResolution : TimerResolutionPolicy.OneMillisecond;
            TimerGlobalSwitchAvailable =
                TimerResolutionPolicy.ReachFor(_timerInfo.WindowsBuild) == TimerResolutionReach.OwnProcessUnlessGlobalSwitch;
            TimerGlobalSwitchOn = _timerInfo.GlobalRequestsEnabled;
            DemotionEnabled = settings.BoostDemoteBackgroundEnabled;
            DemotionNames = string.Join(Environment.NewLine, settings.BoostDemoteProcessNames);
            KeepCoresAwake = settings.BoostKeepCoresAwake;
            GpuHighPerformance = settings.BoostGpuHighPerformance;
        }
        finally
        {
            _loading = false;
        }

        if (!_subscribed)
        {
            _subscribed = true;
            // Raised from the guard's own loop; the texts are bound.
            _guard.StatusChanged += status => Application.Current?.Dispatcher.BeginInvoke(() => { ShowGuard(status); Refresh(); });
            _cleaner.StatusChanged += status => Application.Current?.Dispatcher.BeginInvoke(() => { ShowCleaner(status); Refresh(); });
            _timer.StatusChanged += status => Application.Current?.Dispatcher.BeginInvoke(() => { ShowTimer(status); Refresh(); });
            _demotion.StatusChanged += status => Application.Current?.Dispatcher.BeginInvoke(() => { ShowDemotion(status); Refresh(); });
            _extras.StatusChanged += status => Application.Current?.Dispatcher.BeginInvoke(() => { ShowExtras(status); Refresh(); });
            _presence.PresenceChanged += _ => Application.Current?.Dispatcher.BeginInvoke(Refresh);
        }
        ShowExtras(_extras.Status);
        ShowDemotion(_demotion.Status);
        ShowGuard(_guard.Status);
        ShowCleaner(_cleaner.Status);
        Refresh();
        _ = LoadAdaptersAsync();
        await _timer.SyncAsync(ct);
    }

    partial void OnTimerEnabledChanged(bool value) => _ = PersistTimerAsync();

    partial void OnTimerChoiceChanged(string value) => _ = PersistTimerAsync();

    private async Task PersistTimerAsync()
    {
        if (_loading)
        {
            return;
        }
        try
        {
            await _settings.UpdateSettingsAsync(s => s with
            {
                BoostTimerResolutionEnabled = TimerEnabled,
                BoostTimerResolution = TimerChoice,
            });
            await _timer.SyncAsync();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Saving the timer resolution settings failed");
        }
    }

    // A machine-wide registry value: it goes through the elevated helper (one admin prompt) and
    // Windows reads it at boot, so the text says "after a restart" rather than pretending it is live.
    partial void OnTimerGlobalSwitchOnChanged(bool value) => _ = ApplyGlobalSwitchAsync(value);

    private async Task ApplyGlobalSwitchAsync(bool enable)
    {
        if (_loading)
        {
            return;
        }
        try
        {
            var state = await _tweaks.SetEnabledAsync(TweakCatalog.GlobalTimerRequestsId, enable);
            var applied = state.Status == TweakStatus.Enabled;
            TimerGlobalSwitchText = applied == enable
                ? (enable ? "Switched on. It takes effect after a Windows restart." : "Switched off. It takes effect after a Windows restart.")
                : "Windows did not accept the change (the admin prompt was declined, or the helper refused it).";
            if (applied != enable)
            {
                _loading = true;
                TimerGlobalSwitchOn = applied;
                _loading = false;
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Changing the system-wide timer switch failed");
            TimerGlobalSwitchText = "The change failed. See the log on the Debug page.";
            _loading = true;
            TimerGlobalSwitchOn = _timerInfo.GlobalRequestsEnabled;
            _loading = false;
        }
        await _timer.SyncAsync();
    }

    partial void OnDemotionEnabledChanged(bool value) => _ = PersistDemotionAsync();

    partial void OnDemotionNamesChanged(string value) => _ = PersistDemotionAsync();

    [RelayCommand]
    private void ResetDemotionNames()
        => DemotionNames = string.Join(Environment.NewLine, BackgroundDemotionService.DefaultProcessNames);

    private async Task PersistDemotionAsync()
    {
        if (_loading)
        {
            return;
        }
        try
        {
            var names = DemotionNames
                .Split(['\r', '\n', ',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(name => name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? name[..^4] : name)
                .Where(name => name.Length > 0)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(64)
                .ToList();
            await _settings.UpdateSettingsAsync(s => s with
            {
                BoostDemoteBackgroundEnabled = DemotionEnabled,
                BoostDemoteProcessNames = names,
            });
            await _demotion.SyncAsync();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Saving the background demotion settings failed");
        }
    }

    private void ShowDemotion(BackgroundDemotionStatus status)
    {
        if (!BoostOn && DemotionEnabled)
        {
            DemotionStateText = "STANDBY";
            DemotionDetailText = StandbyText;
            return;
        }
        if (!status.Enabled)
        {
            DemotionStateText = "OFF";
            DemotionDetailText = "Background programs are left as they are.";
        }
        else if (!status.GameOnScreen)
        {
            DemotionStateText = "ARMED";
            DemotionDetailText = "Waiting for the game. Nothing is demoted until Critical Ops is on screen.";
        }
        else
        {
            // The page's own sample once it has one, as the tile and the rows use: the service only
            // learns at its next pass, up to 20 seconds on, that a process has ended.
            var processes = _heldBack?.Count ?? status.ProcessCount;
            var programs = _heldBack?.Select(p => p.Process.ProcessName).Distinct(StringComparer.OrdinalIgnoreCase).Count()
                ?? status.ProgramCount;
            DemotionStateText = processes == 0 ? "WATCHING" : "ACTIVE";
            DemotionDetailText = processes == 0
                ? "None of the listed programs is running right now."
                : $"{processes} process{(processes == 1 ? string.Empty : "es")} of "
                    + $"{programs} program{(programs == 1 ? string.Empty : "s")} demoted · put back when the game leaves";
        }
    }

    partial void OnKeepCoresAwakeChanged(bool value) => _ = PersistExtrasAsync();

    partial void OnGpuHighPerformanceChanged(bool value) => _ = PersistExtrasAsync();

    private async Task PersistExtrasAsync()
    {
        if (_loading)
        {
            return;
        }
        try
        {
            await _settings.UpdateSettingsAsync(s => s with
            {
                BoostKeepCoresAwake = KeepCoresAwake,
                BoostGpuHighPerformance = GpuHighPerformance,
            });
            await _extras.SyncAsync();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Saving the game extras failed");
        }
    }

    private void ShowExtras(GameExtrasStatus status)
    {
        CoresDetailText = !BoostOn && KeepCoresAwake
            ? StandbyText
            : !status.CoresEnabled
            ? "Off. Windows parks cores as the active power plan says."
            : status.CoresHeld
                ? "Every core is being kept awake · the plan's own setting comes back when the game leaves"
                : status.CoresAlreadyAwake
                    ? "The active power plan already keeps every core awake, so there is nothing to change."
                    : "Waiting for the game.";
        GpuDetailText = !BoostOn && GpuHighPerformance
            ? StandbyText
            : !status.GpuEnabled
            ? "Off. Windows chooses the graphics adapter."
            : status.GpuApplied
                ? "Set for " + System.IO.Path.GetFileName(status.GamePath) + " · Windows reads it the next time the game starts"
                : "It will be set the first time Optima sees the game running, because that is how it learns where the game is installed.";
    }

    private void ShowTimer(TimerResolutionStatus status)
    {
        TimerWindowsText = TimerResolutionPolicy.Describe(_timerInfo.WindowsBuild, _timerInfo.GlobalRequestsEnabled);
        var now = status.CurrentMs.ToString("0.###", CultureInfo.InvariantCulture);
        if (!BoostOn && TimerEnabled)
        {
            TimerStateText = "STANDBY";
            TimerDetailText = StandbyText;
            return;
        }
        // Windows' clock is the finest any program asks for, which is not necessarily the tick
        // Optima or the game gets: since Windows 10 2004 each program gets what it asked for itself.
        var clock = $"Windows' clock is at {now} ms, the finest any program asks for.";
        if (!status.Enabled)
        {
            TimerStateText = "OFF";
            TimerDetailText = "Not holding anything. " + clock;
        }
        else if (!status.Holding)
        {
            TimerStateText = "ARMED";
            TimerDetailText = "Waiting for the game. " + clock;
        }
        else
        {
            TimerStateText = "HOLDING";
            var asked = status.RequestedMs.ToString("0.###", CultureInfo.InvariantCulture);
            var applied = status.AppliedMs?.ToString("0.###", CultureInfo.InvariantCulture) ?? "?";
            TimerDetailText = $"Asked for {asked} ms; Windows' clock is at {applied} ms · "
                + (status.ReachesGame ? "this reaches the game" : "on this Windows it stays inside Optima");
        }
    }

    // Switching the cleaner on is deliberate, so it is allowed to ask for the helper right away.
    partial void OnCleanerEnabledChanged(bool value) => _ = PersistCleanerAsync(allowPrompt: value);

    partial void OnCleanerFreeBelowMbChanged(string value) => _ = PersistCleanerAsync(allowPrompt: false);

    partial void OnCleanerStandbyAboveMbChanged(string value) => _ = PersistCleanerAsync(allowPrompt: false);

    private async Task PersistCleanerAsync(bool allowPrompt)
    {
        if (_loading)
        {
            return;
        }
        try
        {
            // A number outside the range is saved at the nearest end of it; text that is not a
            // number keeps the stored threshold.
            var freeBelow = ParseThreshold(CleanerFreeBelowMb);
            var standbyAbove = ParseThreshold(CleanerStandbyAboveMb);
            await _settings.UpdateSettingsAsync(s => s with
            {
                BoostStandbyCleanerEnabled = CleanerEnabled,
                BoostStandbyFreeBelowMb = freeBelow ?? s.BoostStandbyFreeBelowMb,
                BoostStandbyAboveMb = standbyAbove ?? s.BoostStandbyAboveMb,
            });
            await _cleaner.SyncAsync(allowPrompt);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Saving the memory cleaner settings failed");
        }
        // The boxes and the card's summary show the thresholds in force, not text the cleaner ignores.
        if (_settings.Current is { } saved)
        {
            _loading = true;
            CleanerFreeBelowMb = saved.BoostStandbyFreeBelowMb.ToString(CultureInfo.InvariantCulture);
            CleanerStandbyAboveMb = saved.BoostStandbyAboveMb.ToString(CultureInfo.InvariantCulture);
            _loading = false;
        }

        static int? ParseThreshold(string text)
            => int.TryParse(text.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var value)
                ? StandbyCleanerPolicy.ClampThreshold(value)
                : null;
    }

    [RelayCommand]
    private async Task StartHelperAsync()
    {
        CleanerBusy = true;
        try
        {
            await _cleaner.SyncAsync(allowPrompt: true);
        }
        finally
        {
            CleanerBusy = false;
        }
    }

    [RelayCommand]
    private async Task PurgeNowAsync()
    {
        CleanerBusy = true;
        try
        {
            var freed = await _cleaner.PurgeNowAsync();
            CleanerDetailText = freed is { } mb
                ? $"Purged now · {mb.ToString("N0", CultureInfo.InvariantCulture)} MB of standby memory released"
                : "The purge did not run: the helper was not started or refused it. See the log on the Debug page.";
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Purge now failed");
            CleanerDetailText = "The purge failed. See the log on the Debug page.";
        }
        finally
        {
            CleanerBusy = false;
        }
    }

    private void ShowCleaner(StandbyCleanerStatus status)
    {
        if (!BoostOn && CleanerEnabled)
        {
            CleanerNeedsHelper = false;
            CleanerStateText = "STANDBY";
            CleanerDetailText = StandbyText + " Purge now still works on its own.";
            return;
        }
        CleanerNeedsHelper = status.State == StandbyCleanerState.WaitingForHelper;
        CleanerStateText = status.State switch
        {
            StandbyCleanerState.Running => "RUNNING",
            StandbyCleanerState.WaitingForGame => "ARMED",
            StandbyCleanerState.WaitingForHelper => "NEEDS HELPER",
            _ => "OFF",
        };
        CleanerMemoryText = status.FreeMb is { } free && status.StandbyMb is { } standby
            ? $"free {free.ToString("N0", CultureInfo.InvariantCulture)} MB · standby {standby.ToString("N0", CultureInfo.InvariantCulture)} MB"
            : string.Empty;
        CleanerDetailText = status.State switch
        {
            StandbyCleanerState.Off => "The cleaner is off. Purge now still works on its own.",
            StandbyCleanerState.WaitingForGame => "Waiting for the game. It only watches memory while Critical Ops is on screen.",
            StandbyCleanerState.WaitingForHelper =>
                "The game is running but the elevated helper is not, and Optima does not raise an admin prompt from the background. Start it here, or press PLAY next time.",
            // The run's tally, as the MEMORY RECLAIMED tile counts: the helper's own count leaves out
            // "purge now" and starts again with every game session.
            _ => status.Tally.Purges == 0
                ? "Watching. Nothing has needed purging yet."
                : $"Purged {status.Tally.Purges} time{(status.Tally.Purges == 1 ? string.Empty : "s")} this run · last at "
                    + $"{status.LastPurgeAt?.ToString("HH:mm", CultureInfo.InvariantCulture) ?? "?"}, "
                    + $"{status.LastFreedMb.ToString("N0", CultureInfo.InvariantCulture)} MB released",
        };
    }

    partial void OnGamePriorityChanged(string value) => _ = PersistGuardAsync();

    partial void OnPriorityGuardEnabledChanged(bool value) => _ = PersistGuardAsync();

    private async Task PersistGuardAsync()
    {
        if (_loading)
        {
            return;
        }
        try
        {
            await _settings.UpdateSettingsAsync(s => s with
            {
                GamePriority = GamePriority,
                BoostPriorityGuardEnabled = PriorityGuardEnabled,
            });
            // Acts on the change now instead of at the guard's next pass.
            await _guard.CheckNowAsync();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Saving the priority guard settings failed");
        }
    }

    private void ShowGuard(PriorityGuardStatus status)
    {
        if (!BoostOn && PriorityGuardEnabled)
        {
            GuardStateText = "STANDBY";
            GuardDetailText = StandbyText;
            return;
        }
        if (!status.Enabled && !PriorityGuardEnabled)
        {
            GuardStateText = "OFF";
            GuardDetailText = "The guard is off. A session started with PLAY still applies the priority below.";
        }
        else if (status.Wanted == ProcessPriorityLevel.Unchanged && GamePriority == "Unchanged")
        {
            GuardStateText = "IDLE";
            GuardDetailText = "No priority is chosen, so there is nothing to keep. Pick one below.";
        }
        else if (status.SessionOwned)
        {
            GuardStateText = "SESSION";
            GuardDetailText = "The running Optima session is keeping the priority itself; the guard takes over when it ends.";
        }
        else if (status.ProcessCount == 0)
        {
            GuardStateText = "ARMED";
            GuardDetailText = "Waiting for the game. The priority is applied within seconds of it starting, however it was started.";
        }
        else
        {
            GuardStateText = "HOLDING";
            var since = status.GuardingSince?.ToString("HH:mm", CultureInfo.InvariantCulture) ?? "now";
            var processes = status.ProcessCount == 1 ? "1 game process" : $"{status.ProcessCount} game processes";
            var corrections = status.Corrections switch
            {
                0 => "never had to correct it",
                1 => "corrected it once",
                _ => $"corrected it {status.Corrections} times",
            };
            GuardDetailText = $"{status.Wanted} on {processes} since {since} · {corrections}";
        }
    }

    // ── The live readout ──

    private const string Dash = "—";
    private const double Gb = 1024.0 * 1024 * 1024;

    /// <summary>Both timer combs span this much time, so the spacing of their ticks is to scale.</summary>
    private const double CombSpanMs = 100;

    /// <summary>A comb's tick is this long in time, which keeps it about a pixel wide at any spacing.</summary>
    private const double CombTickMs = 0.3;

    /// <summary>The purge ticks under the cleaner's memory bar cover this much of the past.</summary>
    private static readonly TimeSpan PurgeWindow = TimeSpan.FromMinutes(30);

    /// <summary>For this long after a purge, what it released is drawn as freed inside the free memory.</summary>
    private static readonly TimeSpan FreedShown = TimeSpan.FromMinutes(5);

    /// <summary>The pill texts that mean a boost is doing something this second.</summary>
    private static readonly HashSet<string> WorkingStates = ["HOLDING", "RUNNING", "ACTIVE", "WATCHING", "AWAKE", "SET"];

    /// <summary>OFF, ARMED (on, waiting for the game) or ENGAGED (on, with the game on screen): the header's light.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(BoostEngaged))]
    private string _boostPhase = "OFF";

    /// <summary>Boost on with Critical Ops on screen, which the dial's nameplate reads as ENGAGED.</summary>
    public bool BoostEngaged => BoostPhase == "ENGAGED";
    [ObservableProperty] private string _headerStatusText = string.Empty;
    [ObservableProperty] private string _coresStateText = "OFF";
    [ObservableProperty] private string _gpuStateText = "OFF";

    [ObservableProperty] private FlowBar _processorFlow = FlowBar.Unmeasured;
    [ObservableProperty] private FlowBar _memoryFlow = FlowBar.Unmeasured;

    /// <summary>The game's share of the processor, one sample a second while it runs and this page is on screen.</summary>
    public HistorySeries GameShareHistory { get; } = new(60);
    [ObservableProperty] private string _gameShareText = string.Empty;

    [ObservableProperty] private string _cleanerStandbyText = "standby " + Dash;
    [ObservableProperty] private string _cleanerFreeText = "free " + Dash;

    [ObservableProperty] private string _timerHeldText = string.Empty;
    [ObservableProperty] private bool _timerHeldLive;
    /// <summary>One period of the lower comb as a share of its width, and the period in milliseconds with the tick drawn in it.</summary>
    [ObservableProperty] private Rect _timerHeldTick = new(0, 0, 1, 1);
    [ObservableProperty] private Rect _timerHeldBox = new(0, 0, 1, 1);

    [ObservableProperty] private string _heldBackEmptyText = string.Empty;
    [ObservableProperty] private string _coresCellsText = string.Empty;
    [ObservableProperty] private string _gpuPreferenceText = string.Empty;
    [ObservableProperty] private bool _gpuPreferenceSet;

    private IReadOnlyList<BoostTile> _tiles = [];
    private IReadOnlyList<LadderRung> _rungs = [];
    private IReadOnlyList<HeldBackProgram> _heldBackRows = [];
    private IReadOnlyList<PurgeTick> _purgeTicks = [];
    private IReadOnlyList<bool> _coreCells = [];
    private IReadOnlyList<AdapterRow> _adapterRows = [];

    /// <summary>The six numbers beside the dial.</summary>
    public IReadOnlyList<BoostTile> Tiles { get => _tiles; private set => SetList(ref _tiles, value); }

    public IReadOnlyList<LadderRung> Rungs { get => _rungs; private set => SetList(ref _rungs, value); }

    public IReadOnlyList<HeldBackProgram> HeldBackRows { get => _heldBackRows; private set => SetList(ref _heldBackRows, value); }

    public IReadOnlyList<PurgeTick> PurgeTicks { get => _purgeTicks; private set => SetList(ref _purgeTicks, value); }

    /// <summary>One cell per logical processor, lit while every one of them is held awake.</summary>
    public IReadOnlyList<bool> CoreCells { get => _coreCells; private set => SetList(ref _coreCells, value); }

    public IReadOnlyList<AdapterRow> AdapterRows { get => _adapterRows; private set => SetList(ref _adapterRows, value); }

    // A list is replaced only when what it holds changed: the readout runs every second, and an
    // items control rebuilds every row it shows when its list is replaced.
    private void SetList<T>(ref IReadOnlyList<T> field, IReadOnlyList<T> value, [CallerMemberName] string? name = null)
    {
        if (!field.SequenceEqual(value))
        {
            field = value;
            OnPropertyChanged(name);
        }
    }

    /// <summary>
    /// Starts and stops the live readout with the page: the view calls this as it comes on screen and
    /// as it goes (another page, the tray, a minimized window). The samples ride on the hardware monitor's tick, which
    /// itself only runs while the window is visible and not minimized, so nothing is read for a page
    /// nobody can see.
    /// </summary>
    public void SetLive(bool live)
    {
        if (live == _live)
        {
            return;
        }
        _live = live;
        if (live)
        {
            _monitor.MetricsUpdated += OnMetrics;
            _metrics = _monitor.Latest;
            Refresh();
            return;
        }
        _monitor.MetricsUpdated -= OnMetrics;
        lock (_cpuGate)
        {
            foreach (var (handle, _) in _cpu.Values)
            {
                handle?.Dispose();
            }
            _cpu.Clear();
        }
        _heldBack = null;
        // The line claims the last 60 seconds, so it starts again rather than join two visits.
        GameShareHistory.Clear();
    }

    private void OnMetrics(object? sender, HardwareMetrics metrics)
    {
        // On the monitor's thread: the demoted programs' processor times are read here, beside the
        // monitor's own reading, and only the result goes to the UI thread.
        var heldBack = SampleHeldBack(_demotion.Status.Demoted);
        Application.Current?.Dispatcher.BeginInvoke(() =>
        {
            if (!_live)
            {
                return;
            }
            _metrics = metrics;
            _heldBack = heldBack;
            ShowDemotion(_demotion.Status);
            if (_presence.Current != GamePresence.NotRunning)
            {
                GameShareHistory.Append(metrics.GameCpuPercent);
            }
            Refresh();
        });
    }

    /// <summary>
    /// Each demoted process that is still running, with its share of the processor since the last
    /// sample; null for one seen for the first time, which has nothing to compare against yet. The
    /// service drops a process that has ended only at its next pass, up to 20 seconds on, and
    /// browsers end processes all the time: one that has ended is not held back any more and is left
    /// out. So is one that cannot be opened, because Optima opened every one of them to demote it.
    /// </summary>
    private List<(ProcessStateSnapshot Process, double? Cpu)> SampleHeldBack(IReadOnlyList<ProcessStateSnapshot> demoted)
    {
        lock (_cpuGate)
        {
            var result = new List<(ProcessStateSnapshot Process, double? Cpu)>(demoted.Count);
            if (!_live)
            {
                return result;
            }
            var elapsed = Stopwatch.GetElapsedTime(_sampledAt);
            _sampledAt = Stopwatch.GetTimestamp();
            foreach (var pid in _cpu.Keys.Where(pid => !demoted.Any(p => p.ProcessId == pid)).ToList())
            {
                _cpu[pid].Handle?.Dispose();
                _cpu.Remove(pid);
            }
            foreach (var process in demoted)
            {
                if (!_cpu.TryGetValue(process.ProcessId, out var entry))
                {
                    entry = (ProcessQuery.Open(process.ProcessId), null);
                    // The id may be another process's by now: the page lets go of its handles when it
                    // goes off screen, and the service drops an ended process only at its next pass.
                    if (entry.Handle is { } opened && process.StartTime is { } started && ProcessQuery.GetStartTime(opened) != started)
                    {
                        opened.Dispose();
                        entry = (null, null);
                    }
                }
                if (entry.Handle is { } ended && ProcessQuery.HasExited(ended))
                {
                    ended.Dispose();
                    entry = (null, null);
                }
                // One that has ended stays in the table without a handle, so its id is not opened
                // again as whatever process Windows hands that id to next.
                var now = entry.Handle is { } handle ? ProcessQuery.GetTotalProcessorTime(handle) : null;
                _cpu[process.ProcessId] = (entry.Handle, now);
                if (entry.Handle is not null)
                {
                    result.Add((process, now is { } cpu && entry.Cpu is { } before
                        ? BoostReadout.CpuPercent(cpu - before, elapsed, Environment.ProcessorCount)
                        : null));
                }
            }
            return result;
        }
    }

    /// <summary>The graphics adapters, read once: the inventory behind them does not change while Optima runs.</summary>
    private async Task LoadAdaptersAsync()
    {
        if (_adapters is not null)
        {
            return;
        }
        try
        {
            // The virtual display Optima ships is listed as an adapter too, but the game cannot be
            // put on it; it is left out the way the Home page leaves it out.
            _adapters = [.. (await _systemInfo.GetInventoryAsync()).Gpus
                .Where(g => !g.Name.Contains("Virtual", StringComparison.OrdinalIgnoreCase))];
            Refresh();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "The graphics adapters could not be read for the Boost page");
        }
    }

    /// <summary>
    /// Recomputes the readout from each service's latest status and the latest sample. Runs on the UI
    /// thread on every change and every second while the page is live; all of it is arithmetic on
    /// values already in hand.
    /// </summary>
    private void Refresh()
    {
        // Nobody sees the readout while the page is off screen; SetLive brings it up to date as the
        // page comes back.
        if (!_live)
        {
            return;
        }
        var guard = _guard.Status;
        var cleaner = _cleaner.Status;
        var timer = _timer.Status;
        var demotion = _demotion.Status;
        var extras = _extras.Status;
        var presence = _presence.Current;
        var gameRunning = presence != GamePresence.NotRunning;
        var metrics = _metrics;
        var processors = Environment.ProcessorCount;
        var tally = cleaner.Tally;

        CoresStateText = !KeepCoresAwake ? "OFF" : !BoostOn ? "STANDBY" : extras.CoresHeld || extras.CoresAlreadyAwake ? "AWAKE" : "ARMED";
        GpuStateText = !GpuHighPerformance ? "OFF" : !BoostOn ? "STANDBY" : extras.GpuApplied ? "SET" : "WAITING";
        var working = new[] { GuardStateText, CleanerStateText, TimerStateText, DemotionStateText, CoresStateText, GpuStateText }
            .Count(WorkingStates.Contains);
        BoostPhase = !BoostOn ? "OFF" : presence == GamePresence.InGame ? "ENGAGED" : "ARMED";
        HeaderStatusText = BoostPhase switch
        {
            "OFF" => $"OFF · {BoostFeatureCount} of 6 ticked",
            "ARMED" => $"ARMED · {BoostFeatureCount} of 6 ticked · waiting for Critical Ops",
            _ => $"ENGAGED · {working} of {BoostFeatureCount} working · Critical Ops on screen",
        };

        // The demoted programs, one row per program, the busiest first. The sample counts only the
        // processes still running; until the first one, the service's own count stands in.
        var heldCount = _heldBack?.Count ?? demotion.ProcessCount;
        var programs = (_heldBack ?? [])
            .GroupBy(p => p.Process.ProcessName, StringComparer.OrdinalIgnoreCase)
            .Select(g => (Name: g.Key, Count: g.Count(), Cpu: g.Any(p => p.Cpu is not null) ? g.Sum(p => p.Cpu ?? 0) : (double?)null))
            .OrderByDescending(p => p.Cpu ?? -1)
            .ThenBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var heldBackCpu = programs.Sum(p => p.Cpu ?? 0);
        var heldBackRead = programs.Any(p => p.Cpu is not null);
        var scale = Math.Max(1, heldBackCpu > 0 ? programs.Max(p => p.Cpu ?? 0) : 0);
        HeldBackRows = [.. programs.Take(4).Select(p => new HeldBackProgram(
            p.Name, "×" + p.Count.ToString(CultureInfo.InvariantCulture), p.Cpu is { } c ? Percent(c, "0.0") : Dash, p.Cpu ?? 0, scale))];
        HeldBackEmptyText = programs.Count > 0 ? string.Empty
            : !DemotionEnabled ? "Switched off. Background programs run as usual."
            : !BoostOn ? "Boost is off, so nothing is held back."
            : !demotion.GameOnScreen ? "Nothing is held back until Critical Ops is on screen."
            : heldCount == 0 ? "None of the listed programs is running."
            : "Reading what they use. The first figures arrive within two seconds.";

        // Resource flow: the processor and memory, split.
        var gameCpu = gameRunning && metrics is { } sample ? sample.GameCpuPercent : 0;
        if (metrics is { } m)
        {
            var cpu = BoostReadout.SplitProcessor(m.CpuUtilizationPercent, gameCpu, heldBackCpu);
            ProcessorFlow = new FlowBar(
                gameRunning ? $"game {Percent(cpu.Game, "0")} · busy {Percent(m.CpuUtilizationPercent, "0")}" : $"busy {Percent(m.CpuUtilizationPercent, "0")} · no game",
                Part(cpu.Game, "COPS " + Percent(cpu.Game, "0")),
                Part(cpu.HeldBack, Percent(cpu.HeldBack, "0.0")),
                Part(cpu.Other, "other " + Percent(cpu.Other, "0")),
                Part(0, string.Empty),
                Part(0, string.Empty),
                Part(cpu.Idle, "idle"));
        }
        else
        {
            ProcessorFlow = FlowBar.Unmeasured;
        }

        var standbyMb = cleaner.State == StandbyCleanerState.Running ? cleaner.StandbyMb : null;
        if (metrics is { RamTotalBytes: > 0 } r)
        {
            var freedMb = standbyMb is not null && cleaner.LastPurgeAt is { } at && DateTimeOffset.Now - at < FreedShown ? cleaner.LastFreedMb : 0;
            var memory = BoostReadout.SplitMemory(r.RamTotalBytes, r.RamUsedBytes, gameRunning ? r.GameRamBytes : 0, standbyMb, freedMb);
            var totalGb = r.RamTotalBytes / Gb;
            string Size(double share) => (share / 100 * totalGb).ToString("0.0", CultureInfo.InvariantCulture) + " GB";
            MemoryFlow = new FlowBar(
                tally.TotalFreedMb > 0 ? MbOrGb(tally.TotalFreedMb) + " freed this run" : totalGb.ToString("0", CultureInfo.InvariantCulture) + " GB in all",
                Part(memory.Game, "COPS " + Size(memory.Game)),
                Part(0, string.Empty),
                Part(memory.Other, "in use " + Size(memory.Other)),
                Part(memory.Standby, "standby " + Size(memory.Standby)),
                Part(memory.Freed, "freed"),
                Part(memory.Free, (standbyMb is null ? "available " : "free ") + Size(memory.Free)));
            CleanerStandbyText = standbyMb is { } standby ? "standby " + MbOrGb(standby) : "standby " + Dash;
            CleanerFreeText = standbyMb is not null && cleaner.FreeMb is { } free
                ? "free " + MbOrGb(free)
                : "available " + MbOrGb((long)((r.RamTotalBytes - Math.Min(r.RamUsedBytes, r.RamTotalBytes)) / (1024 * 1024)));
        }
        else
        {
            MemoryFlow = FlowBar.Unmeasured;
            CleanerStandbyText = "standby " + Dash;
            CleanerFreeText = "free " + Dash;
        }
        var now = DateTimeOffset.Now;
        PurgeTicks = [.. tally.Recent
            .Where(at => now - at < PurgeWindow && at <= now)
            .Select(at => Math.Round(1 - (now - at) / PurgeWindow, 3))
            .Select(f => new PurgeTick(new GridLength(f, GridUnitType.Star), new GridLength(1 - f, GridUnitType.Star)))];

        GameShareText = !gameRunning ? "Critical Ops is not running"
            : metrics is null ? "not measured yet"
            : GameShareHistory.Count == 0 ? "now " + Percent(gameCpu, "0")
            : $"now {Percent(gameCpu, "0")} · peak {Percent(GameShareHistory.Max(), "0")}";

        // The priority ladder, lit on the chosen rung while the guard holds it.
        var holding = GuardStateText == "HOLDING";
        string[] rungNames = ["Unchanged", "Normal", "Above", "High"];
        Rungs = [.. GamePriorityOptions.Select((option, i) => new LadderRung(rungNames[i], 18 + 12 * i, option == GamePriority, holding && option == GamePriority))];

        // The timer. Windows' clock is the finest that any program asks for, often another program's.
        // Shown as Optima's, it credited Boost with a gain another program was making, at nearly
        // full bar with Boost off; it is given as a note instead.
        var tickNow = BoostReadout.TimerTickMs(timer.Holding, timer.RequestedMs, timer.CurrentMs, timer.ReachesGame);
        var otherProgram = timer.CurrentMs > 0 && timer.CurrentMs < tickNow * 0.9
            ? $" · another program asks for {Ms(timer.CurrentMs)} ms"
            : string.Empty;

        // The timer combs: Windows' default tick above, what is held (or what Windows gives Optima now) below.
        var combMs = timer.Holding || timer.CurrentMs > 0 ? tickNow : 0;
        TimerHeldLive = timer.Holding;
        TimerHeldText = combMs <= 0 ? "now " + Dash : (timer.Holding ? "held " : "now ") + Ms(combMs) + " ms";
        TimerHeldTick = new Rect(0, 0, combMs > 0 ? Math.Min(1, combMs / CombSpanMs) : 1, 1);
        TimerHeldBox = new Rect(0, 0, Math.Max(CombTickMs * 1.5, combMs > 0 ? combMs : CombSpanMs), 1);

        var awake = BoostOn && (extras.CoresHeld || extras.CoresAlreadyAwake);
        CoreCells = [.. Enumerable.Repeat(awake, processors)];
        CoresCellsText = awake
            ? $"all {processors} logical processors awake"
            : $"{processors} logical processors · which are parked is not read";

        // By maker, not memory: Windows reports an adapter's memory in 32 bits, so every card with
        // 4 GB or more reads as 4 GB.
        AdapterRows = _adapters is null ? [] : [.. _adapters.Select(a => new AdapterRow(
            a.Name,
            _adapters.Count == 1 ? "the only adapter"
                : a.Vendor switch { GpuVendor.Nvidia => "NVIDIA", GpuVendor.Amd => "AMD", GpuVendor.Intel => "Intel", _ => string.Empty }))];
        GpuPreferenceSet = BoostOn && extras.GpuApplied;
        GpuPreferenceText = GpuPreferenceSet && extras.GamePath is { Length: > 0 } path
            ? "High performance for " + System.IO.Path.GetFileName(path)
            : GpuHighPerformance && BoostOn ? "Not set yet: the game has not been seen running"
            : "No preference: Windows chooses";

        Tiles =
        [
            metrics is null ? new BoostTile("CPU TO THE GAME", Dash, string.Empty, "read once a second while this page is open", 0, false)
                : !gameRunning ? new BoostTile("CPU TO THE GAME", Dash, string.Empty, "Critical Ops is not running", 0, false)
                : new BoostTile("CPU TO THE GAME", gameCpu.ToString("0", CultureInfo.InvariantCulture), "%",
                    $"of the whole processor · {Percent(metrics.Value.CpuUtilizationPercent, "0")} busy in all", gameCpu, true),

            !DemotionEnabled ? new BoostTile("PROGRAMS HELD BACK", Dash, string.Empty, "switched off · they run as usual", 0, false)
                : !BoostOn ? new BoostTile("PROGRAMS HELD BACK", Dash, string.Empty, "Boost is off", 0, false)
                : !demotion.GameOnScreen ? new BoostTile("PROGRAMS HELD BACK", Dash, string.Empty, "waiting for Critical Ops", 0, false)
                : new BoostTile("PROGRAMS HELD BACK", heldCount.ToString(CultureInfo.InvariantCulture),
                    heldCount == 1 ? "process" : "processes",
                    heldCount == 0 ? "none of the listed programs is running"
                    : heldBackRead ? Percent(heldBackCpu, "0.0") + " of the processor left to them"
                    : "reading what they use", heldBackCpu, true),

            tally.Purges > 0 || cleaner.State == StandbyCleanerState.Running
                ? new BoostTile("MEMORY RECLAIMED", tally.TotalFreedMb.ToString("N0", CultureInfo.InvariantCulture), "MB",
                    tally.Purges == 0 ? "watching · nothing has needed purging yet"
                    : $"{tally.Purges} purge{(tally.Purges == 1 ? string.Empty : "s")} this run · last "
                        + (cleaner.LastPurgeAt?.ToString("HH:mm", CultureInfo.InvariantCulture) ?? "?"),
                    metrics is { RamTotalBytes: > 0 } total ? 100.0 * tally.TotalFreedMb * 1024 * 1024 / total.RamTotalBytes : 0,
                    cleaner.State == StandbyCleanerState.Running)
                : new BoostTile("MEMORY RECLAIMED", Dash, string.Empty,
                    !CleanerEnabled ? "switched off · the standby list is left alone"
                    : !BoostOn ? "Boost is off"
                    : cleaner.State == StandbyCleanerState.WaitingForHelper ? "the helper is not running"
                    : "waiting for Critical Ops", 0, false),

            timer.Holding
                ? new BoostTile("TIMER RESOLUTION", Ms(tickNow), "ms",
                    $"{BoostReadout.FinerBy(tickNow).ToString("0.#", CultureInfo.InvariantCulture)}× finer than Windows' 15.6 ms"
                        + (timer.ReachesGame ? string.Empty : " · inside Optima only") + otherProgram,
                    100 * (1 - tickNow / BoostReadout.WindowsTickMs), true)
                : timer.CurrentMs > 0
                    ? new BoostTile("TIMER RESOLUTION", Ms(tickNow), "ms",
                        (timer.ReachesGame ? "Windows' tick now" : "Windows' default tick") + " · nothing held" + otherProgram, 0, false)
                    : new BoostTile("TIMER RESOLUTION", Dash, string.Empty, "not read yet", 0, false),

            awake
                ? new BoostTile("CORES AWAKE", processors.ToString(CultureInfo.InvariantCulture), "/ " + processors.ToString(CultureInfo.InvariantCulture),
                    extras.CoresHeld ? "parking off · the plan's own value returns after" : "the power plan already keeps them awake", 100, extras.CoresHeld)
                : new BoostTile("CORES AWAKE", Dash, "/ " + processors.ToString(CultureInfo.InvariantCulture),
                    !KeepCoresAwake ? "switched off · Windows parks idle cores" : !BoostOn ? "Boost is off" : "waiting for Critical Ops", 0, false),

            holding
                ? new BoostTile("GAME PRIORITY", guard.Wanted == ProcessPriorityLevel.AboveNormal ? "Above normal" : guard.Wanted.ToString(), string.Empty,
                    $"held since {guard.GuardingSince?.ToString("HH:mm", CultureInfo.InvariantCulture) ?? "now"} · "
                        + (guard.Corrections == 0 ? "no corrections needed" : $"{guard.Corrections} correction{(guard.Corrections == 1 ? string.Empty : "s")}"),
                    100.0 * (int)guard.Wanted / 3, true)
                : new BoostTile("GAME PRIORITY", Dash, string.Empty, GuardStateText switch
                {
                    "OFF" => "switched off",
                    "STANDBY" => "Boost is off",
                    "IDLE" => "no priority chosen",
                    "SESSION" => "kept by the running Optima session",
                    _ => GamePriority + " once Critical Ops starts",
                }, 0, false),
        ];

        static FlowSegment Part(double share, string label) => new(new GridLength(Math.Max(0, share), GridUnitType.Star), label);
        static string Percent(double value, string format) => value.ToString(format, CultureInfo.InvariantCulture) + "%";
        // Windows' default tick is 15.625 ms, written 15.6 everywhere on the page.
        static string Ms(double ms) => (ms >= BoostReadout.WindowsTickMs ? Math.Round(ms, 1) : ms).ToString("0.0##", CultureInfo.InvariantCulture);
        static string MbOrGb(long mb) => mb >= 1024
            ? (mb / 1024.0).ToString("0.0", CultureInfo.InvariantCulture) + " GB"
            : mb.ToString("N0", CultureInfo.InvariantCulture) + " MB";
    }
}

/// <summary>One of the six numbers beside the dial. Bar is out of 100; Live lights the tile's edge.</summary>
public sealed record BoostTile(string Kicker, string Value, string Unit, string Line, double Bar, bool Live);

/// <summary>One part of a resource bar: its share as a star width, and its label, which the bar shows only where it fits whole.</summary>
public sealed record FlowSegment(GridLength Width, string Label);

/// <summary>
/// A stacked resource bar. The processor fills game, held back, other and idle; memory fills game,
/// other, standby, freed and free (in Idle). The parts are star widths that add up to 100.
/// </summary>
public sealed record FlowBar(string Note, FlowSegment Game, FlowSegment HeldBack, FlowSegment Other, FlowSegment Standby, FlowSegment Freed, FlowSegment Idle)
{
    private static readonly FlowSegment None = new(new GridLength(0, GridUnitType.Star), string.Empty);

    public static FlowBar Unmeasured { get; } = new("not measured yet", None, None, None, None, None,
        new FlowSegment(new GridLength(1, GridUnitType.Star), "measured while this window is open"));
}

/// <summary>One rung of the priority ladder; Height is in pixels.</summary>
public sealed record LadderRung(string Name, double Height, bool Chosen, bool Lit);

/// <summary>One demoted program: how many of its processes are held back and what they use together.</summary>
public sealed record HeldBackProgram(string Name, string CountText, string CpuText, double Cpu, double Scale);

/// <summary>A purge on the cleaner's half-hour line, placed by the time before and after it.</summary>
public sealed record PurgeTick(GridLength Before, GridLength After);

public sealed record AdapterRow(string Name, string Tag);
