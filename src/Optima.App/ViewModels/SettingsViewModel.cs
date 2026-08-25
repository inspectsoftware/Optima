using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Optima.Core.Configuration;
using Optima.Core.Models;

namespace Optima.App.ViewModels;

/// <summary>SETTINGS page (§21/§28/§29): app options, detection overrides, developer mode toggle.</summary>
public sealed partial class SettingsViewModel : ObservableObject
{
    private readonly SettingsService _settings;

    public SettingsViewModel(SettingsService settings)
    {
        _settings = settings;
    }

    public IReadOnlyList<string> ProviderOptions { get; } = ["Auto", "MttVdd", "Mock"];
    public IReadOnlyList<string> LogLevelOptions { get; } = ["Trace", "Debug", "Information", "Warning", "Error"];

    [ObservableProperty] private string _provider = "Auto";
    [ObservableProperty] private bool _enableFrametimeCapture = true;
    [ObservableProperty] private string _logLevel = "Information";
    [ObservableProperty] private bool _developerMode;
    [ObservableProperty] private string _vddSettingsPath = string.Empty;
    [ObservableProperty] private string _manualInstallPath = string.Empty;
    [ObservableProperty] private string _customLaunchCommand = string.Empty;
    [ObservableProperty] private string _statusMessage = string.Empty;

    public async Task InitializeAsync(CancellationToken ct = default)
    {
        var settings = await _settings.GetSettingsAsync(ct);
        Provider = settings.VirtualDisplayProvider;
        EnableFrametimeCapture = settings.EnableFrametimeCapture;
        LogLevel = settings.MinimumLogLevel;
        DeveloperMode = settings.DeveloperMode;
        VddSettingsPath = settings.VddSettingsPath ?? string.Empty;

        var rules = await _settings.GetDetectionRulesAsync(ct);
        ManualInstallPath = rules.ManualInstallPath ?? string.Empty;
        CustomLaunchCommand = rules.CustomLaunchCommand ?? string.Empty;
    }

    [RelayCommand]
    private async Task SaveAsync()
    {
        await _settings.UpdateSettingsAsync(s => s with
        {
            VirtualDisplayProvider = Provider,
            EnableFrametimeCapture = EnableFrametimeCapture,
            MinimumLogLevel = LogLevel,
            DeveloperMode = DeveloperMode,
            VddSettingsPath = string.IsNullOrWhiteSpace(VddSettingsPath) ? null : VddSettingsPath.Trim(),
        });

        var rules = await _settings.GetDetectionRulesAsync();
        await _settings.SaveDetectionRulesAsync(rules with
        {
            ManualInstallPath = string.IsNullOrWhiteSpace(ManualInstallPath) ? null : ManualInstallPath.Trim(),
            CustomLaunchCommand = string.IsNullOrWhiteSpace(CustomLaunchCommand) ? null : CustomLaunchCommand.Trim(),
        });

        App.LogLevelSwitch.MinimumLevel = LogsViewModel.ToSerilogLevel(LogLevel);
        StatusMessage = "Settings saved.";
    }
}
