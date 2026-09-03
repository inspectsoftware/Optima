using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Optima.Core.Abstractions;
using Optima.Core.Configuration;
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
    private static readonly LaunchPhase[] StepPhases =
    [
        LaunchPhase.Validating,
        LaunchPhase.ApplyingPerformanceProfile,
        LaunchPhase.ConfiguringDisplay,
        LaunchPhase.StartingPlatform,
        LaunchPhase.Restoring,
    ];

    private readonly LaunchOrchestrator _orchestrator;
    private readonly ProfileService _profiles;
    private readonly SettingsService _settings;
    private readonly StatusViewModel _status;
    private readonly IGameTerminator _terminator;
    private readonly ILogger<PlayViewModel> _logger;
    private CancellationTokenSource? _sessionCts;
    private DispatcherTimer? _elapsedTimer;

    public PlayViewModel(
        LaunchOrchestrator orchestrator,
        ProfileService profiles,
        SettingsService settings,
        StatusViewModel status,
        IGameTerminator terminator,
        ILogger<PlayViewModel> logger)
    {
        _orchestrator = orchestrator;
        _profiles = profiles;
        _settings = settings;
        _status = status;
        _terminator = terminator;
        _logger = logger;
        _orchestrator.ProgressChanged += OnProgress;
    }

    public ObservableCollection<LaunchProfile> Profiles { get; } = [];

    public ObservableCollection<ProfileChip> ProfileChips { get; } = [];

    public ObservableCollection<LaunchStep> Steps { get; } =
    [
        new("Launcher resolved", "checking the install"),
        new("Profile applied", "power plan, priority, throttling"),
        new("Virtual display", "as the profile says"),
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
        ? $"RUNNING {SessionElapsed:mm\\:ss}"
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

    public string PlayButtonText => IsSessionActive ? "Running" : "Play Critical Ops";

    public string SelectedProfileSummary => SelectedProfile is null
        ? string.Empty
        : SelectedProfile.Display.VirtualDisplay
            ? $"{SelectedProfile.Display.Mode} virtual display · {SelectedProfile.Performance.PowerPlan} power plan · restored on exit"
            : $"Physical display · {SelectedProfile.Performance.PowerPlan} power plan · restored on exit";

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
        if (SelectedProfile is null)
        {
            return;
        }

        LastError = null;
        LastSession = null;
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
            var result = await Task.Run(() => _orchestrator.RunSessionAsync(profile, _sessionCts.Token));
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
                DeveloperDetails = ex.ToString(),
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
    private void Cancel() => _sessionCts?.Cancel();

    [ObservableProperty]
    private string _killStatusText = string.Empty;

    [RelayCommand]
    private async Task KillGameAsync()
    {
        KillStatusText = "killing...";
        try
        {
            var result = await _terminator.KillGameAsync();
            KillStatusText = result.Message;
            await _status.RefreshAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Kill game failed");
            KillStatusText = "kill failed. See Logs.";
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
        var live = Steps.FirstOrDefault(s => s.State == StepState.Live) ?? Steps[0];
        live.State = StepState.Failed;
    }
}
