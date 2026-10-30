using Optima.Core.Models;

namespace Optima.Core.Abstractions;

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

/// <summary>Abstraction over a virtual display driver (§6).</summary>
public interface IVirtualDisplayProvider
{
    string Name { get; }

    Task<bool> IsAvailableAsync(CancellationToken ct = default);

    Task<DriverCapabilities> GetCapabilitiesAsync(CancellationToken ct = default);

    Task InitializeAsync(CancellationToken ct = default);

    Task CreateDisplayAsync(CancellationToken ct = default);

    Task EnableDisplayAsync(CancellationToken ct = default);

    Task DisableDisplayAsync(CancellationToken ct = default);

    Task SetResolutionAsync(int width, int height, CancellationToken ct = default);

    Task SetRefreshRateAsync(int refreshRate, CancellationToken ct = default);

    Task SetModeAsync(DisplayMode mode, CancellationToken ct = default);

    Task<IReadOnlyList<DisplayMode>> GetSupportedModesAsync(CancellationToken ct = default);

    Task<DisplayMode?> GetCurrentModeAsync(CancellationToken ct = default);

    Task<bool> IsDisplayActiveAsync(CancellationToken ct = default);

    Task<DisplayInfo?> GetDisplayInfoAsync(CancellationToken ct = default);

    Task RestoreOriginalStateAsync(CancellationToken ct = default);

    /// <summary>
    /// Whether the driver's device was already switched on when <see cref="InitializeAsync"/> ran.
    /// A session only records that it switched the device on when it was off: a device that was on
    /// with its display merely off the desktop, or one Windows has no driver for, must not be switched
    /// off by the recovery at the end. False when the provider has no device to switch.
    /// </summary>
    bool DeviceWasEnabledInitially => false;
}
