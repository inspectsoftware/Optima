using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Optima.Core.Abstractions;
using Optima.Core.Configuration;
using Optima.Core.Crashes;
using Optima.Core.Launch;
using Optima.Core.Models;
using Microsoft.Extensions.Logging;

namespace Optima.App.ViewModels;

/// <summary>State of one row in the launch step list.</summary>
public enum StepState
{
    Todo,
    Live,
    Done,
    Failed,
}

/// <summary>One launch step shown on the session page.</summary>
public sealed partial class LaunchStep : ObservableObject
{
    public LaunchStep(string title, string detail)
    {
        Title = title;
        _detail = detail;
        _defaultDetail = detail;
    }

    private readonly string _defaultDetail;

    public string Title { get; }

    [ObservableProperty]
    private string _detail;

    [ObservableProperty]
    private StepState _state;

    public void Reset()
    {
        State = StepState.Todo;
        Detail = _defaultDetail;
    }
}

/// <summary>A profile as a selectable chip on the launch surface.</summary>
public sealed partial class ProfileChip : ObservableObject
{
    public ProfileChip(LaunchProfile profile)
    {
        Profile = profile;
    }

    public LaunchProfile Profile { get; }

    public string Name => Profile.Name;

    [ObservableProperty]
    private bool _isSelected;
}

/// <summary>
/// The launch surface (Home) and the session page (Play): profile choice, the PLAY button, live pipeline progress as a
/// step list, session result (§3/§5/§32).
/// </summary>
public sealed partial class PlayViewModel : ObservableObject
{
    private readonly LaunchOrchestrator _orchestrator;
    private readonly ProfileService _profiles;
    private readonly SettingsService _settings;
    private readonly StatusViewModel _status;
    private readonly IGameTerminator _terminator;
    private readonly Core.Launch.CrashAutoRelaunchService _crashRelaunch;
    private readonly PlayGuideViewModel _playGuide;
    private readonly CrashSentinel _crashSentinel;
    private readonly AppPaths _paths;
    private readonly ILogger<PlayViewModel> _logger;
    private readonly Optima.Monitoring.Metrics.StandbyCleanerService _standbyCleaner;
    private readonly Optima.Monitoring.Metrics.EtwMetricsProviderClient _capture;
    private readonly Optima.Core.Protection.ShieldLoader _shield;
    private CancellationTokenSource? _sessionCts;
    private DispatcherTimer? _elapsedTimer;

    public PlayViewModel(
        LaunchOrchestrator orchestrator,
        ProfileService profiles,
        SettingsService settings,
        StatusViewModel status,
        IGameTerminator terminator,
        Core.Launch.CrashAutoRelaunchService crashRelaunch,
        PlayGuideViewModel playGuide,
        CrashSentinel crashSentinel,
        AppPaths paths,
        Optima.Monitoring.Metrics.StandbyCleanerService standbyCleaner,
        Optima.Monitoring.Metrics.EtwMetricsProviderClient capture,
        Optima.Core.Protection.ShieldLoader shield,
        ILogger<PlayViewModel> logger)
    {
        _standbyCleaner = standbyCleaner;
        _capture = capture;
        _shield = shield;
        _orchestrator = orchestrator;
        _profiles = profiles;
        _settings = settings;
        _status = status;
        _terminator = terminator;
        _crashRelaunch = crashRelaunch;
        _playGuide = playGuide;
        _crashSentinel = crashSentinel;
        _paths = paths;
        _logger = logger;
        _orchestrator.ProgressChanged += OnProgress;
        // The line under the profile names the display, and the display is a setting now.
        _settings.SettingsChanged += (_, _) =>
            Application.Current?.Dispatcher.BeginInvoke(() => OnPropertyChanged(nameof(SelectedProfileSummary)));
        // New crash bundles surface on the Play tab as a dismissible banner; dismissal is persisted.
        _crashSentinel.BundleWritten += folder => Application.Current?.Dispatcher.BeginInvoke(() => _ = RefreshCrashBannerAsync());
    }

    public async Task InitializeCrashBannerAsync(CancellationToken ct = default)
    {
        await RefreshCrashBannerAsync(ct);
    }

