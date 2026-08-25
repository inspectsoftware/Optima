using System.Diagnostics;
using Optima.Core.Abstractions;
using Optima.Core.Models;
using Optima.Monitoring.Nvidia;
using Optima.Platform.Windows.NativeMethods;
using Microsoft.Extensions.Logging;

namespace Optima.Monitoring;

/// <summary>
/// Live dashboard feed (§12): one sample per second. CPU comes from GetSystemTimes deltas,
/// RAM from GlobalMemoryStatusEx, GPU from NVML when present (RTX systems) with Windows
/// "GPU Engine" performance counters as fallback, and per-process usage for the game PIDs.
/// </summary>
public sealed class HardwareMonitor : IPerformanceMonitor
{
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(1);

    private readonly ILogger<HardwareMonitor> _logger;
    private readonly object _stateLock = new();

    private NvmlGpuReader? _nvml;
    private PerformanceCounter[]? _gpuEngineCounters;
    private PerformanceCounter? _cpuPerformanceCounter;
    private double _cpuBaseMhz;
    private CancellationTokenSource? _cts;
    private Task? _loop;

    private long _prevIdle, _prevKernel, _prevUser;
    private IReadOnlyList<int> _gameProcessIds = [];
    private readonly Dictionary<int, TimeSpan> _prevProcessCpu = [];
    private DateTimeOffset _prevProcessSample = DateTimeOffset.MinValue;

    public HardwareMonitor(ILogger<HardwareMonitor> logger)
    {
        _logger = logger;
    }

    public HardwareMetrics? Latest { get; private set; }

    public event EventHandler<HardwareMetrics>? MetricsUpdated;

    public void SetGameProcessIds(IReadOnlyList<int> processIds)
    {
        lock (_stateLock)
        {
            _gameProcessIds = processIds;
            _prevProcessCpu.Clear();
        }
    }

    public Task StartAsync(CancellationToken ct = default)
    {
        if (_loop is not null)
        {
            return Task.CompletedTask;
        }

        _nvml = new NvmlGpuReader();
        _logger.LogInformation("Hardware monitor starting (NVML available: {Nvml})", _nvml.IsAvailable);

        try
        {
            _cpuPerformanceCounter = new PerformanceCounter("Processor Information", "% Processor Performance", "_Total", readOnly: true);
            _ = _cpuPerformanceCounter.NextValue(); // warm-up; first read is always 0
        }
        catch (Exception ex) when (ex is InvalidOperationException or UnauthorizedAccessException)
        {
            _logger.LogDebug(ex, "CPU performance counter unavailable");
            _cpuPerformanceCounter = null;
        }

        _cpuBaseMhz = ReadCpuBaseMhz();
        ProcessNative.GetSystemTimes(out _prevIdle, out _prevKernel, out _prevUser);

        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _loop = Task.Run(() => SampleLoopAsync(_cts.Token), CancellationToken.None);
        return Task.CompletedTask;
    }

