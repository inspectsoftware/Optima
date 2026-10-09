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

    /// <summary>
    /// Starts the elevated helper when capture is going to want it. Called at the click that starts
    /// a session, which is where the administrator prompt belongs: capture itself only starts once
    /// the game is on screen, and a prompt raised then lands on top of the game. Never throws,
    /// because a session starts just as well without capture. Runs on the pool; the returned task
    /// is also what <see cref="StartAsync"/> waits for, so capture is decided by the answer to
    /// this prompt and not by the one before it.
    /// </summary>
    public Task EnsureHelperAsync() => _helperStart = Task.Run(EnsureHelperCoreAsync);

    private volatile Task _helperStart = Task.CompletedTask;

    private async Task EnsureHelperCoreAsync()
    {
        try
        {
            var settings = await _settings.GetSettingsAsync().ConfigureAwait(false);
            if (settings.EnableFrametimeCapture && !settings.UseMockMetricsProvider && !_elevation.IsConnected)
            {
                await _elevation.EnsureStartedAsync().ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "The elevated helper could not be started ahead of the session");
        }
    }

    public async Task StartAsync(IReadOnlyList<int> processIds, CancellationToken ct = default)
    {
        if (processIds.Count == 0)
        {
            throw new ArgumentException("At least one process id is required.", nameof(processIds));
        }
        // The click that started the session already asked, and may still be waiting for its
        // answer. A no there is not answered by asking again now, over the game; the session runs
        // without capture.
        await _helperStart.WaitAsync(ct).ConfigureAwait(false);
        if (!_elevation.IsConnected && _elevation.LastStartFailure == ElevationStartFailure.Declined)
        {
            throw new InvalidOperationException("The administrator prompt was declined, so frametime capture is off for this session.");
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
        IpcResponse response;
        try
        {
            response = await _elevation.SendAsync(new IpcRequest { Command = IpcCommand.StopEtw }, cts.Token).ConfigureAwait(false);
        }
        catch
        {
            // The helper may still be capturing: stay "running" so the next stop asks again.
            lock (_lock)
            {
                _running = true;
            }
            throw;
        }
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
