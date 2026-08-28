using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Optima.Core.Abstractions;
using Optima.Core.Configuration;
using Optima.Core.Models;
using Microsoft.Extensions.Logging;

namespace Optima.App.ViewModels;

public enum StatusKind
{
    Unknown,
    Good,
    Warning,
    Bad,
}

/// <summary>One row of the STATUS panel.</summary>
public sealed partial class StatusItem : ObservableObject
{
    public StatusItem(string label)
    {
        Label = label;
    }

    public string Label { get; }

    [ObservableProperty]
    private string _value = "····";

    [ObservableProperty]
    private StatusKind _kind = StatusKind.Unknown;
}

/// <summary>Shared environment status shown on HOME and in the sidebar footer (§3).</summary>
public sealed partial class StatusViewModel : ObservableObject
{
    private readonly IGameDetector _detector;
    private readonly IVirtualDisplayProvider _virtualDisplay;
    private readonly IDriverInstaller _driverInstaller;
    private readonly ISystemInfoService _systemInfo;
    private readonly IProcessMonitor _processMonitor;
    private readonly SettingsService _settings;
    private readonly ILogger<StatusViewModel> _logger;

    public StatusViewModel(
        IGameDetector detector,
        IVirtualDisplayProvider virtualDisplay,
        IDriverInstaller driverInstaller,
        ISystemInfoService systemInfo,
        IProcessMonitor processMonitor,
        SettingsService settings,
        ILogger<StatusViewModel> logger)
    {
        _detector = detector;
        _virtualDisplay = virtualDisplay;
        _driverInstaller = driverInstaller;
        _systemInfo = systemInfo;
        _processMonitor = processMonitor;
        _settings = settings;
        _logger = logger;
    }

    public StatusItem GooglePlayGames { get; } = new("Google Play Games");
    public StatusItem CriticalOps { get; } = new("Critical Ops");
    public StatusItem VirtualDisplay { get; } = new("Optima Virtualization");
    public StatusItem Virtualization { get; } = new("VT-x");
    public StatusItem Display { get; } = new("Display");

    [ObservableProperty]
    private bool _gameIsRunning;

    public InstalledGame? DetectedGame { get; private set; }
    public GooglePlayGamesInstallation? DetectedPlatform { get; private set; }

    [RelayCommand]
    public async Task RefreshAsync(CancellationToken ct = default)
    {
        try
        {
            // Values render inside bracket tags, so status words are upper-case by design.
            DetectedPlatform = await _detector.DetectPlatformAsync(ct);
            Set(GooglePlayGames,
                DetectedPlatform is null ? "NOT FOUND" : DetectedPlatform.ServiceRunning ? "READY" : "INSTALLED",
                DetectedPlatform is null ? StatusKind.Bad : StatusKind.Good);

            DetectedGame = await _detector.DetectTargetGameAsync(ct);
            Set(CriticalOps,
                DetectedGame is null ? "NOT INSTALLED" : "INSTALLED",
                DetectedGame is null ? StatusKind.Bad : StatusKind.Good);

            // Device presence is the authority. A leftover settings file from a driver that
            // has since been uninstalled would otherwise report READY with no device at all.
            var driverState = await _driverInstaller.GetStateAsync(ct);
            if (driverState != DriverState.Installed)
            {
                Set(VirtualDisplay,
                    driverState == DriverState.NotInstalledPackageAvailable ? "NOT INSTALLED" : "NO DRIVER",
                    StatusKind.Warning);
            }
            else
            {
                var vddActive = await _virtualDisplay.IsDisplayActiveAsync(ct);
                Set(VirtualDisplay, vddActive ? "ACTIVE" : "READY", StatusKind.Good);
            }

            var virtualization = await _systemInfo.GetVirtualizationStateAsync(ct);
            var virtOk = virtualization.HypervisorPresent == true || virtualization.FirmwareVirtualizationEnabled == true;
            Set(Virtualization,
                virtOk ? "ENABLED" : "DISABLED",
                virtOk ? StatusKind.Good : StatusKind.Bad);

            // Not a status word but a measurement, so it keeps its natural casing.
            // The readout answers "which display drives the FPS cap": the virtual display
            // whenever it is attached (that is where the game renders during an uncapped
            // session), otherwise the physical primary.
            var overrides = (await _settings.GetSettingsAsync(ct)).DisplayOverrides;
            var virtualInfo = await _virtualDisplay.GetDisplayInfoAsync(ct);
            if (virtualInfo is not null)
            {
                var name = DisplayPresentation.CustomName(virtualInfo, overrides) ?? "virtual";
                Set(Display, $"{virtualInfo.CurrentMode} on {name}", StatusKind.Good);
            }
            else
            {
                var displays = (await _systemInfo.GetInventoryAsync(ct)).Displays;
                var primary = displays.FirstOrDefault(d => d.IsPrimary) ?? displays.FirstOrDefault(d => d.IsActive);
                var name = primary is null ? null : DisplayPresentation.CustomName(primary, overrides) ?? "primary";
                Set(Display,
                    primary is null ? "UNKNOWN" : $"{primary.CurrentMode} on {name}",
                    primary is null ? StatusKind.Warning : StatusKind.Good);
            }

            GameIsRunning = await _processMonitor.GetGameStateAsync(ct) == GameRuntimeState.Running;
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Status refresh failed");
        }
    }

    private static void Set(StatusItem item, string value, StatusKind kind)
    {
        item.Value = value;
        item.Kind = kind;
    }
}