    public async Task StopAsync()
    {
        _cts?.Cancel();
        if (_loop is not null)
        {
            try
            {
                await _loop.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        }
        _loop = null;
        _cts?.Dispose();
        _cts = null;
    }

    private async Task SampleLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var metrics = Sample();
                Latest = metrics;
                MetricsUpdated?.Invoke(this, metrics);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "Hardware sampling tick failed");
            }
            await Task.Delay(Interval, ct).ConfigureAwait(false);
        }
    }

    private HardwareMetrics Sample()
    {
        // ---- CPU ----
        ProcessNative.GetSystemTimes(out var idle, out var kernel, out var user);
        var idleDelta = idle - _prevIdle;
        var busyDelta = (kernel - _prevKernel) + (user - _prevUser) - idleDelta; // kernel includes idle
        var totalDelta = (kernel - _prevKernel) + (user - _prevUser);
        (_prevIdle, _prevKernel, _prevUser) = (idle, kernel, user);
        var cpuPercent = totalDelta > 0 ? Math.Clamp(100.0 * busyDelta / totalDelta, 0, 100) : 0;

        double cpuMhz = 0;
        if (_cpuPerformanceCounter is not null && _cpuBaseMhz > 0)
        {
            try
            {
                cpuMhz = _cpuBaseMhz * _cpuPerformanceCounter.NextValue() / 100.0;
            }
            catch (InvalidOperationException)
            {
                cpuMhz = _cpuBaseMhz;
            }
        }

        // ---- RAM ----
        var (totalRam, availableRam) = ProcessNative.GetMemoryStatus();

        // ---- GPU ----
        double gpuUtil = 0;
        ulong gpuMemory = 0;
        double? gpuTemp = null, gpuClock = null;
        if (_nvml is { IsAvailable: true })
        {
            var (utilization, memoryUsed, temperature, clock) = _nvml.Read();
            gpuUtil = utilization ?? 0;
            gpuMemory = memoryUsed ?? 0;
            gpuTemp = temperature;
            gpuClock = clock;
        }
        else
        {
            gpuUtil = SampleGpuEngineCounters();
        }

        // ---- Game processes ----
        var (gameCpu, gameRam) = SampleGameProcesses();

        return new HardwareMetrics
        {
            CpuUtilizationPercent = cpuPercent,
            CpuFrequencyMhz = cpuMhz,
            GpuUtilizationPercent = gpuUtil,
            GpuMemoryUsedBytes = gpuMemory,
            GpuTemperatureCelsius = gpuTemp,
            GpuClockMhz = gpuClock,
            RamTotalBytes = totalRam,
            RamUsedBytes = totalRam - availableRam,
            GameCpuPercent = gameCpu,
            GameRamBytes = gameRam,
        };
    }

    private (double CpuPercent, ulong RamBytes) SampleGameProcesses()
    {
        IReadOnlyList<int> pids;
        lock (_stateLock)
        {
            pids = _gameProcessIds;
        }
        if (pids.Count == 0)
        {
            return (0, 0);
        }

        var now = DateTimeOffset.UtcNow;
        var elapsed = _prevProcessSample == DateTimeOffset.MinValue ? Interval : now - _prevProcessSample;
        _prevProcessSample = now;

        double cpuPercent = 0;
        ulong ramBytes = 0;
        foreach (var pid in pids)
        {
            try
            {
                using var process = Process.GetProcessById(pid);
                ramBytes += (ulong)process.WorkingSet64;

                var cpuNow = process.TotalProcessorTime;
                lock (_stateLock)
                {
                    if (_prevProcessCpu.TryGetValue(pid, out var cpuPrev) && elapsed > TimeSpan.Zero)
                    {
                        cpuPercent += 100.0 * (cpuNow - cpuPrev).TotalMilliseconds
                            / (elapsed.TotalMilliseconds * Environment.ProcessorCount);
                    }
                    _prevProcessCpu[pid] = cpuNow;
                }
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                // Process exited or access denied, so skip it this tick.
            }
        }
        return (Math.Clamp(cpuPercent, 0, 100), ramBytes);
    }

    /// <summary>Fallback GPU utilization: sum of 3D-engine "GPU Engine" counters (documented PDH).</summary>
    private double SampleGpuEngineCounters()
    {
        try
        {
            if (_gpuEngineCounters is null)
            {
                var category = new PerformanceCounterCategory("GPU Engine");
                _gpuEngineCounters = category.GetInstanceNames()
                    .Where(name => name.EndsWith("engtype_3D", StringComparison.OrdinalIgnoreCase))
                    .Select(name => new PerformanceCounter("GPU Engine", "Utilization Percentage", name, readOnly: true))
                    .ToArray();
                foreach (var counter in _gpuEngineCounters)
                {
                    _ = counter.NextValue(); // warm-up
                }
                return 0;
            }

            double sum = 0;
            foreach (var counter in _gpuEngineCounters)
            {
                try
                {
                    sum += counter.NextValue();
                }
                catch (InvalidOperationException)
                {
                    // Instance disappeared (process exited), so rebuild next tick.
                    DisposeGpuCounters();
                    return 0;
                }
            }
            return Math.Clamp(sum, 0, 100);
        }
        catch (Exception ex) when (ex is InvalidOperationException or UnauthorizedAccessException)
        {
            _logger.LogDebug(ex, "GPU Engine counters unavailable");
            return 0;
        }
    }

    private static double ReadCpuBaseMhz()
    {
        try
        {
            using var searcher = new System.Management.ManagementObjectSearcher("SELECT MaxClockSpeed FROM Win32_Processor");
            foreach (var cpu in searcher.Get())
            {
                return Convert.ToDouble(cpu["MaxClockSpeed"] ?? 0); // MHz
            }
        }
        catch (Exception)
        {
            // WMI unavailable, so frequency will simply read 0.
        }
        return 0;
    }

    private void DisposeGpuCounters()
    {
        if (_gpuEngineCounters is not null)
        {
            foreach (var counter in _gpuEngineCounters)
            {
                counter.Dispose();
            }
            _gpuEngineCounters = null;
        }
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
        DisposeGpuCounters();
        _cpuPerformanceCounter?.Dispose();
        _nvml?.Dispose();
    }
}
