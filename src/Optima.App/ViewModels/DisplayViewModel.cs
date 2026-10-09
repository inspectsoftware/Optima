using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Optima.Core.Abstractions;
using Optima.Core.Configuration;
using Optima.Core.Launch;
using Optima.Core.Models;
using Optima.Core.Monitoring;
using Optima.Driver;
using Microsoft.Extensions.Logging;

namespace Optima.App.ViewModels;

/// <summary>A desktop size as the resolution picker shows it.</summary>
public sealed record DisplaySize(int Width, int Height)
{
    public override string ToString() => $"{Width} x {Height}";
}

/// <summary>One line of "Check my setup" as the page shows it.</summary>
public sealed class DisplayCheckRow
{
    public DisplayCheckRow(DisplayCheckStep step)
    {
        Step = step;
        // What the error guide says to do about it sits right under the line that failed: the
        // page is where the player is looking, and the guide is two clicks away. Without the
        // guide's own "go to the Display page and run the check": that is where this is shown.
        var advice = step is { State: DisplayCheckState.Failed, ErrorCode: { } code } && ErrorCatalog.Find(code) is { } entry
            ? string.Join(Environment.NewLine, entry.HowToFix
                .Where(fix => !fix.Contains("Check my setup", StringComparison.OrdinalIgnoreCase))
                .Select(fix => "· " + fix))
            : string.Empty;
        Detail = string.Join(Environment.NewLine, new[] { step.Detail, advice }.Where(text => text.Length > 0));
    }

    public DisplayCheckStep Step { get; }
    public string Title => Step.Title;
    public string Detail { get; }

    public string Mark => Step.State switch
    {
        DisplayCheckState.Passed => "OK",
        DisplayCheckState.Failed => "FAILED",
        DisplayCheckState.Running => "...",
        DisplayCheckState.Skipped => "SKIPPED",
        _ => "-",
    };

    public bool IsPassed => Step.State == DisplayCheckState.Passed;
    public bool IsFailed => Step.State == DisplayCheckState.Failed;

    public string FixLabel => Step.Fix switch
    {
        DisplayCheckFix.InstallDriver => "Install driver",
        DisplayCheckFix.ReloadDriver => "Reload driver",
        _ => string.Empty,
    };

    public bool HasFix => FixLabel.Length > 0;
}

/// <summary>
/// DISPLAY page. Three steps in the order a player meets them: the driver, the one choice of
/// whether Critical Ops runs on the virtual display and at what size, and a way to try it without
/// starting the game. Under them a check that runs the same steps a launch does and says, line by
/// line, which one fails and what to do about it. The list of monitors is at the bottom.
/// </summary>
public sealed partial class DisplayViewModel : ObservableObject
{
    private static readonly IReadOnlyList<DisplaySize> KnownSizes =
        [new(1280, 720), new(1600, 900), new(1920, 1080), new(2560, 1440), new(3840, 2160)];

    private static readonly IReadOnlyList<int> KnownRefreshRates = [60, 90, 120, 144, 165, 240];

    private readonly IVirtualDisplayProvider _provider;
    private readonly IDisplayService _displayService;
    private readonly IDriverInstaller _driverInstaller;
    private readonly SettingsService _settings;
    private readonly StatusViewModel _status;
    private readonly LaunchOrchestrator _orchestrator;
    private readonly GamePresenceService _presence;
    private readonly DisplaySetupCheck _check;
    private readonly ILogger<DisplayViewModel> _logger;
    private string? _safetyTopology;
    private IReadOnlyList<DisplayInfo> _allDisplays = [];
    private bool _suppressFilterHandlers;
    private bool _loadingChoice;

