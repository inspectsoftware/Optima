using Optima.Core.Models;

namespace Optima.Core.Abstractions;

/// <summary>Live hardware utilization feed for the dashboard (§12).</summary>
public interface IPerformanceMonitor : IAsyncDisposable
{
    /// <summary>Latest metrics tick; null until the first sample completes.</summary>
    HardwareMetrics? Latest { get; }

    event EventHandler<HardwareMetrics>? MetricsUpdated;

    /// <summary>Process ids whose CPU/RAM should be aggregated as "game" usage.</summary>
    void SetGameProcessIds(IReadOnlyList<int> processIds);

    Task StartAsync(CancellationToken ct = default);

    Task StopAsync();
}

/// <summary>
/// External FPS / frametime source (§12-13). Never injects into the game;
/// implementations use ETW present statistics or stay mocked.
/// </summary>
public interface IPerformanceMetricsProvider : IAsyncDisposable
{
    string Name { get; }

    Task<bool> IsAvailableAsync(CancellationToken ct = default);

    /// <summary>Begins collecting present statistics for the given process id.</summary>
    Task StartAsync(int processId, CancellationToken ct = default);

    Task StopAsync();

    /// <summary>Raised roughly once per second with (fps, frametimeMs) of the last window.</summary>
    event EventHandler<(double Fps, double FrametimeMs)>? SampleArrived;

    /// <summary>Aggregate statistics accumulated since StartAsync.</summary>
    SessionStats GetSessionStats();

    /// <summary>Per-second FPS samples collected so far (for benchmark comparison).</summary>
    IReadOnlyList<double> GetFpsSamples();
}
