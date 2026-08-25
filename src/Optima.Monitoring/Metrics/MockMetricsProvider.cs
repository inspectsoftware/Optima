using Optima.Core.Abstractions;
using Optima.Core.Models;
using Optima.Core.Statistics;

namespace Optima.Monitoring.Metrics;

/// <summary>
/// Deterministic synthetic FPS source used in tests and developer mode: a base FPS with mild
/// noise and occasional stutter spikes so percentile math has something realistic to chew on.
/// </summary>
public sealed class MockMetricsProvider : IPerformanceMetricsProvider
{
    private readonly object _lock = new();
    private readonly List<double> _frametimes = [];
    private readonly List<double> _fpsSamples = [];
    private Timer? _timer;
    private readonly Random _random;

    public MockMetricsProvider(int seed = 1337)
    {
        _random = new Random(seed);
    }

    public double BaseFps { get; set; } = 180;

    public string Name => "Mock metrics";

    public event EventHandler<(double Fps, double FrametimeMs)>? SampleArrived;

    public Task<bool> IsAvailableAsync(CancellationToken ct = default) => Task.FromResult(true);

    public Task StartAsync(int processId, CancellationToken ct = default)
    {
        lock (_lock)
        {
            _frametimes.Clear();
            _fpsSamples.Clear();
        }
        _timer = new Timer(Tick, null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
        return Task.CompletedTask;
    }

    public Task StopAsync()
    {
        _timer?.Dispose();
        _timer = null;
        return Task.CompletedTask;
    }

    private void Tick(object? state)
    {
        double fps;
        double avgFrametime;
        lock (_lock)
        {
            // One second of synthetic frames.
            var frames = new List<double>();
            var produced = 0.0;
            while (produced < 1000)
            {
                var target = 1000.0 / BaseFps;
                var jitter = target * (0.9 + _random.NextDouble() * 0.2);
                if (_random.NextDouble() < 0.01)
                {
                    jitter *= 3; // stutter spike
                }
                frames.Add(jitter);
                produced += jitter;
            }
            _frametimes.AddRange(frames);
            fps = frames.Count;
            avgFrametime = frames.Average();
            _fpsSamples.Add(fps);
        }
        SampleArrived?.Invoke(this, (fps, avgFrametime));
    }

    public SessionStats GetSessionStats()
    {
        lock (_lock)
        {
            return FrametimeStatistics.Compute(_frametimes);
        }
    }

    public IReadOnlyList<double> GetFpsSamples()
    {
        lock (_lock)
        {
            return [.. _fpsSamples];
        }
    }

    public ValueTask DisposeAsync()
    {
        _timer?.Dispose();
        return ValueTask.CompletedTask;
    }
}
