using System.Windows;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Optima.Core.Configuration;
using Optima.Core.Launch;
using Optima.Core.Models;
using Microsoft.Extensions.Logging;

namespace Optima.App.ViewModels;

/// <summary>PLAY page: profile choice, the big button, live pipeline progress, session result (§3/§5/§32).</summary>
public sealed partial class PlayViewModel : ObservableObject
{
    private readonly LaunchOrchestrator _orchestrator;
    private readonly ProfileService _profiles;
    private readonly SettingsService _settings;
    private readonly StatusViewModel _status;
    private readonly ILogger<PlayViewModel> _logger;
    private CancellationTokenSource? _sessionCts;
    private DispatcherTimer? _elapsedTimer;

    public PlayViewModel(
        LaunchOrchestrator orchestrator,
        ProfileService profiles,
        SettingsService settings,
        StatusViewModel status,
        ILogger<PlayViewModel> logger)
    {
        _orchestrator = orchestrator;
        _profiles = profiles;
        _settings = settings;
        _status = status;
        _logger = logger;
        _orchestrator.ProgressChanged += OnProgress;
    }

    public System.Collections.ObjectModel.ObservableCollection<LaunchProfile> Profiles { get; } = [];

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
    private TimeSpan _sessionElapsed;

    /// <summary>Title-bar state tag, visible from every page.</summary>
    public string SessionTag => IsSessionActive
        ? $"[ RUNNING {SessionElapsed:mm\\:ss} ]"
        : "[ IDLE ]";

    [ObservableProperty]
    private string _phaseText = string.Empty;

    [ObservableProperty]
    private LaunchPhase _phase = LaunchPhase.Idle;

    [ObservableProperty]
    private SessionRecord? _lastSession;

    [ObservableProperty]
    private UserFriendlyError? _lastError;

    public string PlayButtonText => IsSessionActive ? "CRITICAL OPS RUNNING" : "PLAY CRITICAL OPS";

    public string SelectedProfileSummary => SelectedProfile is null
        ? string.Empty
        : SelectedProfile.Display.VirtualDisplay
            ? $"{SelectedProfile.Display.Mode} virtual display · {SelectedProfile.Performance.PowerPlan} power plan"
            : $"Physical display · {SelectedProfile.Performance.PowerPlan} power plan";

    public async Task InitializeAsync(CancellationToken ct = default)
    {
        Profiles.Clear();
        foreach (var profile in await _profiles.GetProfilesAsync(ct))
        {
            Profiles.Add(profile);
        }

        var settings = await _settings.GetSettingsAsync(ct);
        SelectedProfile = Profiles.FirstOrDefault(p =>
            string.Equals(p.Name, settings.SelectedProfileName, StringComparison.OrdinalIgnoreCase)) ?? Profiles.FirstOrDefault();
    }

    partial void OnSelectedProfileChanged(LaunchProfile? value)
    {
        if (value is not null)
        {
            _ = _settings.UpdateSettingsAsync(s => s with { SelectedProfileName = value.Name });
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
            }
            else if (result.Error is { Code: not "CANCELLED" })
            {
                LastError = result.Error;
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

    private void OnProgress(object? sender, LaunchProgress progress)
    {
        Application.Current?.Dispatcher.BeginInvoke(() =>
        {
            Phase = progress.Phase;
            PhaseText = progress.Message;
        });
    }
}
