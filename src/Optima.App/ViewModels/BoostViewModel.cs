using System.Globalization;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Optima.Core.Configuration;
using Optima.Core.Launch;
using Optima.Core.Abstractions;
using Optima.Core.Boost;
using Optima.Core.Models;
using Optima.Monitoring.Metrics;
using Microsoft.Extensions.Logging;

namespace Optima.App.ViewModels;

/// <summary>
/// BOOST page: what Optima does while the game runs to keep it smooth. Every switch saves as it is
/// flipped, and every section says what it is doing right now rather than what it was asked to do.
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
    private readonly ILogger<BoostViewModel> _logger;
    private bool _loading;
    private bool _subscribed;

    public BoostViewModel(
        SettingsService settings,
        PriorityGuardService guard,
        StandbyCleanerService cleaner,
        TimerResolutionService timer,
        ITimerResolution timerInfo,
        ITweakService tweaks,
        BackgroundDemotionService demotion,
        GameExtrasService extras,
        ILogger<BoostViewModel> logger)
    {
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
    public int BoostFeatureCount
        => new[] { PriorityGuardEnabled, CleanerEnabled, TimerEnabled, DemotionEnabled, KeepCoresAwake, GpuHighPerformance }.Count(on => on);

    public string BoostSummaryText => BoostFeatureCount switch
    {
        0 => "Nothing is ticked below, so there is nothing to arm yet.",
        var n => $"{n} of 6 features ticked below · " + (BoostOn ? "armed" : "switched off"),
    };

    protected override void OnPropertyChanged(System.ComponentModel.PropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (e.PropertyName is nameof(PriorityGuardEnabled) or nameof(CleanerEnabled) or nameof(TimerEnabled)
            or nameof(DemotionEnabled) or nameof(KeepCoresAwake) or nameof(GpuHighPerformance))
        {
            base.OnPropertyChanged(new System.ComponentModel.PropertyChangedEventArgs(nameof(BoostFeatureCount)));
            base.OnPropertyChanged(new System.ComponentModel.PropertyChangedEventArgs(nameof(BoostSummaryText)));
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
        ShowAll();
    }

    private void ShowAll()
    {
        ShowGuard(_guard.Status);
        ShowCleaner(_cleaner.Status);
        ShowTimer(_timer.Status);
        ShowDemotion(_demotion.Status);
        ShowExtras(_extras.Status);
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
            _guard.StatusChanged += status => Application.Current?.Dispatcher.BeginInvoke(() => ShowGuard(status));
            _cleaner.StatusChanged += status => Application.Current?.Dispatcher.BeginInvoke(() => ShowCleaner(status));
            _timer.StatusChanged += status => Application.Current?.Dispatcher.BeginInvoke(() => ShowTimer(status));
            _demotion.StatusChanged += status => Application.Current?.Dispatcher.BeginInvoke(() => ShowDemotion(status));
            _extras.StatusChanged += status => Application.Current?.Dispatcher.BeginInvoke(() => ShowExtras(status));
        }
        ShowExtras(_extras.Status);
        ShowDemotion(_demotion.Status);
        ShowGuard(_guard.Status);
        ShowCleaner(_cleaner.Status);
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
        else if (status.ProcessCount == 0)
        {
            DemotionStateText = "WATCHING";
            DemotionDetailText = "None of the listed programs is running right now.";
        }
        else
        {
            DemotionStateText = "ACTIVE";
            DemotionDetailText =
                $"{status.ProcessCount} process{(status.ProcessCount == 1 ? string.Empty : "es")} of "
                + $"{status.ProgramCount} program{(status.ProgramCount == 1 ? string.Empty : "s")} demoted · put back when the game leaves";
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
        if (!status.Enabled)
        {
            TimerStateText = "OFF";
            TimerDetailText = $"Not holding anything. Windows is at {now} ms for Optima right now.";
        }
        else if (!status.Holding)
        {
            TimerStateText = "ARMED";
            TimerDetailText = $"Waiting for the game. Windows is at {now} ms for Optima right now.";
        }
        else
        {
            TimerStateText = "HOLDING";
            var asked = status.RequestedMs.ToString("0.###", CultureInfo.InvariantCulture);
            var applied = status.AppliedMs?.ToString("0.###", CultureInfo.InvariantCulture) ?? "?";
            TimerDetailText = $"Asked for {asked} ms, Windows applied {applied} ms · "
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
            // Text that is not a number yet (the box is mid-edit) keeps the stored threshold.
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

        static int? ParseThreshold(string text)
            => int.TryParse(text.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var value)
                && value is >= StandbyCleanerPolicy.MinThresholdMb and <= StandbyCleanerPolicy.MaxThresholdMb
                ? value
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
            _ => status.Purges == 0
                ? "Watching. Nothing has needed purging yet."
                : $"Purged {status.Purges} time{(status.Purges == 1 ? string.Empty : "s")} · last at "
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
}
