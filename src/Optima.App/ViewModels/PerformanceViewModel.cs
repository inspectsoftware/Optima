using System.Collections.ObjectModel;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Optima.Core.Abstractions;
using Optima.Core.Configuration;
using Optima.Core.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;

namespace Optima.App.ViewModels;

/// <summary>One toggleable Windows tweak row, grouped by category on the PERFORMANCE page.</summary>
public sealed partial class TweakRowViewModel : ObservableObject
{
    public TweakRowViewModel(TweakDefinition definition, TweakStatus status)
    {
        Definition = definition;
        _status = status;
    }

    public TweakDefinition Definition { get; }
    public string Name => Definition.Name;
    public string WhatItChanges => Definition.WhatItChanges;
    public string PotentialBenefit => Definition.PotentialBenefit;
    public string PotentialDownside => Definition.PotentialDownside;
    public bool RequiresElevation => Definition.RequiresElevation;
    public bool RequiresRestart => Definition.RequiresRestart;
    public bool IsModerateRisk => Definition.Risk == TweakRisk.Moderate;

    [ObservableProperty] private TweakStatus _status;
}

public sealed record TweakGroupViewModel(string Category, IReadOnlyList<TweakRowViewModel> Tweaks);

/// <summary>
/// PERFORMANCE page (§8/§22): the live system visualizer, hardware inventory and virtualization
/// facts (merged from the old SYSTEM page), the Windows tweak catalog, and profile editing.
/// </summary>
public sealed partial class PerformanceViewModel : ObservableObject
{
    private const int HistoryLength = 120;
    private static readonly TimeSpan NetworkStaleness = TimeSpan.FromSeconds(5);
    private const string NetworkIdleText = "not measuring · starts with a game session";

    private readonly ProfileService _profiles;
    private readonly ITweakService _tweaks;
    private readonly PlayViewModel _play;
    private readonly ISystemInfoService _systemInfo;
    private readonly SettingsService _settings;
    private readonly IPerformanceMonitor _monitor;
    private readonly ILogger<PerformanceViewModel> _logger;
    private readonly DispatcherTimer _networkStaleness;

    private DateTimeOffset _lastNetworkSample = DateTimeOffset.MinValue;

    public PerformanceViewModel(
        ProfileService profiles,
        ITweakService tweaks,
        PlayViewModel play,
        ISystemInfoService systemInfo,
        SettingsService settings,
        IPerformanceMonitor monitor,
        INetworkQualityMonitor network,
        ILogger<PerformanceViewModel> logger)
    {
        _profiles = profiles;
        _tweaks = tweaks;
        _play = play;
        _systemInfo = systemInfo;
        _settings = settings;
        _monitor = monitor;
        _logger = logger;

        network.SampleArrived += OnNetworkSample;
        _monitor.MetricsUpdated += OnMetrics;

        // Runs from a network sample until the line it wrote has gone stale, not for the life of
        // the process: without samples the idle text is already there.
        _networkStaleness = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _networkStaleness.Tick += (_, _) =>
        {
            if (DateTimeOffset.Now - _lastNetworkSample > NetworkStaleness)
            {
                NetworkStatus = NetworkIdleText;
                _networkStaleness.Stop();
            }
        };
    }

    // ── Live system load (1 s ticks from the hardware monitor) ──
    public HistorySeries CpuHistory { get; } = new(HistoryLength);
    public HistorySeries GpuHistory { get; } = new(HistoryLength);
    public HistorySeries RamHistory { get; } = new(HistoryLength);

    [ObservableProperty] private double _cpuPercent;
    [ObservableProperty] private string _cpuText = "---";
    [ObservableProperty] private double _gpuPercent;
    [ObservableProperty] private string _gpuText = "---";
    [ObservableProperty] private double _ramPercent;
    [ObservableProperty] private string _ramText = "---";
    [ObservableProperty] private string _gameLoadText = string.Empty;
    [ObservableProperty] private string _gpuTempText = string.Empty;

    // ── Hardware inventory and virtualization (from the old SYSTEM page) ──
    public ObservableCollection<InfoRow> HardwareRows { get; } = [];
    public ObservableCollection<InfoRow> VirtualizationRows { get; } = [];
    public ObservableCollection<MonitorRow> Displays { get; } = [];

    [ObservableProperty] private string _networkStatus = NetworkIdleText;

    public ObservableCollection<LaunchProfile> Profiles { get; } = [];
    public ObservableCollection<TweakGroupViewModel> TweakGroups { get; } = [];
    public IReadOnlyList<SettingExplanation> Explanations => SettingExplanations.All;

    [ObservableProperty] private string _tweaksStatus = string.Empty;
    [ObservableProperty] private bool _tweaksBusy;
    [ObservableProperty] private bool _sessionGameBarOff;
    [ObservableProperty] private bool _sessionFseOff;

