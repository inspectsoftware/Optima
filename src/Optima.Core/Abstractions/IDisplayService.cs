using Optima.Core.Models;

namespace Optima.Core.Abstractions;

/// <summary>Windows-side display control (mode changes are temporary, never written to the registry).</summary>
public interface IDisplayService
{
    Task<IReadOnlyList<DisplayInfo>> GetDisplaysAsync(CancellationToken ct = default);

    Task<IReadOnlyList<DisplayMode>> GetSupportedModesAsync(string deviceName, CancellationToken ct = default);

    Task ApplyModeAsync(string deviceName, DisplayMode mode, CancellationToken ct = default);

    Task MakePrimaryAsync(string deviceName, CancellationToken ct = default);

    /// <summary>
    /// Adds a display that is present but switched off in Windows (a freshly enabled virtual display often
    /// is) to the desktop: extended, to the right of the current desktop, never the main screen, not saved.
    /// True when a display whose adapter name contains <paramref name="adapterNameContains"/> is on the
    /// desktop afterwards (also when it already was); false when there is none or Windows did not take it.
    /// </summary>
    Task<bool> AttachToDesktopAsync(string adapterNameContains, CancellationToken ct = default);

    Task<string> CaptureTopologyAsync(CancellationToken ct = default);

    Task RestoreTopologyAsync(string topology, CancellationToken ct = default);
}
