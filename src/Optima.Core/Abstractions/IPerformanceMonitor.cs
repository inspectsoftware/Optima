using Optima.Core.Models;

namespace Optima.Core.Abstractions;

/// <summary>Live hardware utilization feed for the dashboard (§12).</summary>
public interface IPerformanceMonitor : IAsyncDisposable
{
    HardwareMetrics? Latest { get; }

    event EventHandler<HardwareMetrics>? MetricsUpdated;

    void SetGameProcessIds(IReadOnlyList<int> processIds);

    Task StartAsync(CancellationToken ct = default);

    Task StopAsync();
}

/// <summary>External FPS / frametime source (§12-13).</summary>
public interface IPerformanceMetricsProvider : IAsyncDisposable
{
    string Name { get; }

    Task<bool> IsAvailableAsync(CancellationToken ct = default);

    Task StartAsync(IReadOnlyList<int> processIds, CancellationToken ct = default);

    Task StopAsync();

    event EventHandler<(double Fps, double FrametimeMs)>? SampleArrived;

    SessionStats GetSessionStats();

    IReadOnlyList<double> GetFpsSamples();
}
