using System.IO;
using Optima.Core.Abstractions;
using Optima.Core.Models;

namespace Optima.App.Diagnostics;

/// <summary>Diagnostics page checks (§15/§16). Each returns status + reason + recommended fix.</summary>
public sealed class VirtualizationCheck : IDiagnosticCheck
{
    private readonly ISystemInfoService _systemInfo;
    public VirtualizationCheck(ISystemInfoService systemInfo) => _systemInfo = systemInfo;

    public string Name => "Virtualization";
    public int Order => 10;

    public async Task<DiagnosticResult> RunAsync(CancellationToken ct = default)
    {
        var state = await _systemInfo.GetVirtualizationStateAsync(ct);
        if (state.HypervisorPresent == true)
        {
            return new DiagnosticResult
            {
                CheckName = Name,
                Status = DiagnosticStatus.Pass,
                Reason = "A hypervisor is running — hardware virtualization is active.",
            };
        }
        if (state.FirmwareVirtualizationEnabled == true)
        {
            return new DiagnosticResult
            {
                CheckName = Name,
                Status = DiagnosticStatus.Warning,
                Reason = "Virtualization is enabled in firmware but no hypervisor is running.",
                RecommendedFix = "Enable Virtual Machine Platform / Windows Hypervisor Platform in Windows optional features, then restart.",
            };
        }
        return new DiagnosticResult
        {
            CheckName = Name,
            Status = DiagnosticStatus.Fail,
            Reason = "Hardware virtualization looks disabled.",
            RecommendedFix = "Enable virtualization (Intel VT-x / AMD-V / SVM) in the BIOS/UEFI firmware settings. This cannot be changed from Windows.",
        };
    }
}

public sealed class WindowsHypervisorCheck : IDiagnosticCheck
{
    private readonly ISystemInfoService _systemInfo;
    public WindowsHypervisorCheck(ISystemInfoService systemInfo) => _systemInfo = systemInfo;

    public string Name => "Windows Hypervisor";
    public int Order => 20;

    public async Task<DiagnosticResult> RunAsync(CancellationToken ct = default)
    {
        var state = await _systemInfo.GetVirtualizationStateAsync(ct);
        var anyPlatform = state.VirtualMachinePlatformEnabled == true
            || state.WindowsHypervisorPlatformEnabled == true
            || state.HyperVFeatureEnabled == true
            || state.HypervisorPresent == true;

        return new DiagnosticResult
        {
            CheckName = Name,
            Status = anyPlatform ? DiagnosticStatus.Pass : DiagnosticStatus.Fail,
            Reason = anyPlatform
                ? Describe(state)
                : "No Windows hypervisor feature (Hyper-V / Virtual Machine Platform / Windows Hypervisor Platform) is enabled.",
            RecommendedFix = anyPlatform
                ? string.Empty
                : "Turn on 'Virtual Machine Platform' in Windows Features (OptionalFeatures.exe) and restart — Google Play Games requires it.",
        };
    }

    private static string Describe(VirtualizationState s)
    {
        var parts = new List<string>();
        if (s.HyperVFeatureEnabled == true) { parts.Add("Hyper-V"); }
        if (s.VirtualMachinePlatformEnabled == true) { parts.Add("Virtual Machine Platform"); }
        if (s.WindowsHypervisorPlatformEnabled == true) { parts.Add("Windows Hypervisor Platform"); }
        if (parts.Count == 0) { parts.Add("hypervisor running"); }
        return "Enabled: " + string.Join(", ", parts) + ".";
    }
}

public sealed class GooglePlayGamesCheck : IDiagnosticCheck
{
    private readonly IGameDetector _detector;
    public GooglePlayGamesCheck(IGameDetector detector) => _detector = detector;

    public string Name => "Google Play Games";
    public int Order => 30;

    public async Task<DiagnosticResult> RunAsync(CancellationToken ct = default)
    {
        var platform = await _detector.DetectPlatformAsync(ct);
        if (platform is null)
        {
            return new DiagnosticResult
            {
                CheckName = Name,
                Status = DiagnosticStatus.Fail,
                Reason = "Google Play Games for PC was not found.",
                RecommendedFix = "Install Google Play Games from Google's website, or set its install folder manually in Settings.",
            };
        }
        return new DiagnosticResult
        {
            CheckName = Name,
            Status = platform.ProtocolHandlerRegistered ? DiagnosticStatus.Pass : DiagnosticStatus.Warning,
            Reason = $"Version {platform.Version} at {platform.InstallDirectory}"
                + (platform.ServiceRunning ? " (service running)." : " (service not running)."),
            RecommendedFix = platform.ProtocolHandlerRegistered
                ? string.Empty
                : "The googleplaygames:// protocol is not registered — open Google Play Games once to repair it.",
            Details = $"Bootstrapper: {platform.BootstrapperPath}\nClient: {platform.ClientPath}\nEmulator: {platform.EmulatorPath}",
        };
    }
}

public sealed class CriticalOpsCheck : IDiagnosticCheck
{
    private readonly IGameDetector _detector;
    public CriticalOpsCheck(IGameDetector detector) => _detector = detector;

    public string Name => "Critical Ops";
    public int Order => 40;

    public async Task<DiagnosticResult> RunAsync(CancellationToken ct = default)
    {
        var game = await _detector.DetectTargetGameAsync(ct);
        return game is null
            ? new DiagnosticResult
            {
                CheckName = Name,
                Status = DiagnosticStatus.Fail,
                Reason = "Critical Ops is not installed in Google Play Games.",
                RecommendedFix = "Open Google Play Games and install Critical Ops.",
            }
            : new DiagnosticResult
            {
                CheckName = Name,
                Status = DiagnosticStatus.Pass,
                Reason = $"Installed ({game.PackageId}).",
                Details = $"Launch URI: {game.LaunchUri}\nShortcut: {game.ShortcutPath}",
            };
    }
}

