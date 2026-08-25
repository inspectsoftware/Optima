using Optima.Core.Abstractions;
using Optima.Core.Models;

namespace Optima.Driver.Providers;

/// <summary>Shared plumbing for virtual display providers (§6): mode composition + default routing.</summary>
public abstract class VirtualDisplayProviderBase : IVirtualDisplayProvider
{
    public abstract string Name { get; }

    public abstract Task<bool> IsAvailableAsync(CancellationToken ct = default);

    public abstract Task<DriverCapabilities> GetCapabilitiesAsync(CancellationToken ct = default);

    public abstract Task InitializeAsync(CancellationToken ct = default);

    public abstract Task CreateDisplayAsync(CancellationToken ct = default);

    public abstract Task EnableDisplayAsync(CancellationToken ct = default);

    public abstract Task DisableDisplayAsync(CancellationToken ct = default);

    public abstract Task SetModeAsync(DisplayMode mode, CancellationToken ct = default);

    public abstract Task<IReadOnlyList<DisplayMode>> GetSupportedModesAsync(CancellationToken ct = default);

    public abstract Task<DisplayMode?> GetCurrentModeAsync(CancellationToken ct = default);

    public abstract Task<bool> IsDisplayActiveAsync(CancellationToken ct = default);

    public abstract Task<DisplayInfo?> GetDisplayInfoAsync(CancellationToken ct = default);

    public abstract Task RestoreOriginalStateAsync(CancellationToken ct = default);

    // Width/height/refresh-only changes are compositions over SetModeAsync by default.

    public async Task SetResolutionAsync(int width, int height, CancellationToken ct = default)
    {
        var current = await GetCurrentModeAsync(ct).ConfigureAwait(false);
        var refresh = current is { RefreshRate: > 0 } mode ? mode.RefreshRate : 60;
        await SetModeAsync(new DisplayMode(width, height, refresh), ct).ConfigureAwait(false);
    }

    public async Task SetRefreshRateAsync(int refreshRate, CancellationToken ct = default)
    {
        var current = await GetCurrentModeAsync(ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException("No current mode. Enable the virtual display first.");
        await SetModeAsync(current with { RefreshRate = refreshRate }, ct).ConfigureAwait(false);
    }
}