    public IReadOnlyList<PowerPlanKind> PowerPlanOptions { get; } = Enum.GetValues<PowerPlanKind>();
    public IReadOnlyList<ProcessPriorityLevel> PriorityOptions { get; } = Enum.GetValues<ProcessPriorityLevel>();

    [ObservableProperty] private LaunchProfile? _selectedProfile;
    [ObservableProperty] private string _editName = string.Empty;
    [ObservableProperty] private PowerPlanKind _editPowerPlan = PowerPlanKind.Unchanged;
    [ObservableProperty] private ProcessPriorityLevel _editPriority = ProcessPriorityLevel.Unchanged;
    [ObservableProperty] private bool _editDisableThrottling;
    [ObservableProperty] private string _editCleanupList = string.Empty;
    [ObservableProperty] private string _editorStatus = string.Empty;

    public async Task InitializeAsync(CancellationToken ct = default)
    {
        await RefreshSystemAsync(ct);
        await ReloadProfilesAsync(ct);
        await ReloadTweaksAsync(ct);

        var settings = await _settings.GetSettingsAsync(ct);
        _loadingSessionTweaks = true;
        try
        {
            SessionGameBarOff = settings.SessionTweakGameBarOff;
            SessionFseOff = settings.SessionTweakFseOff;
        }
        finally
        {
            _loadingSessionTweaks = false;
        }
    }

    // Showing the stored values is not a change to store.
    private bool _loadingSessionTweaks;

    // Each switch saves its own value only. Saving both wrote the one that had not been
    // loaded yet as "off" whenever the other was clicked while the page was still loading.
    partial void OnSessionGameBarOffChanged(bool value) => PersistSessionTweak(s => s with { SessionTweakGameBarOff = value });
    partial void OnSessionFseOffChanged(bool value) => PersistSessionTweak(s => s with { SessionTweakFseOff = value });

