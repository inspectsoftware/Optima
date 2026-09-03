using Optima.Core.Models;

namespace Optima.Core.Abstractions;

/// <summary>Windows-side display control (mode changes are temporary, never written to the registry).</summary>
public interface IDisplayService
{
    Task<IReadOnlyList<DisplayInfo>> GetDisplaysAsync(CancellationToken ct = default);

    Task<IReadOnlyList<DisplayMode>> GetSupportedModesAsync(string deviceName, CancellationToken ct = default);

    Task ApplyModeAsync(string deviceName, DisplayMode mode, CancellationToken ct = default);

    Task MakePrimaryAsync(string deviceName, CancellationToken ct = default);

    Task<string> CaptureTopologyAsync(CancellationToken ct = default);

    Task RestoreTopologyAsync(string topology, CancellationToken ct = default);
}
