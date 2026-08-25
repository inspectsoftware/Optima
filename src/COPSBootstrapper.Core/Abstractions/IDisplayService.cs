using COPSBootstrapper.Core.Models;

namespace COPSBootstrapper.Core.Abstractions;

/// <summary>Windows-side display control (mode changes are temporary — never written to the registry).</summary>
public interface IDisplayService
{
    Task<IReadOnlyList<DisplayInfo>> GetDisplaysAsync(CancellationToken ct = default);

    Task<IReadOnlyList<DisplayMode>> GetSupportedModesAsync(string deviceName, CancellationToken ct = default);

    /// <summary>Applies a mode to one display. Throws <see cref="CopsException"/> with a friendly error on failure.</summary>
    Task ApplyModeAsync(string deviceName, DisplayMode mode, CancellationToken ct = default);

    /// <summary>Makes the given display the primary display (temporary; topology restore reverts it).</summary>
    Task MakePrimaryAsync(string deviceName, CancellationToken ct = default);

    /// <summary>Serializes the full current display topology (positions, modes, primary) to an opaque string.</summary>
    Task<string> CaptureTopologyAsync(CancellationToken ct = default);

    /// <summary>Restores a topology captured earlier. Safe to call repeatedly.</summary>
    Task RestoreTopologyAsync(string topology, CancellationToken ct = default);
}