    public DisplayViewModel(
        IVirtualDisplayProvider provider,
        IDisplayService displayService,
        IDriverInstaller driverInstaller,
        SettingsService settings,
        StatusViewModel status,
        LaunchOrchestrator orchestrator,
        GamePresenceService presence,
        ILogger<DisplayViewModel> logger)
    {
        _provider = provider;
        _displayService = displayService;
        _driverInstaller = driverInstaller;
        _settings = settings;
        _status = status;
        _orchestrator = orchestrator;
        _presence = presence;
        _logger = logger;
        _check = new DisplaySetupCheck(
            driverInstaller, provider, displayService,
            helperPresent: () => File.Exists(Path.Combine(AppContext.BaseDirectory, "Optima.Watchdog.exe")),
            restartPending: RestartIsOwed);
        ShowSteps(DisplaySetupCheck.Blank);
    }

    private bool RestartIsOwed()
        => _settings.Current?.DriverRestartPending(DateTimeOffset.Now, TimeSpan.FromMilliseconds(Environment.TickCount64)) ?? false;

    // ------------------------------------------------------------------ shared state

    [ObservableProperty] private bool _isBusy;

    /// <summary>What the last action came to, in a sentence. Not an error colour: most of these are good news.</summary>
    [ObservableProperty] private string _statusMessage = string.Empty;

    /// <summary>Shown beside the provider line and the drivers-folder button, which are a developer's.</summary>
    [ObservableProperty] private bool _developerMode;

    // Read by the DISPLAYS widget on HOME as well.
    [ObservableProperty] private string _providerName = "---";
    [ObservableProperty] private bool _virtualDisplayActive;
    [ObservableProperty] private string _virtualDisplayModeText = "---";

    // ------------------------------------------------------------------ 1 · the driver

    [ObservableProperty] private bool _driverInstalled;
    [ObservableProperty] private bool _canInstallDriver;
    [ObservableProperty] private bool _restartRequired;

    /// <summary>INSTALLED, NOT INSTALLED or RESTART NEEDED.</summary>
    [ObservableProperty] private string _driverBadge = "---";

    [ObservableProperty] private string _driverDetail = string.Empty;

    /// <summary>Why the last install or removal did not go through, with what to do about it.</summary>
    [ObservableProperty] private string _driverProblem = string.Empty;

    // ------------------------------------------------------------------ 2 · the choice