    private async Task RefreshCrashBannerAsync(CancellationToken ct = default)
    {
        try
        {
            var seen = (await _settings.GetSettingsAsync(ct)).LastCrashSeenAt;
            var newest = Directory.Exists(_paths.CrashesDirectory)
                ? Directory.EnumerateDirectories(_paths.CrashesDirectory)
                    .Select(d => new DirectoryInfo(d))
                    .OrderByDescending(d => d.Name, StringComparer.Ordinal)
                    .FirstOrDefault()
                : null;
            if (newest is null)
            {
                CrashBannerText = string.Empty;
                return;
            }
            var written = newest.LastWriteTime;
            CrashBannerVisible = seen is null || written > seen.Value;
            CrashBundleName = newest.Name;
            CrashBundleTimeText = written.ToLocalTime().ToString("yyyy-MM-dd HH:mm");
            CrashBannerText =
                "Critical Ops ended with failure markers in Google Play Games' logs. A crash bundle was saved with a " +
                "timeline and the relevant log excerpt — export a redacted zip from the Crashes tab on the Debug page to share it.";
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Crash banner refresh failed");
        }
    }

    [RelayCommand]
    private async Task DismissCrashBannerAsync()
    {
        CrashBannerVisible = false;
        await _settings.UpdateSettingsAsync(s => s with { LastCrashSeenAt = DateTimeOffset.UtcNow });
    }

    [ObservableProperty]
    private bool _crashBannerVisible;

    [ObservableProperty]
    private string _crashBannerText = string.Empty;

    [ObservableProperty]
    private string _crashBundleName = string.Empty;

    [ObservableProperty]
    private string _crashBundleTimeText = string.Empty;

    /// <summary>Entry point for the crash auto-relaunch service; reuses the normal launch path.</summary>
    public async Task RelaunchAfterCrashAsync(LaunchProfile? profile)
    {
        if (profile is null)
        {
            return;
        }
        // The exit that brought this here arrives while the crashed session is still putting the
        // PC back. Starting over it was refused as "a session is already running", and the one
        // relaunch was spent on that.
        for (var waited = 0; waited < 60 && (IsSessionActive || _orchestrator.IsSessionActive); waited++)
        {
            await Task.Delay(500).ConfigureAwait(false);
        }

        // Called from the pool. The command and everything bound to it belong to the window's thread.
        await Application.Current.Dispatcher.InvokeAsync(async () =>
        {
            if (IsSessionActive)
            {
                return;
            }
            SelectedProfile = Profiles.FirstOrDefault(p => string.Equals(p.Name, profile.Name, StringComparison.OrdinalIgnoreCase)) ?? profile;
            _unattendedLaunch = true;
            await PlayCommand.ExecuteAsync(null);
        }).Task.Unwrap().ConfigureAwait(false);
    }

    // Set for a launch nobody clicked for. Such a launch never puts a window up.
    private bool _unattendedLaunch;

    public ObservableCollection<LaunchProfile> Profiles { get; } = [];

    public ObservableCollection<ProfileChip> ProfileChips { get; } = [];

