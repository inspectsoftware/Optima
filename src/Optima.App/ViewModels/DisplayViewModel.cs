using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Optima.Core.Abstractions;
using Optima.Core.Models;
using Optima.Driver;
using Microsoft.Extensions.Logging;

namespace Optima.App.ViewModels;

/// <summary>
/// DISPLAY page (§6/§7): attached displays, virtual display control, mode presets and custom
/// modes, with a safety net: the topology is captured before any change and an emergency
/// restore button puts everything back.
/// </summary>
public sealed partial class DisplayViewModel : ObservableObject
{
    public static readonly IReadOnlyList<DisplayMode> Presets =
    [
        new(1920, 1080, 144), new(1920, 1080, 165), new(1920, 1080, 240),
        new(2560, 1440, 144), new(2560, 1440, 165), new(2560, 1440, 240),
    ];

    private readonly IVirtualDisplayProvider _provider;
    private readonly IDisplayService _displayService;
    private readonly IDriverInstaller _driverInstaller;
    private readonly ILogger<DisplayViewModel> _logger;
    private string? _safetyTopology;

    public DisplayViewModel(
        IVirtualDisplayProvider provider,
        IDisplayService displayService,
        IDriverInstaller driverInstaller,
        ILogger<DisplayViewModel> logger)
    {
        _provider = provider;
        _displayService = displayService;
        _driverInstaller = driverInstaller;
        _logger = logger;
    }

    public ObservableCollection<DisplayInfo> Displays { get; } = [];
    public ObservableCollection<DisplayMode> SupportedModes { get; } = [];
    public IReadOnlyList<DisplayMode> ModePresets => Presets;

    [ObservableProperty] private string _providerName = "---";
    [ObservableProperty] private string _providerCapabilities = string.Empty;
    [ObservableProperty] private bool _virtualDisplayActive;
    [ObservableProperty] private string _virtualDisplayModeText = "---";
    [ObservableProperty] private DisplayMode? _selectedPreset;
    [ObservableProperty] private int _customWidth = 1920;
    [ObservableProperty] private int _customHeight = 1080;
    [ObservableProperty] private int _customRefreshRate = 240;
    [ObservableProperty] private string _statusMessage = string.Empty;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private bool _canEmergencyRestore;

    // Driver install state. Drives the actionable banner shown when no device exists.
    [ObservableProperty] private bool _driverMissing;
    [ObservableProperty] private bool _canInstallDriver;
    [ObservableProperty] private string _driverBannerText = string.Empty;
    [ObservableProperty] private string _driverPackageText = string.Empty;
    [ObservableProperty] private bool _restartRequired;

    public async Task InitializeAsync(CancellationToken ct = default)
    {
        await RefreshAsync(ct);
    }

