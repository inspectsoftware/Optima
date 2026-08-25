using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using COPSBootstrapper.Core.Abstractions;
using COPSBootstrapper.Core.Models;
using Microsoft.Extensions.Logging;

namespace COPSBootstrapper.App.ViewModels;

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
    private string _value = "Checking…";

    [ObservableProperty]
    private StatusKind _kind = StatusKind.Unknown;
}

/// <summary>Shared environment status shown on HOME and in the sidebar footer (§3).</summary>
public sealed partial class StatusViewModel : ObservableObject
{
    private readonly IGameDetector _detector;
    private readonly IVirtualDisplayProvider _virtualDisplay;
    private readonly ISystemInfoService _systemInfo;
    private readonly IProcessMonitor _processMonitor;
    private readonly ILogger<StatusViewModel> _logger;

    public StatusViewModel(
        IGameDetector detector,
        IVirtualDisplayProvider virtualDisplay,
        ISystemInfoService systemInfo,
        IProcessMonitor processMonitor,
        ILogger<StatusViewModel> logger)
    {
        _detector = detector;
        _virtualDisplay = virtualDisplay;
        _systemInfo = systemInfo;
        _processMonitor = processMonitor;
        _logger = logger;
    }

    public StatusItem GooglePlayGames { get; } = new("Google Play Games");
    public StatusItem CriticalOps { get; } = new("Critical Ops");
    public StatusItem VirtualDisplay { get; } = new("Virtual Display");
    public StatusItem Virtualization { get; } = new("Virtualization");
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
            DetectedPlatform = await _detector.DetectPlatformAsync(ct);
            Set(GooglePlayGames,
                DetectedPlatform is null ? "Not found" : DetectedPlatform.ServiceRunning ? "Ready" : "Installed",
                DetectedPlatform is null ? StatusKind.Bad : StatusKind.Good);

            DetectedGame = await _detector.DetectTargetGameAsync(ct);
            Set(CriticalOps,
                DetectedGame is null ? "Not installed" : "Installed",
                DetectedGame is null ? StatusKind.Bad : StatusKind.Good);

            var vddAvailable = await _virtualDisplay.IsAvailableAsync(ct);
            var vddActive = vddAvailable && await _virtualDisplay.IsDisplayActiveAsync(ct);
            Set(VirtualDisplay,
                !vddAvailable ? "Not detected" : vddActive ? "Active" : "Ready",
                !vddAvailable ? StatusKind.Warning : StatusKind.Good);

            var virtualization = await _systemInfo.GetVirtualizationStateAsync(ct);
            var virtOk = virtualization.HypervisorPresent == true || virtualization.FirmwareVirtualizationEnabled == true;
            Set(Virtualization,
                virtOk ? "Enabled" : "Disabled",
                virtOk ? StatusKind.Good : StatusKind.Bad);

            var displays = (await _systemInfo.GetInventoryAsync(ct)).Displays;
            var primary = displays.FirstOrDefault(d => d.IsPrimary) ?? displays.FirstOrDefault(d => d.IsActive);
            Set(Display,
                primary is null ? "Unknown" : primary.CurrentMode.ToString(),
                primary is null ? StatusKind.Warning : StatusKind.Good);

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