    public ObservableCollection<DisplaySize> Sizes { get; } = [.. KnownSizes];
    public ObservableCollection<int> RefreshRates { get; } = [.. KnownRefreshRates];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ChoiceNote))]
    private bool _useVirtualDisplay;

    [ObservableProperty] private DisplaySize? _selectedSize;
    [ObservableProperty] private int _selectedRefreshRate = 240;

    /// <summary>Said under the switch: what the choice means for the next launch, given the driver.</summary>
    public string ChoiceNote => !UseVirtualDisplay
        ? "Off: Critical Ops runs on your own screen."
        : DriverInstalled
            ? "Turned on when the game starts and off again when it ends. Nothing about your screens is kept changed."
            : "The driver is not installed, so the game will run on your own screen until it is.";

    private DisplayMode ChosenMode
        => new(SelectedSize?.Width ?? 1920, SelectedSize?.Height ?? 1080, SelectedRefreshRate);

    partial void OnUseVirtualDisplayChanged(bool value) => SaveChoice();
    partial void OnSelectedSizeChanged(DisplaySize? value) => SaveChoice();
    partial void OnSelectedRefreshRateChanged(int value) => SaveChoice();
    partial void OnDriverInstalledChanged(bool value) => OnPropertyChanged(nameof(ChoiceNote));

    private async void SaveChoice()
    {
        // Showing the stored choice is not a change to store.
        if (_loadingChoice || SelectedSize is null)
        {
            return;
        }
        var mode = ChosenMode;
        var enabled = UseVirtualDisplay;
        try
        {
            await _settings.UpdateSettingsAsync(s => s with
            {
                VirtualDisplayEnabled = enabled,
                VirtualDisplayWidth = mode.Width,
                VirtualDisplayHeight = mode.Height,
                VirtualDisplayRefreshRate = mode.RefreshRate,
            });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            StatusMessage = "That choice could not be saved: " + ex.Message;
        }
    }

    private void LoadChoice(AppSettings settings)
    {
        _loadingChoice = true;
        try
        {
            var size = new DisplaySize(settings.VirtualDisplayWidth, settings.VirtualDisplayHeight);
            // A mode that came from an older profile and is not one of the usual ones is still the
            // player's, and stays selectable.
            if (!Sizes.Contains(size))
            {
                Sizes.Add(size);
            }
            if (!RefreshRates.Contains(settings.VirtualDisplayRefreshRate))
            {
                RefreshRates.Add(settings.VirtualDisplayRefreshRate);
            }
            UseVirtualDisplay = settings.VirtualDisplayEnabled == true;
            SelectedSize = size;
            SelectedRefreshRate = settings.VirtualDisplayRefreshRate;
            DeveloperMode = settings.DeveloperMode;
        }
        finally
        {
            _loadingChoice = false;
        }
    }

    // ------------------------------------------------------------------ 3 · try it, and the check

    public ObservableCollection<DisplayCheckRow> CheckRows { get; } = [];

    /// <summary>The countdown while a test holds the display on.</summary>
    [ObservableProperty] private string _testStatus = string.Empty;

    [ObservableProperty] private bool _canEmergencyRestore;

    private void ShowSteps(IReadOnlyList<DisplayCheckStep> steps)
    {
        CheckRows.Clear();
        foreach (var step in steps)
        {
            CheckRows.Add(new DisplayCheckRow(step));
        }
    }

    /// <summary>Turns the display on at the chosen size, keeps it for fifteen seconds to look at, and puts everything back.</summary>
    [RelayCommand]
    private Task TestAsync() => RunStepsAsync(TimeSpan.FromSeconds(15));

    /// <summary>The same steps without the wait: for the verdicts, not for looking.</summary>
    [RelayCommand]
    private Task RunCheckAsync() => RunStepsAsync(TimeSpan.FromSeconds(2));

    private async Task RunStepsAsync(TimeSpan hold)
    {
        if (IsBusy)
        {
            return;
        }
        // It moves screens around. Under a running game that is the game's window going with them.
        if (_orchestrator.IsSessionActive || _presence.Current != GamePresence.NotRunning)
        {
            StatusMessage = "Not while Critical Ops is running: this switches your screens around. Close the game first.";
            return;
        }

        IsBusy = true;
        StatusMessage = string.Empty;
        try
        {
            string? before = null;
            try
            {
                before = await _displayService.CaptureTopologyAsync();
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "The layout could not be captured before the display check");
            }

            var steps = await _check.RunAsync(
                ChosenMode, hold, ShowSteps,
                left => TestStatus = left > 0
                    ? $"The virtual display is on. {left} second{(left == 1 ? string.Empty : "s")} left, then everything is put back."
                    : string.Empty);
            ShowSteps(steps);

            var putBack = steps.First(step => step.Id == DisplaySetupCheck.PutBack);
            if (putBack.State == DisplayCheckState.Failed && before is not null)
            {
                // The one case the button exists for: the screens were changed and did not go back.
                _safetyTopology = before;
                CanEmergencyRestore = true;
            }

            if (steps.All(step => step.State is DisplayCheckState.Passed or DisplayCheckState.Skipped))
            {
                StatusMessage = "Everything works. The virtual display turned on at "
                    + steps.First(step => step.Id == DisplaySetupCheck.Mode).Detail + " and was put back.";
                // A driver that works needs no restart any more, whatever Windows said at install.
                await _settings.UpdateSettingsAsync(s => s with { DriverRestartAskedAt = null });
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "The display check could not be run");
            StatusMessage = "The check could not be run. See the log on the Debug page.";
        }
        finally
        {
            TestStatus = string.Empty;
            IsBusy = false;
            await RefreshAsync();
            // The HOME "Display" readout follows the virtual display.
            await _status.RefreshAsync();
        }
    }

    /// <summary>The button on a failed line of the check.</summary>
    [RelayCommand]
    private async Task FixAsync(DisplayCheckRow? row)
    {
        switch (row?.Step.Fix)
        {
            case DisplayCheckFix.InstallDriver:
                await InstallDriverAsync();
                break;
            case DisplayCheckFix.ReloadDriver:
                await ReloadDriverAsync();
                break;
        }
    }

    /// <summary>
    /// Asks the driver to read its settings again and attach its display: what cures a display
    /// that is enabled and does not appear. Then the check runs again, to see whether it did.
    /// </summary>
    private async Task ReloadDriverAsync()
    {
        if (IsBusy || _provider is not IVirtualDisplayMaintenance maintenance)
        {
            return;
        }
        var reloaded = false;
        await GuardedAsync(async ct =>
        {
            await maintenance.ReloadDriverAsync(ct);
            reloaded = true;
        });
        if (reloaded)
        {
            await RunStepsAsync(TimeSpan.FromSeconds(2));
        }
    }

    [RelayCommand]
    private Task EmergencyRestoreAsync() => GuardedAsync(async ct =>
    {
        if (_safetyTopology is null)
        {
            return;
        }
        await _displayService.RestoreTopologyAsync(_safetyTopology, ct);
        await _provider.RestoreOriginalStateAsync(ct);
        StatusMessage = "Your screens are back the way they were before the check.";
        CanEmergencyRestore = false;
        _safetyTopology = null;
    });

    // ------------------------------------------------------------------ driver commands

    [RelayCommand]
    private Task InstallDriverAsync() => GuardedAsync(async ct =>
    {
        DriverProblem = string.Empty;
        var result = await _driverInstaller.InstallAsync(ct);
        if (result.Success)
        {
            // Kept in the settings: the page that says "restart needed" must still say it after
            // Optima has been closed and opened again, which is what most people do first.
            await _settings.UpdateSettingsAsync(
                s => s with { DriverRestartAskedAt = result.RestartRequired ? DateTimeOffset.Now : null }, ct);
            StatusMessage = result.RestartRequired
                ? "Driver installed. Restart Windows to finish, then come back to this page."
                : "Driver installed. Press \"Test for 15 seconds\" below to see the virtual display.";
        }
        else if (result.Error is { } error)
        {
            DriverProblem = Describe(error);
        }
    });

    [RelayCommand]
    private Task UninstallDriverAsync() => GuardedAsync(async ct =>
    {
        var confirm = Views.GlassDialog.Confirm(
            System.Windows.Application.Current.MainWindow,
            "Remove the virtual display driver?",
            "The virtual display stops working until the driver is installed again, and Critical Ops " +
            "runs on your own screen. Windows asks for administrator approval once.",
            "Cancel", "Remove driver", Views.DialogTone.Danger);
        if (!confirm)
        {
            return;
        }

        DriverProblem = string.Empty;
        var result = await _driverInstaller.UninstallAsync(ct);
        if (result.Success)
        {
            await _settings.UpdateSettingsAsync(s => s with { DriverRestartAskedAt = null }, ct);
            StatusMessage = "Driver removed. Install it again from this page whenever you want it back.";
        }
        else if (result.Error is { } error)
        {
            DriverProblem = Describe(error);
        }
    });

    /// <summary>For a build made without a driver package: where one would go. A developer's button.</summary>
    [RelayCommand]
    private void OpenDriversFolder()
    {
        var folder = Path.Combine(AppContext.BaseDirectory, VddDriverInstaller.BundledDriverFolder);
        try
        {
            // Opened, not created: the install folder is Program Files and not Optima's to write to.
            using var process = System.Diagnostics.Process.Start(
                new System.Diagnostics.ProcessStartInfo(folder) { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            _logger.LogWarning(ex, "Could not open the drivers folder at {Folder}", folder);
            StatusMessage = $"Could not open {folder}.";
        }
    }

    /// <summary>An error as the page says it: what happened, why, and every way out the error names.</summary>
    private static string Describe(UserFriendlyError error)
        => string.Join(Environment.NewLine, new[] { error.Title, error.Explanation }
            .Where(text => !string.IsNullOrWhiteSpace(text))
            .Concat(error.SuggestedFixes.Select(fix => "· " + fix)));

    // ------------------------------------------------------------------ loading

    public async Task InitializeAsync(CancellationToken ct = default)
    {
        var settings = await _settings.GetSettingsAsync(ct);
        _suppressFilterHandlers = true;
        HideInactive = settings.HideInactiveDisplays;
        _suppressFilterHandlers = false;
        await RefreshAsync(ct);
    }

    [RelayCommand]
    private async Task RefreshAsync(CancellationToken ct = default)
    {
        try
        {
            LoadChoice(await _settings.GetSettingsAsync(ct));

            _allDisplays = await _displayService.GetDisplaysAsync(ct);
            await RebuildRowsAsync(ct);

            // Before the provider is asked anything: it throws on a PC without the driver, and
            // the driver line must be right on exactly that PC.
            await RefreshDriverStateAsync(ct);

            ProviderName = _provider.Name;
            VirtualDisplayActive = await _provider.IsDisplayActiveAsync(ct);
            var mode = await _provider.GetCurrentModeAsync(ct);
            VirtualDisplayModeText = mode?.ToString() ?? "Off";
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Display refresh failed");
            StatusMessage = "Could not read display information. See the log on the Debug page.";
        }
    }

    private async Task RefreshDriverStateAsync(CancellationToken ct)
    {
        var state = await _driverInstaller.GetStateAsync(ct);
        DriverInstalled = state == DriverState.Installed;
        CanInstallDriver = state == DriverState.NotInstalledPackageAvailable;
        RestartRequired = DriverInstalled && RestartIsOwed();

        DriverBadge = !DriverInstalled ? "NOT INSTALLED" : RestartRequired ? "RESTART NEEDED" : "INSTALLED";
        DriverDetail = state switch
        {
            DriverState.Installed when RestartRequired
                => "Windows asked for a restart to finish installing the driver. The virtual display will not appear until you restart.",
            DriverState.Installed
                => "The virtual display driver is on this PC. It stays switched off until a game session, or a test below, turns it on.",
            DriverState.NotInstalledPackageAvailable
                => "Optima installs it for you. Windows asks for administrator approval once, and nothing appears on your screens until you use it.",
            _ => "This build of Optima has no driver package, so there is nothing to install. Everything else works without it, and Critical Ops runs on your own screen.",
        };
    }

    private async Task GuardedAsync(Func<CancellationToken, Task> action)
    {
        if (IsBusy)
        {
            return;
        }
        IsBusy = true;
        StatusMessage = string.Empty;
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            await action(cts.Token);
        }
        catch (OptimaException ex)
        {
            StatusMessage = Describe(ex.Error);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Display operation failed");
            StatusMessage = "That did not work. See the log on the Debug page for details.";
        }
        finally
        {
            IsBusy = false;
            await RefreshAsync();
            await _status.RefreshAsync();
        }
    }

    // ------------------------------------------------------------------ monitors

    public ObservableCollection<DisplayRowViewModel> DisplayRows { get; } = [];

    /// <summary>"MONITORS (3)": the folded list says how many it holds.</summary>
    [ObservableProperty] private string _monitorsHeader = "MONITORS";

    [ObservableProperty] private bool _hideInactive;
    [ObservableProperty] private bool _showHidden;

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
        MonitorsHeader = $"MONITORS ({DisplayRows.Count})";
    }

    [RelayCommand]
    private void StartRename(DisplayRowViewModel row)
    {
        row.EditName = row.CustomName ?? string.Empty;
        row.IsEditing = true;
    }

    [RelayCommand]
    private void CancelRename(DisplayRowViewModel row) => row.IsEditing = false;

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
        await _status.RefreshAsync();
    }
}

/// <summary>One row of MONITORS: the OS facts plus the user's cosmetic overrides.</summary>
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
