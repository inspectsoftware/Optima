using System.Globalization;
using Optima.Core.Abstractions;
using Optima.Core.Ipc;
using Microsoft.Extensions.Logging;

namespace Optima.Monitoring.Metrics;

/// <summary>One CPU/GPU thermal+load sample from the elevated helper.</summary>
public sealed record HardwareSample(double? CpuTempC, double? GpuTempC, double? CpuLoadPct, double? GpuLoadPct);

/// <summary>
/// Client side of the helper's hardware stream (LibreHardwareMonitor lives in the
/// elevated process because sensor access needs its kernel driver). Starting the
/// stream can therefore raise a UAC prompt; callers say so in their UI.
/// </summary>
public sealed class HardwareStreamClient
{
    private readonly IElevationBroker _broker;
    private readonly ILogger<HardwareStreamClient> _logger;
    private bool _subscribed;

    public HardwareStreamClient(IElevationBroker broker, ILogger<HardwareStreamClient> logger)
    {
        _broker = broker;
        _logger = logger;
    }

    public event Action<HardwareSample>? SampleReceived;

    public bool IsStreaming { get; private set; }

    /// <summary>Starts the stream; false when the user declined elevation or the helper failed.</summary>
    public async Task<bool> StartAsync(CancellationToken ct = default)
    {
        if (!await _broker.EnsureStartedAsync(ct).ConfigureAwait(false))
        {
            return false;
        }
        if (!_subscribed)
        {
            _broker.EventReceived += OnEvent;
            _subscribed = true;
        }
        var response = await _broker.SendAsync(new IpcRequest { Command = IpcCommand.StartHardwareStream }, ct).ConfigureAwait(false);
        IsStreaming = response.Success;
        if (!response.Success)
        {
            _logger.LogWarning("Hardware stream failed to start: {Error}", response.Error);
        }
        return response.Success;
    }

    public async Task StopAsync(CancellationToken ct = default)
    {
        IsStreaming = false;
        if (_broker.IsConnected)
        {
            await _broker.SendAsync(new IpcRequest { Command = IpcCommand.StopHardwareStream }, ct).ConfigureAwait(false);
        }
    }

    private void OnEvent(object? sender, IpcEvent ipcEvent)
    {
        if (ipcEvent.Kind != "hardwareSample")
        {
            return;
        }
        SampleReceived?.Invoke(new HardwareSample(
            Parse(ipcEvent.Data.GetValueOrDefault("cpuTempC")),
            Parse(ipcEvent.Data.GetValueOrDefault("gpuTempC")),
            Parse(ipcEvent.Data.GetValueOrDefault("cpuLoadPct")),
            Parse(ipcEvent.Data.GetValueOrDefault("gpuLoadPct"))));

        static double? Parse(string? text)
            => double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : null;
    }
}
