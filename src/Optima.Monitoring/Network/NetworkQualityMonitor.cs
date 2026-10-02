using System.Net;
using System.Net.NetworkInformation;
using Optima.Core.Abstractions;
using Optima.Core.Configuration;
using Optima.Core.Models;
using Optima.Core.Statistics;
using Microsoft.Extensions.Logging;

namespace Optima.Monitoring.Network;

/// <summary>Passive ping loop (§ security boundaries: observation only).</summary>
public sealed class NetworkQualityMonitor : INetworkQualityMonitor
{
    private static readonly TimeSpan PingInterval = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan RediscoverInterval = TimeSpan.FromSeconds(60);
    private const int PingTimeoutMs = 1000;
    private const int MaxTargets = 3;

    /// <summary>Ceiling on the candidates probed in one discovery sweep, so a long TCP table cannot sweep for seconds.</summary>
    private const int MaxCandidates = 12;

    private readonly IRemoteEndpointSource _endpoints;
    private readonly SettingsService _settings;
    private readonly ILogger<NetworkQualityMonitor> _logger;
    private readonly object _lock = new();

    private NetworkQualityCalculator _calculator = new();
    private CancellationTokenSource? _cts;
    private Task? _loop;
    private IReadOnlyList<int> _processIds = [];

    public NetworkQualityMonitor(
        IRemoteEndpointSource endpoints,
        SettingsService settings,
        ILogger<NetworkQualityMonitor> logger)
    {
        _endpoints = endpoints;
        _settings = settings;
        _logger = logger;
    }

    public NetworkQualitySample? Latest { get; private set; }

    /// <summary>Last sample handed to subscribers; a ping that has not moved is not worth a repaint.</summary>
    private NetworkQualitySample? _published;

    public event EventHandler<NetworkQualitySample>? SampleArrived;

    public Task StartAsync(IReadOnlyList<int> processIds, CancellationToken ct = default)
    {
        if (_loop is not null)
        {
            return Task.CompletedTask;
        }
        lock (_lock)
        {
            _calculator = new NetworkQualityCalculator();
        }
        _processIds = processIds;
        _cts = new CancellationTokenSource();
        _loop = Task.Run(() => RunAsync(_cts.Token), CancellationToken.None);
        return Task.CompletedTask;
    }

    public async Task<NetworkQualityStats?> StopAsync()
    {
        var loop = _loop;
        if (loop is null)
        {
            return null;
        }
        _cts?.Cancel();
        try
        {
            await loop.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        _cts?.Dispose();
        _cts = null;
        _loop = null;
        Latest = null;

        lock (_lock)
        {
            var aggregate = _calculator.SessionAggregate;
            return aggregate.HasData ? aggregate : null;
        }
    }

    private async Task RunAsync(CancellationToken ct)
    {
        var targets = new List<IPAddress>();
        var referenceMode = false;
        var lastDiscovery = DateTimeOffset.MinValue;
        var targetIndex = 0;
        using var ping = new Ping();

        while (!ct.IsCancellationRequested)
        {
            try
            {
                if (DateTimeOffset.Now - lastDiscovery > RediscoverInterval)
                {
                    (targets, referenceMode) = await DiscoverTargetsAsync(ct).ConfigureAwait(false);
                    lastDiscovery = DateTimeOffset.Now;
                }

                if (targets.Count > 0)
                {
                    var target = targets[targetIndex % targets.Count];
                    targetIndex++;

                    double? rtt = null;
                    try
                    {
                        var reply = await ping.SendPingAsync(target, PingTimeoutMs).ConfigureAwait(false);
                        if (reply.Status == IPStatus.Success)
                        {
                            rtt = reply.RoundtripTime;
                        }
                    }
                    catch (PingException)
                    {
                    }

                    NetworkQualitySample sample;
                    lock (_lock)
                    {
                        _calculator.AddResult(rtt);
                        sample = new NetworkQualitySample
                        {
                            PingMs = _calculator.AveragePingMs,
                            JitterMs = _calculator.JitterMs,
                            PacketLossPct = _calculator.PacketLossPct,
                            Target = target.ToString(),
                            IsReferenceHost = referenceMode,
                        };
                    }
                    Latest = sample;

                    // Subscribers post to the UI thread for every sample, and a steady connection
                    // reports the same numbers second after second; only a real change is published.
                    if (HasChanged(sample))
                    {
                        SampleArrived?.Invoke(this, sample);
                    }
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogDebug(ex, "Network quality tick failed");
            }

            await Task.Delay(PingInterval, ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// True when a sample differs from the last one in a way worth showing. The rounded values are
    /// compared, so sub-millisecond noise on a stable connection stays quiet.
    /// </summary>
    private bool HasChanged(NetworkQualitySample sample)
    {
        var previous = _published;
        _published = sample;
        return previous is null
            || Math.Abs(previous.PingMs - sample.PingMs) >= 1
            || Math.Abs(previous.JitterMs - sample.JitterMs) >= 1
            || Math.Abs(previous.PacketLossPct - sample.PacketLossPct) >= 1
            || previous.IsReferenceHost != sample.IsReferenceHost
            || !string.Equals(previous.Target, sample.Target, StringComparison.Ordinal);
    }

    private async Task<(List<IPAddress> Targets, bool ReferenceMode)> DiscoverTargetsAsync(CancellationToken ct)
    {
        var responders = new List<IPAddress>();
        try
        {
            var candidates = await _endpoints.GetRemoteEndpointsAsync(_processIds, ct).ConfigureAwait(false);
            responders = await ProbeCandidatesAsync(candidates, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "Endpoint discovery failed");
        }

        if (responders.Count > 0)
        {
            _logger.LogInformation("Measuring game endpoints: {Targets}", string.Join(", ", responders));
            return (responders, false);
        }

        var host = (await _settings.GetSettingsAsync(ct).ConfigureAwait(false)).NetworkReferenceHost;
        if (IPAddress.TryParse(host, out var reference))
        {
            return ([reference], true);
        }
        try
        {
            var resolved = await Dns.GetHostAddressesAsync(host, ct).ConfigureAwait(false);
            var usable = resolved.FirstOrDefault(a => a.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork);
            if (usable is not null)
            {
                return ([usable], true);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Reference host {Host} could not be resolved", host);
        }
        return ([], true);
    }

    /// <summary>
    /// Probes the candidates at once and keeps the first responders in candidate order. Sequentially
    /// this cost the full timeout per silent candidate — a filtered or stale endpoint list meant
    /// seconds before measurement started. Each probe needs its own Ping instance (one cannot have
    /// several requests in flight), which is why the probe owns one and disposes it.
    /// </summary>
    private static async Task<List<IPAddress>> ProbeCandidatesAsync(IReadOnlyList<IPAddress> candidates, CancellationToken ct)
    {
        var probes = candidates
            .Take(MaxCandidates)
            .Select(candidate => ProbeAsync(candidate, ct))
            .ToList();

        var answered = await Task.WhenAll(probes).ConfigureAwait(false);
        return answered
            .Where(target => target is not null)
            .Select(target => target!)
            .Take(MaxTargets)
            .ToList();
    }

    private static async Task<IPAddress?> ProbeAsync(IPAddress candidate, CancellationToken ct)
    {
        try
        {
            using var ping = new Ping();
            var reply = await ping.SendPingAsync(candidate, PingTimeoutMs).ConfigureAwait(false);
            return reply.Status == IPStatus.Success ? candidate : null;
        }
        catch (Exception ex) when (ex is PingException or InvalidOperationException)
        {
            return null;
        }
    }

    public async ValueTask DisposeAsync() => await StopAsync().ConfigureAwait(false);
}
