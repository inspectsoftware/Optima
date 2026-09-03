using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Optima.App.Services;
using Optima.Core.Configuration;

namespace Optima.App.ViewModels;

/// <summary>
/// First-launch setup (§23), rebuilt around the fix-everything engine (Q7): detect, show findings, one consent to run
/// every automatable fix, an honest BIOS walkthrough for the firmware-only case, reboot orchestration with automatic
/// resume (first-run stays incomplete until Finish, so the wizard reopens by itself), and the personalize step
/// (autostart pre-checked per Q12, player name, Discord application id).
/// </summary>
public sealed partial class SetupWizardViewModel : ObservableObject
{
    private readonly SettingsService _settings;
    private readonly FirstRunFixService _fix;
    private ReadinessReport? _report;

    public SetupWizardViewModel(
        StatusViewModel status,
        DiagnosticsViewModel diagnostics,
        SettingsService settings,
        FirstRunFixService fix)
    {
        Status = status;
        Diagnostics = diagnostics;
        _settings = settings;
        _fix = fix;
    }

    public StatusViewModel Status { get; }
    public DiagnosticsViewModel Diagnostics { get; }

    public ObservableCollection<string> FixLog { get; } = [];

    [ObservableProperty] private bool _isDetecting = true;
    [ObservableProperty] private string _headline = "Setting things up…";
    [ObservableProperty] private bool _showFixAll;
    [ObservableProperty] private string _fixSummary = string.Empty;
    [ObservableProperty] private bool _fixing;
    [ObservableProperty] private bool _restartNeeded;
    [ObservableProperty] private string _restartStatus = string.Empty;
    [ObservableProperty] private bool _firmwareGuideVisible;

    [ObservableProperty] private bool _startWithWindows = true;
    [ObservableProperty] private string _playerIgn = string.Empty;
    [ObservableProperty] private string _discordApplicationId = string.Empty;

    public event EventHandler? Completed;

    public async Task RunDetectionAsync()
    {
        IsDetecting = true;
        await Status.RefreshAsync();
        await Diagnostics.InitializeAsync();

        var current = await _settings.GetSettingsAsync();
        PlayerIgn = current.PlayerIgn;
        DiscordApplicationId = current.DiscordApplicationId;

        await AnalyzeAsync();
        IsDetecting = false;
        Headline = _report is { AllGood: true }
            ? "Everything found. You're ready to play."
            : "Setup found things it can fix for you.";
    }

    private async Task AnalyzeAsync()
    {
        _report = await _fix.AnalyzeAsync();
        FirmwareGuideVisible = _report.FirmwareVirtualizationOff;
        ShowFixAll = _report.AnythingAutomatable;
        FixSummary = ShowFixAll
            ? "One click runs everything that CAN be automated: " +
              string.Join(" and ", new[]
              {
                  _report.HypervisorFeaturesMissing ? "enable the Windows hypervisor features (one administrator prompt, restart afterwards)" : null,
                  _report.GpgMissing ? "open the official Google Play Games download page" : null,
              }.Where(s => s is not null))
              + "."
            : string.Empty;
    }

    [RelayCommand]
    private async Task FixEverythingAsync()
    {
        if (_report is null || Fixing)
        {
            return;
        }
        Fixing = true;
        try
        {
            var result = await _fix.RunFixesAsync(_report);
            foreach (var line in result.Log)
            {
                FixLog.Add(line);
            }
            RestartNeeded = result.RestartRequired;
            await AnalyzeAsync();
        }
        finally
        {
            Fixing = false;
        }
    }

    [RelayCommand]
    private void RestartNow()
    {
        var error = _fix.ScheduleRestart();
        RestartStatus = error is null
            ? "Restarting in 10 seconds. Optima reopens this setup after the restart."
            : "Restart could not be scheduled: " + error;
    }

    [RelayCommand]
    private async Task FinishAsync()
    {
        await _settings.UpdateSettingsAsync(s => s with
        {
            FirstRunCompleted = true,
            StartWithWindows = StartWithWindows,
            PlayerIgn = PlayerIgn.Trim(),
            DiscordApplicationId = DiscordApplicationId.Trim().Length > 0
                ? DiscordApplicationId.Trim()
                : s.DiscordApplicationId,
        });
        AutostartService.Apply(StartWithWindows);
        FirstRunFixService.ClearResume();
        Completed?.Invoke(this, EventArgs.Empty);
    }
}
