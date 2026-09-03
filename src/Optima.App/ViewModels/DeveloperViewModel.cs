using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Optima.Core.Abstractions;
using Optima.Core.Configuration;
using Optima.Core.Ipc;

namespace Optima.App.ViewModels;

/// <summary>DEVELOPER page (§28): raw detected processes, resolved paths, driver capabilities and helper status.</summary>
public sealed partial class DeveloperViewModel : ObservableObject
{
    private readonly IProcessMonitor _processMonitor;
    private readonly IGameDetector _detector;
    private readonly IVirtualDisplayProvider _provider;
    private readonly IElevationBroker _elevation;
    private readonly SettingsService _settings;

    public DeveloperViewModel(
        IProcessMonitor processMonitor,
        IGameDetector detector,
        IVirtualDisplayProvider provider,
        IElevationBroker elevation,
        SettingsService settings)
    {
        _processMonitor = processMonitor;
        _detector = detector;
        _provider = provider;
        _elevation = elevation;
        _settings = settings;
    }

    public ObservableCollection<TrackedProcess> TrackedProcesses { get; } = [];
    public ObservableCollection<InfoRow> ResolvedPaths { get; } = [];
    public ObservableCollection<InfoRow> ProviderInfo { get; } = [];
    public ObservableCollection<InfoRow> EtwProbeResults { get; } = [];

    [ObservableProperty] private string _helperStatus = "Not started";
    [ObservableProperty] private string _detectionRulesJson = string.Empty;
    [ObservableProperty] private string _etwProbeStatus = "not run · needs the elevated helper";

    [RelayCommand]
    public async Task RefreshAsync(CancellationToken ct = default)
    {
        TrackedProcesses.Clear();
        foreach (var process in await _processMonitor.GetTrackedProcessesAsync(ct))
        {
            TrackedProcesses.Add(process);
        }

        ResolvedPaths.Clear();
        var platform = await _detector.DetectPlatformAsync(ct);
        if (platform is not null)
        {
            ResolvedPaths.Add(new InfoRow("Install directory", platform.InstallDirectory));
            ResolvedPaths.Add(new InfoRow("Bootstrapper", platform.BootstrapperPath));
            ResolvedPaths.Add(new InfoRow("Client", platform.ClientPath));
            ResolvedPaths.Add(new InfoRow("Emulator", platform.EmulatorPath));
            ResolvedPaths.Add(new InfoRow("Version", platform.Version));
            ResolvedPaths.Add(new InfoRow("Protocol handler", platform.ProtocolHandlerRegistered ? "registered" : "missing"));
        }
        var game = await _detector.DetectTargetGameAsync(ct);
        if (game is not null)
        {
            ResolvedPaths.Add(new InfoRow("Game package", game.PackageId));
            ResolvedPaths.Add(new InfoRow("Launch URI", game.LaunchUri));
            ResolvedPaths.Add(new InfoRow("Shortcut", game.ShortcutPath));
        }

        ProviderInfo.Clear();
        ProviderInfo.Add(new InfoRow("Provider", _provider.Name));
        var capabilities = await _provider.GetCapabilitiesAsync(ct);
        ProviderInfo.Add(new InfoRow("Capabilities",
            $"customModes={capabilities.SupportsCustomModes} gpuPin={capabilities.SupportsGpuPinning} " +
            $"toggle={capabilities.SupportsEnableDisable} admin={capabilities.RequiresElevation}"));
        var modes = await _provider.GetSupportedModesAsync(ct);
        ProviderInfo.Add(new InfoRow("Advertised modes", string.Join(", ", modes.Take(12)) + (modes.Count > 12 ? " …" : string.Empty)));
        ProviderInfo.Add(new InfoRow("Driver credit", "Virtual Display Driver by MikeTheTech (MIT) · THIRD-PARTY-NOTICES.md"));

        HelperStatus = _elevation.IsConnected ? "Connected" : "Not running";

        var rules = await _settings.GetDetectionRulesAsync(ct);
        DetectionRulesJson = System.Text.Json.JsonSerializer.Serialize(rules, JsonStore.Options);
    }

    [RelayCommand]
    private async Task PingHelperAsync()
    {
        if (!await _elevation.EnsureStartedAsync())
        {
            HelperStatus = "Declined / unavailable";
            return;
        }
        var response = await _elevation.SendAsync(new IpcRequest { Command = IpcCommand.Ping });
        HelperStatus = response.Success ? "Connected (ping ok)" : $"Error: {response.Error}";
    }

    [RelayCommand]
    private async Task RunEtwProbeAsync()
    {
        EtwProbeResults.Clear();
        EtwProbeStatus = "probing for 10 seconds, keep the game running";

        if (!await _elevation.EnsureStartedAsync())
        {
            EtwProbeStatus = "helper declined / unavailable";
            return;
        }

        var response = await _elevation.SendAsync(new IpcRequest
        {
            Command = IpcCommand.RunEtwProbe,
            Args = { ["durationSeconds"] = "10" },
        });
        if (!response.Success)
        {
            EtwProbeStatus = $"probe failed: {response.Error}";
            return;
        }

        var tracked = await _processMonitor.GetTrackedProcessesAsync();
        var rows = new List<(long Count, string Label, string Detail)>();
        foreach (var (key, value) in response.Data)
        {
            if (!key.StartsWith("pid:", StringComparison.Ordinal)
                || !int.TryParse(key[4..], out var pid)
                || !long.TryParse(value, out var count))
            {
                continue;
            }
            var match = tracked.FirstOrDefault(p => p.ProcessId == pid);
            var name = match?.Name;
            if (name is null)
            {
                try
                {
                    name = System.Diagnostics.Process.GetProcessById(pid).ProcessName;
                }
                catch (Exception)
                {
                    name = "unknown";
                }
            }
            var kind = match is not null ? $" [{match.Kind.ToString().ToUpperInvariant()}]" : string.Empty;
            rows.Add((count, $"pid {pid} {name}{kind}", $"{count} presents"));
        }

        foreach (var row in rows.OrderByDescending(r => r.Count))
        {
            EtwProbeResults.Add(new InfoRow(row.Label, row.Detail));
        }
        EtwProbeStatus = rows.Count == 0
            ? "no DXGI presents observed · is the game running and unpaused?"
            : $"{rows.Count} presenting processes · highest count is the real presenter";
    }
}