    public ObservableCollection<LaunchStep> Steps { get; } =
    [
        new("Launcher resolved", "checking the install"),
        new("Profile applied", "power plan, priority, throttling"),
        new("Virtual display", "as the Display page says"),
        new("Game running", "waiting for the game window"),
        new("Restore on exit", "pending"),
    ];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SelectedProfileSummary))]
    private LaunchProfile? _selectedProfile;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PlayButtonText))]
    [NotifyPropertyChangedFor(nameof(SessionTag))]
    [NotifyCanExecuteChangedFor(nameof(PlayCommand))]
    [NotifyCanExecuteChangedFor(nameof(CancelCommand))]
    private bool _isSessionActive;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SessionTag))]
    [NotifyPropertyChangedFor(nameof(ElapsedText))]
    private TimeSpan _sessionElapsed;

    public string SessionTag => IsSessionActive
        ? $"RUNNING {(int)SessionElapsed.TotalMinutes:00}:{SessionElapsed.Seconds:00}"
        : "IDLE";

    public string ElapsedText => $"{SessionElapsed:hh\\:mm\\:ss}";

    [ObservableProperty]
    private string _phaseText = string.Empty;

    [ObservableProperty]
    private LaunchPhase _phase = LaunchPhase.Idle;

    [ObservableProperty]
    private double _sessionProgress;

    [ObservableProperty]
    private SessionRecord? _lastSession;

    [ObservableProperty]
    private UserFriendlyError? _lastError;

    /// <summary>What the last session went without: steps that failed and were not worth stopping it for.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasLastWarnings))]
    private IReadOnlyList<LaunchWarning> _lastWarnings = [];

    public bool HasLastWarnings => LastWarnings.Count > 0;

    public string PlayButtonText => IsSessionActive ? "Running" : "Play Critical Ops";

    public string SelectedProfileSummary
    {
        get
        {
            if (SelectedProfile is null)
            {
                return string.Empty;
            }
            // The display is the Display page's choice for every profile; a profile's own only
            // counts until that choice exists, which is what the launch does as well.
            var display = _settings.Current is { VirtualDisplayEnabled: not null } settings
                ? settings.EffectiveDisplay
                : SelectedProfile.Display;
            return display.VirtualDisplay
                ? $"{display.Mode} virtual display · {SelectedProfile.Performance.PowerPlan} power plan · restored on exit"
                : $"Your own screen · {SelectedProfile.Performance.PowerPlan} power plan · restored on exit";
        }
    }

    public async Task InitializeAsync(CancellationToken ct = default)
    {
        Profiles.Clear();
        ProfileChips.Clear();
        foreach (var profile in await _profiles.GetProfilesAsync(ct))
        {
            Profiles.Add(profile);
            ProfileChips.Add(new ProfileChip(profile));
        }

        var settings = await _settings.GetSettingsAsync(ct);
        SelectedProfile = Profiles.FirstOrDefault(p =>
            string.Equals(p.Name, settings.SelectedProfileName, StringComparison.OrdinalIgnoreCase)) ?? Profiles.FirstOrDefault();
    }

    partial void OnSelectedProfileChanged(LaunchProfile? value)
    {
        foreach (var chip in ProfileChips)
        {
            chip.IsSelected = value is not null && string.Equals(chip.Profile.Name, value.Name, StringComparison.OrdinalIgnoreCase);
        }
        if (value is not null)
        {
            _ = _settings.UpdateSettingsAsync(s => s with { SelectedProfileName = value.Name });
        }
    }

    [RelayCommand]
    private void SelectProfile(ProfileChip? chip)
    {
        if (chip is not null && !IsSessionActive)
        {
            SelectedProfile = chip.Profile;
        }
    }

    [RelayCommand(CanExecute = nameof(CanPlay))]
    private async Task PlayAsync()
    {
        var unattended = _unattendedLaunch;
        _unattendedLaunch = false;
        if (SelectedProfile is null)
        {
            return;
        }

        LastError = null;
        LastSession = null;
        LastWarnings = [];
        IsSessionActive = true;
        ResetSteps();
        _sessionCts = new CancellationTokenSource();
        var profile = SelectedProfile;

        var startedAt = DateTimeOffset.UtcNow;
        SessionElapsed = TimeSpan.Zero;
        _elapsedTimer = new DispatcherTimer(DispatcherPriority.Normal, Application.Current.Dispatcher)
        {
            Interval = TimeSpan.FromSeconds(1),
        };
        _elapsedTimer.Tick += (_, _) => SessionElapsed = DateTimeOffset.UtcNow - startedAt;
        _elapsedTimer.Start();

        try
        {
            // Frametime capture needs the elevated helper, and would only ask for it once the game
            // is on screen. Asked for here instead, and not waited for: the prompt and the helper's
            // start then overlap the game's own, and the launch is never held up by either. Only
            // for a launch that can go ahead: one that fails at once must not cost a prompt.
            if (_status.DetectedPlatform is not null && _status.DetectedGame is not null && !_orchestrator.IsSessionActive)
            {
                // A PC linked under an older "what protected play does" is shown the current one
                // first. Here and only here, from a click: nothing in the background may put a
                // window up, and a launch that did not come from the window (a relaunch after a
                // crash) does not either. Before the helper is asked for, so the requests below
                // still reach its gate together.
                // And only in a build that has Shield: a PC linked under an earlier build keeps its
                // link file, and a build without the module has nothing to disclose.
                if (_shield.Installed && _shield.NeedsDisclosure && !unattended && Views.ProtectedPlayWindow.Ask())
                {
                    _shield.AcceptDisclosure();
                }
                _ = _capture.EnsureHelperAsync();
                // Protected play wants the helper as well, for the same reason and under the same
                // rule: its request meets the others at the helper's gate, so there is one prompt
                // for all of them. Not waited for either. Without the helper it runs with reduced
                // coverage; on a PC that is not linked it does not run at all.
                _ = _shield.StartAsync("play", allowPrompt: true);
            }
            // BOOST's memory cleaner needs the helper too; PLAY is where its one admin prompt
            // belongs, before the game covers the screen. A no-op with the cleaner off. After the
            // line above, so the two requests meet at the helper's gate and share one prompt.
            await _standbyCleaner.SyncAsync(allowPrompt: true);
            _crashRelaunch.NoteSessionStart(profile);
            var result = await Task.Run(() => _orchestrator.RunSessionAsync(profile, _sessionCts.Token));
            LastWarnings = result.Warnings;
            if (result.Success)
            {
                LastSession = result.Session;
                MarkAllDone();
            }
            else if (result.Error is { Code: not "CANCELLED" })
            {
                LastError = result.Error;
                MarkLiveFailed();
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Session task faulted");
            LastError = new UserFriendlyError
            {
                Code = "UNEXPECTED",
                Title = "Something went wrong during the session.",
                Explanation = "Details were written to the log.",
                DeveloperDetails = Core.Health.ExceptionDetail.Capture(ex).FullText,
            };
            MarkLiveFailed();
        }
        finally
        {
            _elapsedTimer?.Stop();
            _elapsedTimer = null;
            IsSessionActive = false;
            _sessionCts?.Dispose();
            _sessionCts = null;
            await _status.RefreshAsync();
        }
    }

    private bool CanPlay() => !IsSessionActive;

    [RelayCommand(CanExecute = nameof(IsSessionActive))]
    private void Cancel()
    {
        _crashRelaunch.NoteIntentionalExit();
        _sessionCts?.Cancel();
    }

    /// <summary>Opens the five-step Critical Ops on PC setup guide as a dialog, with a fresh PC scan.</summary>
    [RelayCommand]
    private void OpenSetupGuide()
    {
        var window = new Views.PlayGuideWindow
        {
            Owner = Application.Current.MainWindow,
        };
        _playGuide.CurrentStepIndex = 0;
        window.DataContext = _playGuide;
        _ = _playGuide.ScanCommand.ExecuteAsync(null);
        window.ShowDialog();
    }

    [ObservableProperty]
    private string _killStatusText = string.Empty;

    [RelayCommand]
    private async Task KillGameAsync()
    {
        KillStatusText = "killing...";
        try
        {
            _crashRelaunch.NoteIntentionalExit();
            var result = await _terminator.KillGameAsync();
            KillStatusText = result.Message;
            await _status.RefreshAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Kill game failed");
            KillStatusText = "kill failed. See the log on the Debug page.";
        }
    }

    private void OnProgress(object? sender, LaunchProgress progress)
    {
        Application.Current?.Dispatcher.BeginInvoke(() =>
        {
            Phase = progress.Phase;
            PhaseText = progress.Message;
            ApplyPhase(progress.Phase, progress.Message);
        });
    }

    private void ResetSteps()
    {
        foreach (var step in Steps)
        {
            step.Reset();
        }
        SessionProgress = 0;
    }

    private void ApplyPhase(LaunchPhase phase, string message)
    {
        switch (phase)
        {
            case LaunchPhase.Idle:
                return;
            case LaunchPhase.Completed:
                MarkAllDone();
                return;
            case LaunchPhase.Failed:
                MarkLiveFailed();
                return;
        }

        var index = phase switch
        {
            LaunchPhase.Validating => 0,
            LaunchPhase.ApplyingPerformanceProfile => 1,
            LaunchPhase.ConfiguringDisplay => 2,
            LaunchPhase.StartingPlatform or LaunchPhase.WaitingForGame or LaunchPhase.Monitoring => 3,
            LaunchPhase.Restoring => 4,
            _ => -1,
        };
        if (index < 0)
        {
            return;
        }
        for (var i = 0; i < Steps.Count; i++)
        {
            if (i < index)
            {
                Steps[i].State = StepState.Done;
            }
            else if (i == index)
            {
                Steps[i].State = StepState.Live;
                Steps[i].Detail = phase == LaunchPhase.Monitoring ? "running" : message;
            }
        }
        SessionProgress = phase switch
        {
            LaunchPhase.Validating => 0.12,
            LaunchPhase.ApplyingPerformanceProfile => 0.32,
            LaunchPhase.ConfiguringDisplay => 0.52,
            LaunchPhase.StartingPlatform => 0.7,
            LaunchPhase.WaitingForGame => 0.82,
            LaunchPhase.Monitoring => 0.9,
            LaunchPhase.Restoring => 0.96,
            _ => SessionProgress,
        };
    }

    private void MarkAllDone()
    {
        foreach (var step in Steps)
        {
            step.State = StepState.Done;
        }
        Steps[^1].Detail = "everything restored";
        SessionProgress = 1;
    }

    private void MarkLiveFailed()
    {
        // A failure is reported twice (by the phase and by the result); the second report finds no
        // live step and must not blame the first one.
        if (Steps.Any(s => s.State == StepState.Failed))
        {
            return;
        }
        var live = Steps.FirstOrDefault(s => s.State == StepState.Live) ?? Steps[0];
        live.State = StepState.Failed;
    }
}
