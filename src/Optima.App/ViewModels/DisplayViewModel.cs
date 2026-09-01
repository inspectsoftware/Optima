using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Optima.Core.Abstractions;
using Optima.Core.Configuration;
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
    private readonly SettingsService _settings;
    private readonly StatusViewModel _status;
    private readonly ILogger<DisplayViewModel> _logger;
    private string? _safetyTopology;
    private IReadOnlyList<DisplayInfo> _allDisplays = [];
    private bool _suppressFilterHandlers;

    public DisplayViewModel(
        IVirtualDisplayProvider provider,
        IDisplayService displayService,
        IDriverInstaller driverInstaller,
        SettingsService settings,
        StatusViewModel status,
        ILogger<DisplayViewModel> logger)
    {
        _provider = provider;
        _displayService = displayService;
        _driverInstaller = driverInstaller;
        _settings = settings;
        _status = status;
        _logger = logger;
    }

    public ObservableCollection<DisplayRowViewModel> DisplayRows { get; } = [];
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

    /// <summary>Filters for the attached-displays list. HideInactive is persisted; ShowHidden is not.</summary>
    [ObservableProperty] private bool _hideInactive;
    [ObservableProperty] private bool _showHidden;

    public async Task InitializeAsync(CancellationToken ct = default)
    {
        var settings = await _settings.GetSettingsAsync(ct);
        _suppressFilterHandlers = true;
        HideInactive = settings.HideInactiveDisplays;
        _suppressFilterHandlers = false;
        await RefreshAsync(ct);
    }

    partial void OnHideInactiveChanged(bool value)
    {
        if (_suppressFilterHandlers)
        {
            return;
        }
        _ = _settings.UpdateSettingsAsync(s => s with { HideInactiveDisplays = value });
        _ = RebuildRowsAsync();
    }

    partial void OnShowHiddenChanged(bool value)
    {
        if (!_suppressFilterHandlers)
        {
            _ = RebuildRowsAsync();
        }
    }

    [RelayCommand]
    private async Task RefreshAsync(CancellationToken ct = default)
    {
        try
        {
            _allDisplays = await _displayService.GetDisplaysAsync(ct);
            await RebuildRowsAsync(ct);

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

    /// <summary>Re-applies the user's cosmetic overrides (name / order / hidden) to the cached OS list.</summary>
    private async Task RebuildRowsAsync(CancellationToken ct = default)
    {
        var overrides = (await _settings.GetSettingsAsync(ct)).DisplayOverrides;
        DisplayRows.Clear();
        foreach (var display in DisplayPresentation.Arrange(_allDisplays, overrides, HideInactive, ShowHidden))
        {
            DisplayRows.Add(new DisplayRowViewModel(
                display,
                DisplayPresentation.CustomName(display, overrides),
                overrides.GetValueOrDefault(DisplayPresentation.OverrideKey(display))?.Hidden ?? false));
        }
    }

    [RelayCommand]
    private void StartRename(DisplayRowViewModel row)
    {
        row.EditName = row.CustomName ?? string.Empty;
        row.IsEditing = true;
    }

    [RelayCommand]
    private void CancelRename(DisplayRowViewModel row) => row.IsEditing = false;

    /// <summary>Saving an empty name clears the custom name back to the OS-reported one.</summary>
    [RelayCommand]
    private async Task SaveNameAsync(DisplayRowViewModel row)
    {
        var name = row.EditName.Trim();
        row.IsEditing = false;
        await UpdateOverrideAsync(row.Info, o => o with { CustomName = name.Length == 0 ? null : name });
    }

    [RelayCommand]
    private Task ToggleHiddenAsync(DisplayRowViewModel row)
        => UpdateOverrideAsync(row.Info, o => o with { Hidden = !o.Hidden });

    [RelayCommand]
    private Task MoveUpAsync(DisplayRowViewModel row) => MoveAsync(row, -1);

    [RelayCommand]
    private Task MoveDownAsync(DisplayRowViewModel row) => MoveAsync(row, +1);

    /// <summary>
    /// Reorders within the visible list, then persists the whole visible order as explicit
    /// sort indexes so the arrangement survives refreshes and restarts.
    /// </summary>
    private async Task MoveAsync(DisplayRowViewModel row, int delta)
    {
        var index = DisplayRows.IndexOf(row);
        var target = index + delta;
        if (index < 0 || target < 0 || target >= DisplayRows.Count)
        {
            return;
        }

        var order = DisplayRows.Select(r => r.Info).ToList();
        (order[index], order[target]) = (order[target], order[index]);

        await _settings.UpdateSettingsAsync(s =>
        {
            var map = new Dictionary<string, DisplayOverride>(s.DisplayOverrides);
            for (var i = 0; i < order.Count; i++)
            {
                var key = DisplayPresentation.OverrideKey(order[i]);
                map[key] = (map.GetValueOrDefault(key) ?? new DisplayOverride()) with { SortIndex = i };
            }
            return s with { DisplayOverrides = map };
        });
        await RebuildRowsAsync();
    }

    private async Task UpdateOverrideAsync(DisplayInfo display, Func<DisplayOverride, DisplayOverride> mutate)
    {
        var key = DisplayPresentation.OverrideKey(display);
        await _settings.UpdateSettingsAsync(s =>
        {
            var map = new Dictionary<string, DisplayOverride>(s.DisplayOverrides);
            var updated = mutate(map.GetValueOrDefault(key) ?? new DisplayOverride());
            if (updated.IsEmpty)
            {
                map.Remove(key);
            }
            else
            {
                map[key] = updated;
            }
            return s with { DisplayOverrides = map };
        });
        await RebuildRowsAsync();
        // Custom names surface in the HOME status readout too.
        await _status.RefreshAsync();
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
        var confirm = Views.GlassDialog.Confirm(
            System.Windows.Application.Current.MainWindow,
            "Remove the virtual display driver?",
            "The virtual display disappears immediately, and the Optima Virtualization " +
            "features stop working until the driver is installed again. One administrator prompt follows.",
            "Cancel", "Remove driver", Views.DialogTone.Danger);
        if (!confirm)
        {
            return;
        }

        var result = await _driverInstaller.UninstallAsync(ct);
        StatusMessage = result.Success
            ? "Virtual display driver removed. Reinstall it any time from this page."
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
            // The HOME "Display" readout follows the virtual display, so it must flip the
            // moment an enable/disable/mode change lands rather than on the next 10 s tick.
            await _status.RefreshAsync();
        }
    }

    private static string YesNo(bool value) => value ? "yes" : "no";
}

/// <summary>One row of ATTACHED DISPLAYS: the OS facts plus the user's cosmetic overrides.</summary>
public sealed partial class DisplayRowViewModel : ObservableObject
{
    public DisplayRowViewModel(DisplayInfo info, string? customName, bool isHidden)
    {
        Info = info;
        CustomName = customName;
        IsHidden = isHidden;
    }

    public DisplayInfo Info { get; }
    public string? CustomName { get; }
    public bool IsHidden { get; }

    public string DeviceName => Info.DeviceName;
    public string OriginalName => Info.AdapterName.Length > 0 ? Info.AdapterName : Info.FriendlyName;
    public string DisplayedName => CustomName ?? OriginalName;
    public string CurrentMode => Info.CurrentMode.ToString();
    public bool IsPrimary => Info.IsPrimary;
    public bool IsActive => Info.IsActive;

    [ObservableProperty] private bool _isEditing;
    [ObservableProperty] private string _editName = string.Empty;
}
