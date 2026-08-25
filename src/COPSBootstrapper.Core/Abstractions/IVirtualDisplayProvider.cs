using COPSBootstrapper.Core.Models;

namespace COPSBootstrapper.Core.Abstractions;

/// <summary>Capabilities a virtual display driver provider exposes (§6).</summary>
public sealed record DriverCapabilities
{
    public bool SupportsCustomModes { get; init; }
    public bool SupportsRefreshRateChange { get; init; }
    public bool SupportsGpuPinning { get; init; }
    public bool SupportsEnableDisable { get; init; }
    public bool RequiresElevation { get; init; }
    public int MaxDisplays { get; init; } = 1;
}

/// <summary>Abstraction over a virtual display driver (§6). Implementations must be fully reversible.</summary>
public interface IVirtualDisplayProvider
{
    string Name { get; }

    /// <summary>Probes whether the underlying driver is present and controllable on this machine.</summary>
    Task<bool> IsAvailableAsync(CancellationToken ct = default);

    Task<DriverCapabilities> GetCapabilitiesAsync(CancellationToken ct = default);

    Task InitializeAsync(CancellationToken ct = default);

    /// <summary>Ensures the virtual display exists and is attached to the desktop.</summary>
    Task CreateDisplayAsync(CancellationToken ct = default);

    Task EnableDisplayAsync(CancellationToken ct = default);

    Task DisableDisplayAsync(CancellationToken ct = default);

    Task SetResolutionAsync(int width, int height, CancellationToken ct = default);

    Task SetRefreshRateAsync(int refreshRate, CancellationToken ct = default);

    /// <summary>Convenience: width+height+refresh in one transaction where the driver supports it.</summary>
    Task SetModeAsync(DisplayMode mode, CancellationToken ct = default);

    Task<IReadOnlyList<DisplayMode>> GetSupportedModesAsync(CancellationToken ct = default);

    Task<DisplayMode?> GetCurrentModeAsync(CancellationToken ct = default);

    /// <summary>True when the virtual display is currently attached to the desktop.</summary>
    Task<bool> IsDisplayActiveAsync(CancellationToken ct = default);

    /// <summary>The DisplayInfo of the virtual display when active (for mode application / targeting).</summary>
    Task<DisplayInfo?> GetDisplayInfoAsync(CancellationToken ct = default);

    Task RestoreOriginalStateAsync(CancellationToken ct = default);
}
