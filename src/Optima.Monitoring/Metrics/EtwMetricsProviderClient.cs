using System.Globalization;
using Optima.Core.Abstractions;
using Optima.Core.Configuration;
using Optima.Core.Ipc;
using Optima.Core.Models;
using Optima.Core.Statistics;
using Microsoft.Extensions.Logging;

namespace Optima.Monitoring.Metrics;

/// <summary>FPS / frametime provider (§12-13) backed by the elevated helper's ETW present trace.</summary>
public sealed class EtwMetricsProviderClient : IPerformanceMetricsProvider
{
    private readonly IElevationBroker _elevation;
    private readonly SettingsService _settings;
    private readonly ILogger<EtwMetricsProviderClient> _logger;
    private readonly object _lock = new();

    private readonly List<double> _liveFpsSamples = [];
    private readonly List<double> _liveFrametimes = [];
    private SessionStats? _finalStats;
    private IReadOnlyList<double>? _finalFpsSamples;
    private bool _running;

    public EtwMetricsProviderClient(IElevationBroker elevation, SettingsService settings, ILogger<EtwMetricsProviderClient> logger)
    {
        _elevation = elevation;
        _settings = settings;
        _logger = logger;
        _elevation.EventReceived += OnHelperEvent;
    }

    public string Name => "ETW present statistics";

    public event EventHandler<(double Fps, double FrametimeMs)>? SampleArrived;

    public async Task<bool> IsAvailableAsync(CancellationToken ct = default)
        => (await _settings.GetSettingsAsync(ct).ConfigureAwait(false)).EnableFrametimeCapture;

    public async Task StartAsync(IReadOnlyList<int> processIds, CancellationToken ct = default)
    {
        if (processIds.Count == 0)
        {
            throw new ArgumentException("At least one process id is required.", nameof(processIds));
        }
        if (!await _elevation.EnsureStartedAsync(ct).ConfigureAwait(false))
        {
            throw new InvalidOperationException("The elevated helper is required for frametime capture and was not started.");
        }

        var response = await _elevation.SendAsync(new IpcRequest
        {
            Command = IpcCommand.StartEtw,
            Args = { ["pids"] = string.Join(',', processIds.Select(p => p.ToString(CultureInfo.InvariantCulture))) },
        }, ct).ConfigureAwait(false);

        if (!response.Success)
        {
            throw new InvalidOperationException($"Frametime capture could not start: {response.Error}");
        }

        lock (_lock)
        {
            _running = true;
            _finalStats = null;
            _finalFpsSamples = null;
            _liveFpsSamples.Clear();
            _liveFrametimes.Clear();
        }
        _logger.LogInformation("Frametime capture started for candidate PIDs {Pids}", string.Join(", ", processIds));
    }

    public async Task StopAsync()
    {
        lock (_lock)
        {
            if (!_running)
            {
                return;
            }
            _running = false;
        }

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var response = await _elevation.SendAsync(new IpcRequest { Command = IpcCommand.StopEtw }, cts.Token).ConfigureAwait(false);
        if (!response.Success)
        {
            _logger.LogWarning("Frametime capture stop reported: {Error}", response.Error);
            return;
        }

        lock (_lock)
        {
            _finalStats = new SessionStats
            {
                SampleCount = ParseInt(response.Data, "sampleCount"),
                AverageFps = ParseDouble(response.Data, "averageFps"),
                OnePercentLowFps = ParseDouble(response.Data, "onePercentLowFps"),
                PointOnePercentLowFps = ParseDouble(response.Data, "pointOnePercentLowFps"),
                AverageFrametimeMs = ParseDouble(response.Data, "averageFrametimeMs"),
                P95FrametimeMs = ParseDouble(response.Data, "p95FrametimeMs"),
                P99FrametimeMs = ParseDouble(response.Data, "p99FrametimeMs"),
            };
            _finalFpsSamples = response.Data.TryGetValue("fpsSamples", out var joined) && joined.Length > 0
                ? joined.Split(',').Select(s => double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : 0).ToList()
                : [.. _liveFpsSamples];
        }
        _logger.LogInformation("Frametime capture stopped ({Samples} frames)", _finalStats?.SampleCount);
    }

    public SessionStats GetSessionStats()
    {
        lock (_lock)
        {
            if (_finalStats is not null)
            {
                return _finalStats;
            }
            return FrametimeStatistics.Compute(_liveFrametimes);
        }
    }

    public IReadOnlyList<double> GetFpsSamples()
    {
        lock (_lock)
        {
            return _finalFpsSamples ?? [.. _liveFpsSamples];
        }
    }

    private void OnHelperEvent(object? sender, IpcEvent evt)
    {
        if (evt.Kind != "etwSample")
        {
            return;
        }

        var fps = ParseDouble(evt.Data, "fps");
        var frametime = ParseDouble(evt.Data, "frametimeMs");
        lock (_lock)
        {
            if (!_running)
            {
                return;
            }
            _liveFpsSamples.Add(fps);
            _liveFrametimes.Add(frametime);
        }
        SampleArrived?.Invoke(this, (fps, frametime));
    }

    private static double ParseDouble(Dictionary<string, string> data, string key)
        => data.TryGetValue(key, out var text)
            && double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) ? value : 0;

    private static int ParseInt(Dictionary<string, string> data, string key)
        => data.TryGetValue(key, out var text)
            && int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : 0;

    public ValueTask DisposeAsync()
    {
        _elevation.EventReceived -= OnHelperEvent;
        return ValueTask.CompletedTask;
    }
}
