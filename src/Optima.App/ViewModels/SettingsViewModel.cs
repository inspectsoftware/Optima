using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Optima.Core.Configuration;
using Optima.Core.Ipc;
using Optima.Core.Models;
using Optima.Core.Theming;

namespace Optima.App.ViewModels;

/// <summary>One selectable accent preset on the SETTINGS page.</summary>
public sealed record AccentPreset(string Name, string Hex);

/// <summary>SETTINGS page (§21/§28/§29): appearance, app options, detection overrides.</summary>
public sealed partial class SettingsViewModel : ObservableObject
{
    private readonly SettingsService _settings;
    private readonly Services.PlayerSwitcherService _players;
    private readonly Optima.Core.Abstractions.IElevationBroker _broker;
    private readonly Optima.Platform.Windows.Services.DevEmulatorSettingsService? _devEmulator;

    // What is actually on disk right now, so Apply only touches what changed.
    private Optima.Platform.Windows.Services.GpuPreferenceKind _devGpuPreferenceOnDisk;
    private bool _devGpuPrioritizationOnDisk;
    private bool _devGpuRetrievalOnDisk;

    public SettingsViewModel(
        SettingsService settings,
        Services.PlayerSwitcherService players,
        Optima.Core.Abstractions.IElevationBroker broker,
        Optima.Platform.Windows.Services.DevEmulatorSettingsService? devEmulator = null)
    {
        _settings = settings;
        _players = players;
        _broker = broker;
        _devEmulator = devEmulator;
    }

    public IReadOnlyList<string> ProviderOptions { get; } = ["Auto", "MttVdd", "Mock"];
    public IReadOnlyList<string> LogLevelOptions { get; } = ["Trace", "Debug", "Information", "Warning", "Error"];
    public IReadOnlyList<string> CornerOptions { get; } = ["TopLeft", "TopRight", "BottomLeft", "BottomRight"];
    public IReadOnlyList<double> OpacityOptions { get; } = [0.5, 0.65, 0.8, 1.0];
    public IReadOnlyList<string> ThemeOptions { get; } = ["Dark", "Light"];