    [RelayCommand]
    private async Task RefreshAsync(CancellationToken ct = default)
    {
        try
        {
            Displays.Clear();
            foreach (var display in await _displayService.GetDisplaysAsync(ct))
            {
                Displays.Add(display);
            }

            // Capabilities first: provider selection is lazy, and reading Name before it
            // resolves would report "(not selected yet)".
            var capabilities = await _provider.GetCapabilitiesAsync(ct);
            ProviderName = _provider.Name;
            ProviderCapabilities =
                $"Custom modes: {YesNo(capabilities.SupportsCustomModes)} · GPU pinning: {YesNo(capabilities.SupportsGpuPinning)} · " +
                $"Enable/disable: {YesNo(capabilities.SupportsEnableDisable)} · Needs admin: {YesNo(capabilities.RequiresElevation)}";

            VirtualDisplayActive = await _provider.IsDisplayActiveAsync(ct);
            var mode = await _provider.GetCurrentModeAsync(ct);
            VirtualDisplayModeText = mode?.ToString() ?? "Off";

            SupportedModes.Clear();
            foreach (var supported in await _provider.GetSupportedModesAsync(ct))
            {
                SupportedModes.Add(supported);
            }

            await RefreshDriverStateAsync(ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Display refresh failed");
            StatusMessage = "Could not read display information. See Logs.";
        }
    }

    private async Task RefreshDriverStateAsync(CancellationToken ct)
    {
        var state = await _driverInstaller.GetStateAsync(ct);
        DriverMissing = state != DriverState.Installed;
        CanInstallDriver = state == DriverState.NotInstalledPackageAvailable;

        // The banner must not promise an install when there is nothing to install from,
        // so the headline changes with the state rather than only the fine print.
        DriverBannerText = state switch
        {
            DriverState.NotInstalledPackageAvailable
                => "Optima can install the driver for you. It needs one administrator approval, and adds a display device Windows can render to.",
            DriverState.NotInstalledNoPackage
                => $"No driver package ships with this build, so there is nothing to install yet. Put one in the '{VddDriverInstaller.BundledDriverFolder}' folder beside Optima.exe and this becomes a one-click install. Everything else works without it; virtual display features fall back to the mock provider.",
            _ => string.Empty,
        };

        DriverPackageText = state == DriverState.NotInstalledPackageAvailable
            && _driverInstaller.FindBundledPackage() is { } package
                ? $"bundled package: {package.DisplayName}"
                    + (package.HasCatalog ? string.Empty : "  (unsigned, Windows will refuse it)")
                : string.Empty;
    }

    /// <summary>
    /// Opens the folder a driver package belongs in, creating it if needed. Turns the
    /// "nothing bundled" state into something actionable instead of a dead end.
    /// </summary>
    [RelayCommand]
    private void OpenDriversFolder()
    {
        var folder = Path.Combine(AppContext.BaseDirectory, VddDriverInstaller.BundledDriverFolder);
        try
        {
            Directory.CreateDirectory(folder);
            using var process = System.Diagnostics.Process.Start(
                new System.Diagnostics.ProcessStartInfo(folder) { UseShellExecute = true });
            StatusMessage = string.Empty;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            _logger.LogWarning(ex, "Could not open the drivers folder at {Folder}", folder);
            StatusMessage = $"Could not open {folder}.";
        }
    }

    /// <summary>Installs the bundled driver so the user never has to touch Device Manager.</summary>
    [RelayCommand]
    private Task InstallDriverAsync() => GuardedAsync(async ct =>
    {
        var result = await _driverInstaller.InstallAsync(ct);
        if (result.Success)
        {
            RestartRequired = result.RestartRequired;
            StatusMessage = result.RestartRequired
                ? "Driver installed. Restart Windows to finish setting up the virtual display."
                : "Virtual display driver installed.";
        }
        else if (result.Error is { } error)
        {
            StatusMessage = $"{error.Title} {error.SuggestedFixes.FirstOrDefault()}";
        }
    });

    [RelayCommand]
    private Task UninstallDriverAsync() => GuardedAsync(async ct =>
    {
        var result = await _driverInstaller.UninstallAsync(ct);
        StatusMessage = result.Success
            ? "Virtual display driver removed."
            : $"{result.Error?.Title} {result.Error?.SuggestedFixes.FirstOrDefault()}";
    });

    [RelayCommand]
    private Task EnableVirtualDisplayAsync() => GuardedAsync(async ct =>
    {
        await CaptureSafetyTopologyAsync(ct);
        await _provider.InitializeAsync(ct);
        await _provider.EnableDisplayAsync(ct);
        StatusMessage = "Virtual display enabled.";
    });

    [RelayCommand]
    private Task DisableVirtualDisplayAsync() => GuardedAsync(async ct =>
    {
        await CaptureSafetyTopologyAsync(ct);
        await _provider.DisableDisplayAsync(ct);
        StatusMessage = "Virtual display disabled.";
    });

    [RelayCommand]
    private Task ApplyPresetAsync() => GuardedAsync(async ct =>
    {
        if (SelectedPreset is not { } preset)
        {
            StatusMessage = "Pick a preset first.";
            return;
        }
        await ApplyModeCoreAsync(preset, ct);
    });

    [RelayCommand]
    private Task ApplyCustomModeAsync() => GuardedAsync(async ct =>
    {
        var mode = new DisplayMode(CustomWidth, CustomHeight, CustomRefreshRate);
        if (!mode.IsValid)
        {
            StatusMessage = $"{mode} is not a sensible display mode.";
            return;
        }
        await ApplyModeCoreAsync(mode, ct);
    });

    [RelayCommand]
    private Task EmergencyRestoreAsync() => GuardedAsync(async ct =>
    {
        if (_safetyTopology is null)
        {
            StatusMessage = "Nothing to restore. No display change was made this session.";
            return;
        }
        await _displayService.RestoreTopologyAsync(_safetyTopology, ct);
        await _provider.RestoreOriginalStateAsync(ct);
        StatusMessage = "Previous display configuration restored.";
        CanEmergencyRestore = false;
        _safetyTopology = null;
    });

    private async Task ApplyModeCoreAsync(DisplayMode mode, CancellationToken ct)
    {
        await CaptureSafetyTopologyAsync(ct);
        await _provider.InitializeAsync(ct);
        if (!await _provider.IsDisplayActiveAsync(ct))
        {
            await _provider.EnableDisplayAsync(ct);
        }
        await _provider.SetModeAsync(mode, ct);
        StatusMessage = $"Applied {mode} to the virtual display.";
    }

    private async Task CaptureSafetyTopologyAsync(CancellationToken ct)
    {
        _safetyTopology ??= await _displayService.CaptureTopologyAsync(ct);
        CanEmergencyRestore = true;
    }

    private async Task GuardedAsync(Func<CancellationToken, Task> action)
    {
        if (IsBusy)
        {
            return;
        }
        IsBusy = true;
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            await action(cts.Token);
        }
        catch (OptimaException ex)
        {
            StatusMessage = $"{ex.Error.Title} {ex.Error.SuggestedFixes.FirstOrDefault()}";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Display operation failed");
            StatusMessage = "The display operation failed. See Logs for details.";
        }
        finally
        {
            IsBusy = false;
            await RefreshAsync();
        }
    }

    private static string YesNo(bool value) => value ? "yes" : "no";
}