public sealed class VirtualDriverCheck : IDiagnosticCheck
{
    private readonly IVirtualDisplayProvider _provider;
    public VirtualDriverCheck(IVirtualDisplayProvider provider) => _provider = provider;

    public string Name => "Virtual Display Driver";
    public int Order => 50;

    public async Task<DiagnosticResult> RunAsync(CancellationToken ct = default)
    {
        var available = await _provider.IsAvailableAsync(ct);
        if (!available)
        {
            return new DiagnosticResult
            {
                CheckName = Name,
                Status = DiagnosticStatus.Warning,
                Reason = "No virtual display driver was detected — the mock provider will be used.",
                RecommendedFix = "Install a virtual display driver (e.g. the IddCx Virtual Display Driver) to unlock high-refresh virtual displays.",
            };
        }

        var active = await _provider.IsDisplayActiveAsync(ct);
        var capabilities = await _provider.GetCapabilitiesAsync(ct);
        return new DiagnosticResult
        {
            CheckName = Name,
            Status = DiagnosticStatus.Pass,
            Reason = $"{_provider.Name} detected" + (active ? " (display active)." : " (display currently off)."),
            Details = $"Custom modes: {capabilities.SupportsCustomModes}, GPU pinning: {capabilities.SupportsGpuPinning}, needs admin: {capabilities.RequiresElevation}",
        };
    }
}

public sealed class RefreshRateCheck : IDiagnosticCheck
{
    private readonly IDisplayService _displayService;
    public RefreshRateCheck(IDisplayService displayService) => _displayService = displayService;

    public string Name => "Refresh Rate";
    public int Order => 60;

    public async Task<DiagnosticResult> RunAsync(CancellationToken ct = default)
    {
        var displays = await _displayService.GetDisplaysAsync(ct);
        var best = displays.Where(d => d.IsActive).Select(d => d.CurrentMode.RefreshRate).DefaultIfEmpty(0).Max();
        return new DiagnosticResult
        {
            CheckName = Name,
            Status = best >= 120 ? DiagnosticStatus.Pass : DiagnosticStatus.Warning,
            Reason = best > 0 ? $"Fastest active display runs at {best} Hz." : "No active display detected.",
            RecommendedFix = best >= 120
                ? string.Empty
                : "Use a Competitive profile with the virtual display to run the game at a high refresh rate.",
        };
    }
}

public sealed class GpuDriverCheck : IDiagnosticCheck
{
    private readonly ISystemInfoService _systemInfo;
    public GpuDriverCheck(ISystemInfoService systemInfo) => _systemInfo = systemInfo;

    public string Name => "GPU Driver";
    public int Order => 70;

    public async Task<DiagnosticResult> RunAsync(CancellationToken ct = default)
    {
        var inventory = await _systemInfo.GetInventoryAsync(ct);
        var realGpus = inventory.Gpus.Where(g => !g.Name.Contains("Virtual", StringComparison.OrdinalIgnoreCase)).ToList();
        if (realGpus.Count == 0)
        {
            return new DiagnosticResult
            {
                CheckName = Name,
                Status = DiagnosticStatus.Fail,
                Reason = "No GPU was detected.",
                RecommendedFix = "Check Device Manager and reinstall the graphics driver.",
            };
        }
        return new DiagnosticResult
        {
            CheckName = Name,
            Status = DiagnosticStatus.Pass,
            Reason = string.Join("; ", realGpus.Select(g => $"{g.Name} (driver {g.DriverVersion})")),
        };
    }
}

public sealed class DiskSpaceCheck : IDiagnosticCheck
{
    public string Name => "Disk Space";
    public int Order => 80;

    public Task<DiagnosticResult> RunAsync(CancellationToken ct = default)
    {
        var systemDrive = Path.GetPathRoot(Environment.GetFolderPath(Environment.SpecialFolder.Windows)) ?? "C:\\";
        var drive = new DriveInfo(systemDrive);
        var freeGb = drive.AvailableFreeSpace / (1024.0 * 1024 * 1024);
        return Task.FromResult(new DiagnosticResult
        {
            CheckName = Name,
            Status = freeGb >= 10 ? DiagnosticStatus.Pass : freeGb >= 3 ? DiagnosticStatus.Warning : DiagnosticStatus.Fail,
            Reason = $"{freeGb:F1} GB free on {drive.Name}",
            RecommendedFix = freeGb >= 10 ? string.Empty : "Free up disk space — game updates and the emulator image need room.",
        });
    }
}

public sealed class AdminPermissionsCheck : IDiagnosticCheck
{
    private readonly IElevationBroker _elevation;
    public AdminPermissionsCheck(IElevationBroker elevation) => _elevation = elevation;

    public string Name => "Administrator Permissions";
    public int Order => 90;

    public Task<DiagnosticResult> RunAsync(CancellationToken ct = default)
    {
        var helperPath = Path.Combine(AppContext.BaseDirectory, "Optima.Elevated.exe");
        var helperPresent = File.Exists(helperPath);
        var status = helperPresent ? DiagnosticStatus.Pass : DiagnosticStatus.Warning;
        var reason = _elevation.CurrentProcessIsElevated
            ? "The app is running elevated (works, but not required)."
            : helperPresent
                ? "Running non-elevated with the elevated helper available (recommended setup)."
                : "The elevated helper executable is missing — device toggling and frametime capture will be unavailable.";
        return Task.FromResult(new DiagnosticResult
        {
            CheckName = Name,
            Status = status,
            Reason = reason,
            RecommendedFix = helperPresent ? string.Empty : "Reinstall or rebuild the application so Optima.Elevated.exe sits next to the main executable.",
        });
    }
}