    public IReadOnlyList<AccentPreset> AccentPresets { get; } =
    [
        new("Gold", "#E8B45A"),
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
    [ObservableProperty] private string _playerAccountId = string.Empty;
    [ObservableProperty] private string _saveAccountLabel = string.Empty;
    [ObservableProperty] private string _newTrackedIgn = string.Empty;
    [ObservableProperty] private string _newTrackedAccountId = string.Empty;
    public ObservableCollection<PlayerAccount> TrackedPlayers { get; } = [];

    [RelayCommand]
    private async Task SaveAsAccountAsync()
    {
        try
        {
            await _players.SaveCurrentAsAccountAsync(SaveAccountLabel);
            SaveAccountLabel = string.Empty;
            SaveBarMark = "[ OK ]";
            SaveBarText = "Account saved. It is now in the switcher at the top of the window.";
            SaveBarVisible = true;
        }
        catch (InvalidOperationException ex)
        {
            SaveBarMark = "[ ! ]";
            SaveBarText = ex.Message;
            SaveBarVisible = true;
        }
    }

    [RelayCommand]
    private async Task TrackPlayerAsync()
    {
        var ign = NewTrackedIgn.Trim();
        if (ign.Length == 0)
        {
            SaveBarMark = "[ ! ]";
            SaveBarText = "Give the player's in-game name first.";
            SaveBarVisible = true;
            return;
        }
        long? accountId = long.TryParse(NewTrackedAccountId.Trim(), out var id) && id > 0 ? id : null;
        await _players.AddTrackedAsync(new PlayerAccount { Ign = ign, AccountId = accountId });
        TrackedPlayers.Add(new PlayerAccount { Ign = ign, AccountId = accountId });
        NewTrackedIgn = string.Empty;
        NewTrackedAccountId = string.Empty;
        SaveBarMark = "[ OK ]";
        SaveBarText = $"Tracking {ign}. They appear on HOME beside your own stats.";
        SaveBarVisible = true;
    }

    [RelayCommand]
    private async Task RemoveTrackedAsync(PlayerAccount account)
    {
        await _players.RemoveTrackedAsync(account.Key);
        TrackedPlayers.Remove(account);
    }
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
    [ObservableProperty] private bool _autoRelaunchOnCrash;
    [ObservableProperty] private bool _useMockMetricsProvider;
    [ObservableProperty] private bool _useDeveloperEmulator;
    [ObservableProperty] private string _vddSettingsPath = string.Empty;
    [ObservableProperty] private string _manualInstallPath = string.Empty;
    [ObservableProperty] private string _customLaunchCommand = string.Empty;

    [ObservableProperty] private bool _hasUnsavedChanges;
    [ObservableProperty] private bool _saveBarVisible;
    [ObservableProperty] private string _saveBarText = string.Empty;
    [ObservableProperty] private string _saveBarMark = "[ ! ]";

    private DispatcherTimer? _confirmTimer;

    // Values as of the last load or save; every property change is compared against this so the
    // pinned save bar appears the moment anything differs (multiple users missed the old button).
    private IReadOnlyDictionary<string, object?> _savedValues = new Dictionary<string, object?>();

    protected override void OnPropertyChanged(PropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (e.PropertyName is not nameof(HasUnsavedChanges))
        {
            RefreshUnsavedState();
        }

        // The player identity is persisted as it is typed (debounced): it must survive an
        // app close or a crash without hunting for the Save button first.
        if (e.PropertyName is nameof(PlayerIgn) or nameof(PlayerAccountId))
        {
            RestartIdentityAutosave();
        }
    }

    private DispatcherTimer? _identityAutosaveTimer;

    private void RestartIdentityAutosave()
    {
        if (_savedValues.Count == 0)
        {
            // Not initialized yet (settings still loading); do not persist half-loaded text.
            return;
        }
        _identityAutosaveTimer ??= new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(800) };
        _identityAutosaveTimer.Stop();
        _identityAutosaveTimer.Tick += OnIdentityAutosaveTick;
        _identityAutosaveTimer.Start();
    }

    private async void OnIdentityAutosaveTick(object? sender, EventArgs e)
    {
        var timer = (DispatcherTimer)sender!;
        timer.Stop();
        try
        {
            long? accountId = long.TryParse(PlayerAccountId.Trim(), out var id) && id > 0 ? id : null;
            await _settings.UpdateSettingsAsync(s => s with
            {
                PlayerIgn = PlayerIgn.Trim(),
                PlayerAccountId = accountId,
            });
        }
        catch
        {
            // Autosave is best-effort; the Save button remains the authoritative path.
        }
    }

    private void CaptureSavedValues() => _savedValues = NormalizedValues();