    private async void PersistSessionTweak(Func<AppSettings, AppSettings> change)
    {
        if (_loadingSessionTweaks)
        {
            return;
        }
        try
        {
            await _settings.UpdateSettingsAsync(change);
        }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException)
        {
            TweaksStatus = "That switch could not be saved: " + ex.Message;
        }
    }

    [RelayCommand]
    private async Task ReloadTweaksAsync(CancellationToken ct = default)
    {
        try
        {
            var states = await _tweaks.GetStatesAsync(ct);
            var groups = states.GroupBy(s => s.Definition.Category).ToList();

            // The page asks again each time it is opened. When the answer is the tweaks already
            // listed, only their states are written: new rows would close every row the user had
            // opened to read, and build the whole list again for nothing.
            var rows = TweakGroups.SelectMany(g => g.Tweaks).ToList();
            var current = groups.SelectMany(g => g).ToList();
            if (rows.Select(r => r.Definition.Id).SequenceEqual(current.Select(s => s.Definition.Id)))
            {
                for (var i = 0; i < rows.Count; i++)
                {
                    rows[i].Status = current[i].Status;
                }
                return;
            }

            TweakGroups.Clear();
            foreach (var group in groups)
            {
                TweakGroups.Add(new TweakGroupViewModel(
                    group.Key.ToUpperInvariant(),
                    group.Select(s => new TweakRowViewModel(s.Definition, s.Status)).ToList()));
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Reading tweak states failed");
            TweaksStatus = "Could not read the current tweak states. See the log on the Debug page.";
        }
    }

    [RelayCommand]
    private async Task ToggleTweakAsync(TweakRowViewModel row)
    {
        if (TweaksBusy)
        {
            return;
        }
        TweaksBusy = true;
        var enable = row.Status == TweakStatus.Disabled;
        try
        {
            var state = await _tweaks.SetEnabledAsync(row.Definition.Id, enable);
            row.Status = state.Status;
            TweaksStatus = enable
                ? $"'{row.Name}' enabled." + (row.RequiresRestart ? " Takes full effect after a Windows restart." : string.Empty)
                : $"'{row.Name}' disabled; original values restored.";
        }
        catch (OptimaException ex)
        {
            TweaksStatus = $"{ex.Error.Title} {ex.Error.SuggestedFixes.FirstOrDefault()}";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Toggling tweak {Tweak} failed", row.Definition.Id);
            TweaksStatus = "The tweak change failed. See the log on the Debug page.";
        }
        finally
        {
            TweaksBusy = false;
        }
    }

    [RelayCommand]
    private async Task DisableAllTweaksAsync()
    {
        if (TweaksBusy)
        {
            return;
        }
        TweaksBusy = true;
        var reverted = 0;
        try
        {
            foreach (var row in TweakGroups.SelectMany(g => g.Tweaks)
                         .Where(r => r.Status is TweakStatus.Enabled or TweakStatus.Mixed))
            {
                var state = await _tweaks.SetEnabledAsync(row.Definition.Id, enable: false);
                row.Status = state.Status;
                reverted++;
            }
            TweaksStatus = reverted == 0 ? "No tweaks are enabled." : $"Reverted {reverted} tweak(s) to original values.";
        }
        catch (OptimaException ex)
        {
            TweaksStatus = $"{ex.Error.Title} {ex.Error.SuggestedFixes.FirstOrDefault()}";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Disabling all tweaks failed");
            TweaksStatus = "Reverting tweaks failed part-way. See the log on the Debug page.";
        }
        finally
        {
            TweaksBusy = false;
        }
    }

    private async Task ReloadProfilesAsync(CancellationToken ct = default)
    {
        // Clearing the list deselects the profile, and the editor then loads the first one over
        // whatever was being typed; an unchanged list is left alone so opening the page keeps both.
        BoundRows.Replace(Profiles, await _profiles.GetProfilesAsync(ct));
        SelectedProfile ??= Profiles.FirstOrDefault();
    }

    partial void OnSelectedProfileChanged(LaunchProfile? value)
    {
        if (value is null)
        {
            return;
        }
        EditName = value.IsBuiltIn ? value.Name + " (copy)" : value.Name;
        EditPowerPlan = value.Performance.PowerPlan;
        EditPriority = value.Performance.Priority;
        EditDisableThrottling = value.Performance.DisablePowerThrottling;
        EditCleanupList = string.Join(", ", value.Performance.CleanupProcessNames);
        EditorStatus = value.IsBuiltIn ? "Built-in profile. Saving creates a copy under the new name." : string.Empty;
    }

    [RelayCommand]
    private async Task SaveProfileAsync()
    {
        try
        {
            var profile = new LaunchProfile
            {
                Name = EditName.Trim(),
                Performance = new PerformanceProfile
                {
                    PowerPlan = EditPowerPlan,
                    Priority = EditPriority,
                    DisablePowerThrottling = EditDisableThrottling,
                    CleanupProcessNames = EditCleanupList
                        .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
                },
            };
            if (profile.Name.Length == 0)
            {
                EditorStatus = "Give the profile a name first.";
                return;
            }

            await _profiles.SaveProfileAsync(profile);
            await ReloadProfilesAsync();
            await _play.InitializeAsync();
            SelectedProfile = Profiles.FirstOrDefault(p => p.Name == profile.Name);
            EditorStatus = $"Saved '{profile.Name}'.";
        }
        catch (InvalidOperationException ex)
        {
            EditorStatus = ex.Message;
        }
    }

    [RelayCommand]
    private async Task DeleteProfileAsync()
    {
        if (SelectedProfile is null || SelectedProfile.IsBuiltIn)
        {
            EditorStatus = "Built-in profiles cannot be deleted.";
            return;
        }
        await _profiles.DeleteProfileAsync(SelectedProfile.Name);
        await ReloadProfilesAsync();
        await _play.InitializeAsync();
        SelectedProfile = Profiles.FirstOrDefault();
        EditorStatus = "Profile deleted.";
    }

    [RelayCommand]
    private async Task ExportProfileAsync()
    {
        if (SelectedProfile is null)
        {
            return;
        }
        var dialog = new SaveFileDialog
        {
            Filter = "Optima profile (*.json)|*.json",
            FileName = SelectedProfile.Name + ".json",
        };
        if (dialog.ShowDialog() == true)
        {
            try
            {
                await _profiles.ExportProfileAsync(SelectedProfile.Name, dialog.FileName);
                EditorStatus = $"Exported to {dialog.FileName}.";
            }
            catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException)
            {
                EditorStatus = "Could not write the export: " + ex.Message;
            }
        }
    }

    [RelayCommand]
    private async Task ImportProfileAsync()
    {
        var dialog = new OpenFileDialog { Filter = "Optima profile (*.json)|*.json|All files|*.*" };
        if (dialog.ShowDialog() != true)
        {
            return;
        }
        try
        {
            var imported = await _profiles.ImportProfileAsync(dialog.FileName);
            await ReloadProfilesAsync();
            await _play.InitializeAsync();
            SelectedProfile = Profiles.FirstOrDefault(p => p.Name == imported.Name);
            EditorStatus = $"Imported '{imported.Name}'.";
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.Text.Json.JsonException
            or System.IO.IOException or UnauthorizedAccessException)
        {
            EditorStatus = "That file is not a valid profile.";
            _logger.LogWarning(ex, "Profile import failed");
        }
    }

    [RelayCommand]
    private async Task RefreshSystemAsync(CancellationToken ct = default)
    {
        var inventory = await _systemInfo.GetInventoryAsync(ct);

        var hardware = new List<InfoRow>
        {
            new("CPU", $"{inventory.CpuName} ({inventory.CpuCores}C/{inventory.CpuThreads}T)"),
        };
        foreach (var gpu in inventory.Gpus)
        {
            var vram = gpu.VramBytes > 0 ? $", {gpu.VramBytes / (1024.0 * 1024 * 1024):F0} GB VRAM" : string.Empty;
            hardware.Add(new InfoRow($"GPU ({gpu.Vendor})", $"{gpu.Name}, driver {gpu.DriverVersion}{vram}"));
        }
        hardware.Add(new InfoRow("RAM", $"{inventory.TotalRamBytes / (1024.0 * 1024 * 1024):F0} GB"));
        hardware.Add(new InfoRow("Windows", inventory.WindowsVersion));
        BoundRows.Replace(HardwareRows, hardware);

        var virtualization = inventory.Virtualization;
        BoundRows.Replace(VirtualizationRows,
        [
            new InfoRow("Firmware virtualization", Tri(virtualization.FirmwareVirtualizationEnabled)),
            new InfoRow("Hypervisor running", Tri(virtualization.HypervisorPresent)),
            new InfoRow("Hyper-V feature", Tri(virtualization.HyperVFeatureEnabled)),
            new InfoRow("Virtual Machine Platform", Tri(virtualization.VirtualMachinePlatformEnabled)),
            new InfoRow("Windows Hypervisor Platform", Tri(virtualization.WindowsHypervisorPlatformEnabled)),
        ]);

        var overrides = (await _settings.GetSettingsAsync(ct)).DisplayOverrides;
        BoundRows.Replace(Displays, inventory.Displays
            .Select(display => new MonitorRow(
                display.DeviceName,
                DisplayPresentation.CustomName(display, overrides) ?? display.FriendlyName,
                display.CurrentMode.ToString()))
            .ToList());
    }

    private void OnNetworkSample(object? sender, NetworkQualitySample sample)
    {
        System.Windows.Application.Current?.Dispatcher.BeginInvoke(() =>
        {
            _lastNetworkSample = DateTimeOffset.Now;
            var suffix = sample.IsReferenceHost ? " [ REF HOST ] link quality" : $" · {sample.Target}";
            NetworkStatus = $"{sample.PingMs:F0} ms · {sample.JitterMs:F1} ms jitter · {sample.PacketLossPct:F1}% loss{suffix}";
            _networkStaleness.Start();
        });
    }

    private void OnMetrics(object? sender, HardwareMetrics metrics)
    {
        System.Windows.Application.Current?.Dispatcher.BeginInvoke(() =>
        {
            CpuPercent = metrics.CpuUtilizationPercent;
            GpuPercent = metrics.GpuUtilizationPercent;
            RamPercent = metrics.RamTotalBytes > 0
                ? 100.0 * metrics.RamUsedBytes / metrics.RamTotalBytes
                : 0;

            CpuText = $"{CpuPercent:F0}%";
            GpuText = $"{GpuPercent:F0}%";
            RamText = $"{metrics.RamUsedBytes / (1024.0 * 1024 * 1024):F1} / {metrics.RamTotalBytes / (1024.0 * 1024 * 1024):F0} GB";
            GpuTempText = metrics.GpuTemperatureCelsius is { } temp ? $"{temp:F0}°C" : string.Empty;

            CpuHistory.Append(CpuPercent);
            GpuHistory.Append(GpuPercent);
            RamHistory.Append(RamPercent);

            var parts = new List<string>();
            if (metrics.GameCpuPercent > 0)
            {
                parts.Add($"game cpu {metrics.GameCpuPercent:F0}%");
            }
            if (metrics.GameRamBytes > 0)
            {
                parts.Add($"game ram {metrics.GameRamBytes / (1024.0 * 1024 * 1024):F1} GB");
            }
            GameLoadText = string.Join(" · ", parts);
        });
    }

    private static string Tri(bool? value) => value switch
    {
        true => "Enabled",
        false => "Disabled",
        null => "Unknown",
    };
}

public sealed record InfoRow(string Label, string Value);

public sealed record MonitorRow(string DeviceName, string Name, string Mode);

/// <summary>
/// Refills a bound list only when what it holds has changed. Every page is asked to load again
/// each time it is opened, and clearing a list throws away the visuals built for its rows along
/// with what they held: the selection, the scroll position, a row left open.
/// </summary>
internal static class BoundRows
{
    public static void Replace<T>(ObservableCollection<T> target, IReadOnlyList<T> rows)
    {
        if (target.SequenceEqual(rows))
        {
            return;
        }

        target.Clear();
        foreach (var row in rows)
        {
            target.Add(row);
        }
    }
}
