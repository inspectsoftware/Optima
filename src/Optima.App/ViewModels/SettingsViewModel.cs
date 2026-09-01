using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Optima.Core.Configuration;
using Optima.Core.Models;
using Optima.Core.Theming;

namespace Optima.App.ViewModels;

/// <summary>One selectable accent preset on the SETTINGS page.</summary>
public sealed record AccentPreset(string Name, string Hex);

/// <summary>SETTINGS page (§21/§28/§29): appearance, app options, detection overrides.</summary>
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
    public IReadOnlyList<string> ThemeOptions { get; } = ["Dark", "Light"];

    public IReadOnlyList<AccentPreset> AccentPresets { get; } =
    [
        new("Aureum Gold", "#E8B45A"),
        new("Frost", "#6FB7E8"),
        new("Mint", "#7FD6A4"),
        new("Rose", "#E88A9E"),
        new("Violet", "#A98BE8"),
        new("Slate", "#C7CFDD"),
    ];

    [RelayCommand]
    private void SelectAccent(AccentPreset preset) => AccentColor = preset.Hex;

    [ObservableProperty] private string _theme = "Dark";
    [ObservableProperty] private string _accentColor = AccentMath.DefaultAccentHex;
    [ObservableProperty] private string _playerIgn = string.Empty;
    [ObservableProperty] private bool _discordPresenceEnabled = true;
    [ObservableProperty] private bool _discordPresenceInLauncher = true;
    [ObservableProperty] private string _discordApplicationId = string.Empty;

    [ObservableProperty] private string _provider = "Auto";
    [ObservableProperty] private bool _enableFrametimeCapture = true;
    [ObservableProperty] private string _logLevel = "Information";
    [ObservableProperty] private bool _developerMode;
    [ObservableProperty] private bool _keepInTrayOnClose;
    [ObservableProperty] private bool _followWindowsMotion = true;
    [ObservableProperty] private bool _startWithWindows;
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
        Theme = settings.Theme;
        AccentColor = settings.AccentColor;
        PlayerIgn = settings.PlayerIgn;
        DiscordPresenceEnabled = settings.DiscordPresenceEnabled;
        DiscordPresenceInLauncher = settings.DiscordPresenceInLauncher;
        DiscordApplicationId = settings.DiscordApplicationId;
        Provider = settings.VirtualDisplayProvider;
        EnableFrametimeCapture = settings.EnableFrametimeCapture;
        LogLevel = settings.MinimumLogLevel;
        DeveloperMode = settings.DeveloperMode;
        KeepInTrayOnClose = settings.KeepInTrayOnClose;
        FollowWindowsMotion = settings.FollowWindowsMotion;
        StartWithWindows = settings.StartWithWindows;
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
        var accentValid = AccentMath.TryParse(AccentColor) is not null;

        await _settings.UpdateSettingsAsync(s => s with
        {
            Theme = Theme,
            AccentColor = accentValid ? AccentColor.Trim() : s.AccentColor,
            PlayerIgn = PlayerIgn.Trim(),
            DiscordPresenceEnabled = DiscordPresenceEnabled,
            DiscordPresenceInLauncher = DiscordPresenceInLauncher,
            DiscordApplicationId = DiscordApplicationId.Trim(),
            VirtualDisplayProvider = Provider,
            EnableFrametimeCapture = EnableFrametimeCapture,
            MinimumLogLevel = LogLevel,
            DeveloperMode = DeveloperMode,
            KeepInTrayOnClose = KeepInTrayOnClose,
            FollowWindowsMotion = FollowWindowsMotion,
            StartWithWindows = StartWithWindows,
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

        var autostartError = ApplyStartWithWindows();

        App.LogLevelSwitch.MinimumLevel = LogsViewModel.ToSerilogLevel(LogLevel);
        if (!accentValid)
        {
            AccentColor = (await _settings.GetSettingsAsync()).AccentColor;
            StatusMessage = "Settings saved. Accent color was not a valid hex value, so the previous accent was kept.";
        }
        else
        {
            StatusMessage = autostartError is null
                ? "Settings saved."
                : "Settings saved, but the start-with-Windows entry could not be updated: " + autostartError;
        }
    }

    private string? ApplyStartWithWindows() => Services.AutostartService.Apply(StartWithWindows);
}
