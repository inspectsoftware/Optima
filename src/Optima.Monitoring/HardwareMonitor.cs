using System.Diagnostics;
using Microsoft.Win32.SafeHandles;
using Optima.Core.Abstractions;
using Optima.Core.Models;
using Optima.Monitoring.Nvidia;
using Optima.Platform.Windows.NativeMethods;
using Microsoft.Extensions.Logging;

namespace Optima.Monitoring;

/// <summary>Live dashboard feed (§12): one sample per second while someone can see it.</summary>
public sealed class HardwareMonitor : IPerformanceMonitor
{
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(1);

    private readonly ILogger<HardwareMonitor> _logger;
    private readonly object _stateLock = new();
    private readonly SemaphoreSlim _lifecycle = new(1, 1);

    private NvmlGpuReader? _nvml;
    private GpuEngineCounters? _gpuEngines;
    private bool _gpuEnginesUnavailable;
    private PerformanceCounter? _cpuPerformanceCounter;
    private bool _countersInitialized;
    private double _cpuBaseMhz;
    private CancellationTokenSource? _cts;
    private Task? _loop;

    private long _prevIdle, _prevKernel, _prevUser;
    private readonly Dictionary<int, SafeProcessHandle> _gameProcesses = [];
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
            foreach (var pid in _gameProcesses.Keys.Where(pid => !processIds.Contains(pid)).ToList())
            {
                _gameProcesses[pid].Dispose();
                _gameProcesses.Remove(pid);
                _prevProcessCpu.Remove(pid);
            }
            foreach (var pid in processIds)
            {
                if (!_gameProcesses.ContainsKey(pid) && ProcessQuery.Open(pid) is { } handle)
                {
                    _gameProcesses[pid] = handle;
                }
            }
        }
    }

    public async Task StartAsync(CancellationToken ct = default)
    {
        await _lifecycle.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_loop is not null)
            {
                return;
            }

            InitializeCountersOnce();
            ProcessNative.GetSystemTimes(out _prevIdle, out _prevKernel, out _prevUser);
            lock (_stateLock)
            {
                _prevProcessCpu.Clear();
                _prevProcessSample = DateTimeOffset.MinValue;
            }

            _cts = new CancellationTokenSource();
            var token = _cts.Token;
            _loop = Task.Run(() => SampleLoopAsync(token), CancellationToken.None);
            _logger.LogInformation("Hardware monitor sampling (NVML available: {Nvml})", _nvml?.IsAvailable ?? false);
        }
        finally
        {
            _lifecycle.Release();
        }
    }

    public async Task StopAsync()
    {
        await _lifecycle.WaitAsync().ConfigureAwait(false);
        try
        {
            var loop = _loop;
            if (loop is null)
            {
                return;
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
            _logger.LogDebug("Hardware monitor paused");
        }
        finally
        {
            _lifecycle.Release();
        }
    }

    private void InitializeCountersOnce()
    {
        if (_countersInitialized)
        {
            return;
        }
        _countersInitialized = true;

        _nvml = new NvmlGpuReader();
        try
        {
            _cpuPerformanceCounter = new PerformanceCounter("Processor Information", "% Processor Performance", "_Total", readOnly: true);
            _ = _cpuPerformanceCounter.NextValue();
        }
        catch (Exception ex) when (ex is InvalidOperationException or UnauthorizedAccessException)
        {
            _logger.LogDebug(ex, "CPU performance counter unavailable");
            _cpuPerformanceCounter = null;
        }
        _cpuBaseMhz = ReadCpuBaseMhz();
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
        ProcessNative.GetSystemTimes(out var idle, out var kernel, out var user);
        var idleDelta = idle - _prevIdle;
        var busyDelta = (kernel - _prevKernel) + (user - _prevUser) - idleDelta;
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

        var (totalRam, availableRam) = ProcessNative.GetMemoryStatus();

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
            gpuUtil = SampleGpuEngines();
        }

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
        lock (_stateLock)
        {
            if (_gameProcesses.Count == 0)
            {
                return (0, 0);
            }

            var now = DateTimeOffset.UtcNow;
            var elapsed = _prevProcessSample == DateTimeOffset.MinValue ? Interval : now - _prevProcessSample;
            _prevProcessSample = now;

            double cpuPercent = 0;
            ulong ramBytes = 0;
            foreach (var (pid, handle) in _gameProcesses)
            {
                if (ProcessQuery.GetWorkingSetBytes(handle) is { } workingSet)
                {
                    ramBytes += workingSet;
                }
                if (ProcessQuery.GetTotalProcessorTime(handle) is { } cpuNow)
                {
                    if (_prevProcessCpu.TryGetValue(pid, out var cpuPrev) && elapsed > TimeSpan.Zero)
                    {
                        cpuPercent += 100.0 * (cpuNow - cpuPrev).TotalMilliseconds
                            / (elapsed.TotalMilliseconds * Environment.ProcessorCount);
                    }
                    _prevProcessCpu[pid] = cpuNow;
                }
            }
            return (Math.Clamp(cpuPercent, 0, 100), ramBytes);
        }
    }

    private double SampleGpuEngines()
    {
        if (_gpuEnginesUnavailable)
        {
            return 0;
        }
        try
        {
            _gpuEngines ??= new GpuEngineCounters();
            return _gpuEngines.Sample();
        }
        catch (Exception ex) when (ex is InvalidOperationException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            _logger.LogDebug(ex, "GPU Engine counters unavailable; GPU utilization stays at 0");
            _gpuEnginesUnavailable = true;
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
                return Convert.ToDouble(cpu["MaxClockSpeed"] ?? 0);
            }
        }
        catch (Exception)
        {
        }
        return 0;
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
        lock (_stateLock)
        {
            foreach (var handle in _gameProcesses.Values)
            {
                handle.Dispose();
            }
            _gameProcesses.Clear();
        }
        _cpuPerformanceCounter?.Dispose();
        _nvml?.Dispose();
    }
}