    private Dictionary<string, object?> NormalizedValues() => new()
    {
        [nameof(Theme)] = Theme,
        [nameof(AccentColor)] = AccentColor.Trim(),
        [nameof(PlayerIgn)] = PlayerIgn.Trim(),
        [nameof(PlayerAccountId)] = PlayerAccountId.Trim(),
        [nameof(DiscordPresenceEnabled)] = DiscordPresenceEnabled,
        [nameof(DiscordPresenceInLauncher)] = DiscordPresenceInLauncher,
        [nameof(DiscordApplicationId)] = DiscordApplicationId.Trim(),
        [nameof(Provider)] = Provider,
        [nameof(EnableFrametimeCapture)] = EnableFrametimeCapture,
        [nameof(LogLevel)] = LogLevel,
        [nameof(DeveloperMode)] = DeveloperMode,
        [nameof(KeepInTrayOnClose)] = KeepInTrayOnClose,
        [nameof(FollowWindowsMotion)] = FollowWindowsMotion,
        [nameof(StartWithWindows)] = StartWithWindows,
        [nameof(OverlayEnabled)] = OverlayEnabled,
        [nameof(OverlayCorner)] = OverlayCorner,
        [nameof(OverlayOpacity)] = OverlayOpacity,
        [nameof(OverlayShowNetwork)] = OverlayShowNetwork,
        [nameof(NetworkReferenceHost)] = string.IsNullOrWhiteSpace(NetworkReferenceHost) ? "1.1.1.1" : NetworkReferenceHost.Trim(),
        [nameof(EnableWatchMode)] = EnableWatchMode,
        [nameof(UseMockMetricsProvider)] = UseMockMetricsProvider,
        [nameof(UseDeveloperEmulator)] = UseDeveloperEmulator,
        [nameof(VddSettingsPath)] = VddSettingsPath.Trim(),
        [nameof(ManualInstallPath)] = ManualInstallPath.Trim(),
        [nameof(CustomLaunchCommand)] = CustomLaunchCommand.Trim(),
    };

    private void RefreshUnsavedState()
    {
        var current = NormalizedValues();
        HasUnsavedChanges = _savedValues.Count > 0
            && (current.Count != _savedValues.Count
                || current.Any(kv => !Equals(kv.Value, _savedValues.GetValueOrDefault(kv.Key))));
        if (!HasUnsavedChanges)
        {
            return;
        }

        // Any further edit replaces the confirmation with the warning again.
        StopConfirmTimer();
        SaveBarMark = "[ ! ]";
        SaveBarText = "Unsaved changes. Settings apply when saved.";
        SaveBarVisible = true;
    }

