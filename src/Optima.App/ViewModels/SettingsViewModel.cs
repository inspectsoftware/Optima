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
    public IReadOnlyList<string> CornerOptions { get; } = ["TopLeft", "TopRight", "BottomLeft", "BottomRight"];
    public IReadOnlyList<double> OpacityOptions { get; } = [0.5, 0.65, 0.8, 1.0];

    [ObservableProperty] private string _provider = "Auto";
    [ObservableProperty] private bool _enableFrametimeCapture = true;
    [ObservableProperty] private string _logLevel = "Information";
    [ObservableProperty] private bool _developerMode;
    [ObservableProperty] private bool _keepInTrayOnClose;
    [ObservableProperty] private bool _overlayEnabled;
    [ObservableProperty] private string _overlayCorner = "TopRight";
    [ObservableProperty] private double _overlayOpacity = 0.8;
    [ObservableProperty] private bool _overlayShowNetwork = true;
    [ObservableProperty] private string _networkReferenceHost = "1.1.1.1";
    [ObservableProperty] private bool _enableWatchMode;
    [ObservableProperty] private bool _useMockMetricsProvider;
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
        KeepInTrayOnClose = settings.KeepInTrayOnClose;
        OverlayEnabled = settings.OverlayEnabled;
        OverlayCorner = settings.OverlayCorner;
        OverlayOpacity = settings.OverlayOpacity;
        OverlayShowNetwork = settings.OverlayShowNetwork;
        NetworkReferenceHost = settings.NetworkReferenceHost;
        EnableWatchMode = settings.EnableWatchMode;
        UseMockMetricsProvider = settings.UseMockMetricsProvider;
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
            KeepInTrayOnClose = KeepInTrayOnClose,
            OverlayEnabled = OverlayEnabled,
            OverlayCorner = OverlayCorner,
            OverlayOpacity = OverlayOpacity,
            OverlayShowNetwork = OverlayShowNetwork,
            NetworkReferenceHost = string.IsNullOrWhiteSpace(NetworkReferenceHost) ? "1.1.1.1" : NetworkReferenceHost.Trim(),
            EnableWatchMode = EnableWatchMode,
            UseMockMetricsProvider = UseMockMetricsProvider,
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