    private void ShowSaveConfirmation(string message)
    {
        SaveBarMark = "[ OK ]";
        SaveBarText = message;
        SaveBarVisible = true;
        StopConfirmTimer();
        _confirmTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(4) };
        _confirmTimer.Tick += (_, _) =>
        {
            StopConfirmTimer();
            if (!HasUnsavedChanges)
            {
                SaveBarVisible = false;
            }
        };
        _confirmTimer.Start();
    }

    private void StopConfirmTimer()
    {
        _confirmTimer?.Stop();
        _confirmTimer = null;
    }

    public async Task InitializeAsync(CancellationToken ct = default)
    {
        var settings = await _settings.GetSettingsAsync(ct);
        Theme = settings.Theme;
        AccentColor = settings.AccentColor;
        PlayerIgn = settings.PlayerIgn;
        PlayerAccountId = settings.PlayerAccountId?.ToString(CultureInfo.InvariantCulture) ?? string.Empty;
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
        AutoRelaunchOnCrash = settings.AutoRelaunchOnCrash;
        UseMockMetricsProvider = settings.UseMockMetricsProvider;
        UseDeveloperEmulator = settings.UseDeveloperEmulator;
        VddSettingsPath = settings.VddSettingsPath ?? string.Empty;

        var rules = await _settings.GetDetectionRulesAsync(ct);
        ManualInstallPath = rules.ManualInstallPath ?? string.Empty;
        CustomLaunchCommand = rules.CustomLaunchCommand ?? string.Empty;

        TrackedPlayers.Clear();
        foreach (var tracked in settings.TrackedPlayers)
        {
            TrackedPlayers.Add(tracked);
        }

        CaptureSavedValues();
        HasUnsavedChanges = false;
        StopConfirmTimer();
        SaveBarVisible = false;

        // Dev edition only: read the emulator state so the section opens already populated.
        if (IsDevEdition)
        {
            await RefreshDevEmulatorAsync();
        }
    }

    [RelayCommand]
    private async Task SaveAsync()
    {
        var accentValid = AccentMath.TryParse(AccentColor) is not null;

        long? accountId = null;
        var accountText = PlayerAccountId.Trim();
        var accountValid = true;
        if (accountText.Length > 0)
        {
            if (long.TryParse(accountText, CultureInfo.InvariantCulture, out var parsed) && parsed > 0)
            {
                accountId = parsed;
            }
            else
            {
                accountValid = false;
            }
        }

        await _settings.UpdateSettingsAsync(s => s with
        {
            Theme = Theme,
            AccentColor = accentValid ? AccentColor.Trim() : s.AccentColor,
            PlayerIgn = PlayerIgn.Trim(),
            PlayerAccountId = accountId,
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
            AutoRelaunchOnCrash = AutoRelaunchOnCrash,
            UseMockMetricsProvider = UseMockMetricsProvider,
            UseDeveloperEmulator = UseDeveloperEmulator,
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

        if (!accountValid)
        {
            PlayerAccountId = (await _settings.GetSettingsAsync()).PlayerAccountId?.ToString(CultureInfo.InvariantCulture) ?? string.Empty;
        }

        CaptureSavedValues();
        HasUnsavedChanges = false;

        if (!accentValid && !accountValid)
        {
            AccentColor = (await _settings.GetSettingsAsync()).AccentColor;
            ShowSaveConfirmation("Settings saved. The accent was not valid hex and the account id was not a number, so both were kept as they were.");
        }
        else if (!accentValid)
        {
            AccentColor = (await _settings.GetSettingsAsync()).AccentColor;
            ShowSaveConfirmation("Settings saved. Accent color was not a valid hex value, so the previous accent was kept.");
        }
        else if (!accountValid)
        {
            ShowSaveConfirmation("Settings saved. The account id was not a number, so it was left unchanged; use the id shown on your Critical Ops profile page.");
        }
        else
        {
            ShowSaveConfirmation(autostartError is null
                ? "Settings saved."
                : "Settings saved, but the start-with-Windows entry could not be updated: " + autostartError);
        }
    }

    private string? ApplyStartWithWindows() => Services.AutostartService.Apply(StartWithWindows);

    #region Developer emulator (Dev edition builds only)

    public bool IsDevEdition
    {
#if DEVEDITION
        get => true;
#else
        get => false;
#endif
    }

    public bool DevEmulatorSectionVisible => IsDevEdition;

    [ObservableProperty] private bool _devEmulatorInstalled;
    [ObservableProperty] private string _devEmulatorStatus = string.Empty;
    [ObservableProperty] private string _devGpuSummary = string.Empty;

    [ObservableProperty] private bool _devGpuHighPerformance;
    [ObservableProperty] private bool _devGpuPrioritization;
    [ObservableProperty] private bool _devGpuRetrievalRetry;

    [ObservableProperty] private string _devGameStatus = "not checked";
    [ObservableProperty] private bool _devHasAdb;
    [ObservableProperty] private string _devRendererStatus = "not checked";
    [ObservableProperty] private bool _devForceAngle;
    [ObservableProperty] private bool _devBusy;
    [ObservableProperty] private string _devStatusLine = string.Empty;

    [ObservableProperty] private string _devRefreshRateText = "60";
    [ObservableProperty] private string _devRefreshStatus = "not checked";

    private string? _devAdbPath;

    [RelayCommand]
    private async Task LaunchDevGameAsync()
    {
        if (_devEmulator is null)
        {
            return;
        }

        DevBusy = true;
        try
        {
            DevStatusLine = "starting the emulator…";
            DevStatusLine = await _devEmulator.LaunchGameAsync(await GetDevInstallOverrideAsync());
        }
        finally
        {
            DevBusy = false;
        }
    }

    private async Task<string?> GetDevInstallOverrideAsync()
    {
        var rules = await _settings.GetDetectionRulesAsync();
        return string.IsNullOrWhiteSpace(rules.DeveloperEmulatorInstallPath) ? null : rules.DeveloperEmulatorInstallPath;
    }

    [RelayCommand]
    private async Task RefreshDevEmulatorAsync()
    {
        if (_devEmulator is null)
        {
            return;
        }

        var snapshot = await _devEmulator.ReadAsync(await GetDevInstallOverrideAsync());

        DevEmulatorInstalled = snapshot.Installed;
        DevHasAdb = snapshot.AdbPath is not null;
        _devAdbPath = snapshot.AdbPath;

        if (!snapshot.Installed)
        {
            DevEmulatorStatus = "Google Play Games Developer Emulator was not found on this PC.";
            DevGpuSummary = string.Empty;
            DevGameStatus = "unavailable";
            return;
        }

        DevEmulatorStatus = snapshot.EmulatorRunning
            ? $"v{snapshot.Version} — emulator is running (quit it from the tray before applying startup flags)"
            : $"v{snapshot.Version} — not running";

        _devGpuPreferenceOnDisk = snapshot.WindowsGpuPreference;
        DevGpuHighPerformance = snapshot.WindowsGpuPreference == Optima.Platform.Windows.Services.GpuPreferenceKind.HighPerformance;

        _devGpuPrioritizationOnDisk = snapshot.GpuPrioritization.Present;
        DevGpuPrioritization = snapshot.GpuPrioritization.Present;

        _devGpuRetrievalOnDisk = snapshot.GpuRetrievalRetry.Present;
        DevGpuRetrievalRetry = snapshot.GpuRetrievalRetry.Present;

        DevGpuSummary = snapshot.Gpus.Count == 0
            ? "no GPU information available"
            : string.Join("  •  ", snapshot.Gpus.Select(g => $"{g.Name} ({g.DriverVersion})"));

        await RefreshDevGameStatusAsync();
    }

    [RelayCommand]
    private async Task RefreshDevGameStatusAsync()
    {
        if (_devEmulator is null || !DevEmulatorInstalled)
        {
            return;
        }

        if (_devAdbPath is null)
        {
            DevGameStatus = "adb.exe not found in the emulator install";
            return;
        }

        var (attached, running) = await _devEmulator.CheckGameStatusAsync(_devAdbPath);
        DevGameStatus = attached
            ? running
                ? "emulator attached — Critical Ops is running in it"
                : "emulator attached — game not started"
            : "emulator not attached on adb localhost:6520 (start it, or launch via Optima)";

        var (forced, renderer) = await _devEmulator.ReadGuestRendererStatusAsync(_devAdbPath);
        DevForceAngle = forced;
        DevRendererStatus = renderer;

        var (peak, _, guestStatus) = await _devEmulator.ReadGuestRefreshRatesAsync(_devAdbPath);
        var (hostRate, hostGpuRate, _) = _devEmulator.ReadHostRefreshRate();
        if (peak is not null)
        {
            DevRefreshRateText = peak.Value.ToString(CultureInfo.InvariantCulture);
        }
        else if (hostRate is not null)
        {
            DevRefreshRateText = hostRate.Value.ToString(CultureInfo.InvariantCulture);
        }

        DevRefreshStatus = hostRate is null
            ? guestStatus
            : $"{guestStatus} • host config {hostRate} Hz ({hostGpuRate})";
    }

    [RelayCommand]
    private async Task ApplyDevRefreshRateAsync()
    {
        if (_devEmulator is null)
        {
            return;
        }

        if (!int.TryParse(DevRefreshRateText.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var rate)
            || rate is < 30 or > 240)
        {
            DevRefreshStatus = "enter a refresh rate between 30 and 240";
            return;
        }

        DevBusy = true;
        try
        {
            // 1. Guest rates over adb (no elevation needed).
            var guest = await _devEmulator.ApplyGuestRefreshRateAsync(_devAdbPath, rate);

            // 2. Host config through the elevated helper (Google's Service.exe.config).
            var host = "host config not updated";
            if (await _broker.EnsureStartedAsync())
            {
                var response = await _broker.SendAsync(new IpcRequest
                {
                    Command = IpcCommand.SetDevEmulatorRefreshRate,
                    Args = { ["refreshRate"] = rate.ToString(CultureInfo.InvariantCulture) },
                }, CancellationToken.None);
                host = response.Success
                    ? $"host config set to {rate} Hz"
                    : "host config failed: " + response.Error;
            }
            else
            {
                host = "host config skipped (administrator prompt declined)";
            }

            DevStatusLine = $"{rate} Hz — {guest}. {host}. Restart the emulator for the host change to take effect.";
            await RefreshDevGameStatusAsync();
        }
        finally
        {
            DevBusy = false;
        }
    }

    [RelayCommand]
    private async Task ApplyGuestRendererAsync()
    {
        if (_devEmulator is null)
        {
            return;
        }

        DevBusy = true;
        try
        {
            DevRendererStatus = "applying…";
            DevRendererStatus = await _devEmulator.ApplyGuestAngleAsync(_devAdbPath, DevForceAngle);
        }
        finally
        {
            DevBusy = false;
        }
    }

    [RelayCommand]
    private async Task ApplyDevEmulatorAsync()
    {
        if (_devEmulator is null)
        {
            return;
        }

        var messages = new List<string>();

        var preferenceNow = DevGpuHighPerformance
            ? Optima.Platform.Windows.Services.GpuPreferenceKind.HighPerformance
            : Optima.Platform.Windows.Services.GpuPreferenceKind.Auto;
        if (preferenceNow != _devGpuPreferenceOnDisk)
        {
            var ok = await _devEmulator.SetWindowsGpuPreferenceAsync(preferenceNow, await GetDevInstallOverrideAsync());
            messages.Add(ok
                ? preferenceNow == Optima.Platform.Windows.Services.GpuPreferenceKind.HighPerformance
                    ? "crosvm pinned to the high-performance GPU (takes effect next emulator start)"
                    : "crosvm GPU pin removed (takes effect next emulator start)"
                : "could not write the GPU preference");
            _devGpuPreferenceOnDisk = preferenceNow;
        }

        try
        {
            if (DevGpuPrioritization != _devGpuPrioritizationOnDisk)
            {
                await _devEmulator.SetStartupFlagAsync(Optima.Platform.Windows.Services.DevEmulatorSnapshot.GpuPrioritizationFlag, DevGpuPrioritization);
                messages.Add(DevGpuPrioritization ? "IDXGI GPU prioritization flag enabled" : "IDXGI GPU prioritization flag removed");
                _devGpuPrioritizationOnDisk = DevGpuPrioritization;
            }

            if (DevGpuRetrievalRetry != _devGpuRetrievalOnDisk)
            {
                await _devEmulator.SetStartupFlagAsync(Optima.Platform.Windows.Services.DevEmulatorSnapshot.GpuRetrievalRetryFlag, DevGpuRetrievalRetry);
                messages.Add(DevGpuRetrievalRetry ? "GPU retrieval retry flag enabled" : "GPU retrieval retry flag removed");
                _devGpuRetrievalOnDisk = DevGpuRetrievalRetry;
            }
        }
        catch (InvalidOperationException ex)
        {
            SaveBarMark = "[ ! ]";
            SaveBarText = ex.Message;
            SaveBarVisible = true;
            return;
        }

        SaveBarMark = "[ OK ]";
        SaveBarText = messages.Count == 0
            ? "Developer emulator settings already match."
            : "Applied. " + string.Join(". ", messages) + ".";
        SaveBarVisible = true;

        await RefreshDevEmulatorAsync();
    }

    [RelayCommand]
    private void OpenDevEmulatorLogs()
    {
        var path = System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Google", "Play Games Developer Emulator", "Logs");
        if (System.IO.Directory.Exists(path))
        {
            _devEmulator?.OpenInExplorer(path);
        }
    }

    #endregion
}
