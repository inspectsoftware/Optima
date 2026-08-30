using System.Globalization;
using LibreHardwareMonitor.Hardware;
using Optima.Core.Ipc;

namespace Optima.Watchdog;

/// <summary>
/// CPU/GPU temperature and load sampling via LibreHardwareMonitor, which needs the
/// elevated side because its sensor access uses a kernel driver. One sample every two
/// seconds is pushed as a "hardwareSample" event while a stream is active.
/// </summary>
public sealed class HardwareStreamer : IDisposable
{
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(2);

    private readonly Func<IpcEvent, Task> _publishEvent;
    private readonly Computer _computer;
    private readonly System.Timers.Timer _timer;

    public HardwareStreamer(Func<IpcEvent, Task> publishEvent)
    {
        _publishEvent = publishEvent;
        _computer = new Computer
        {
            IsCpuEnabled = true,
            IsGpuEnabled = true,
        };
        _computer.Open();
        _timer = new System.Timers.Timer(Interval.TotalMilliseconds) { AutoReset = true };
        _timer.Elapsed += (_, _) => Sample();
        _timer.Start();
        HelperLog.Write("hardware stream started");
    }

    private void Sample()
    {
        try
        {
            float? cpuTemp = null, gpuTemp = null, cpuLoad = null, gpuLoad = null;
            foreach (var hardware in _computer.Hardware)
            {
                hardware.Update();
                var isCpu = hardware.HardwareType == HardwareType.Cpu;
                var isGpu = hardware.HardwareType is HardwareType.GpuNvidia or HardwareType.GpuAmd or HardwareType.GpuIntel;
                if (!isCpu && !isGpu)
                {
                    continue;
                }
                foreach (var sensor in hardware.Sensors)
                {
                    if (sensor.Value is not { } value)
                    {
                        continue;
                    }
                    switch (sensor.SensorType)
                    {
                        // CPUs report many temperature sensors; package/Tctl is the honest headline,
                        // and the maximum is a sane fallback when no package sensor exists.
                        case SensorType.Temperature when isCpu:
                            if (sensor.Name.Contains("Package", StringComparison.OrdinalIgnoreCase)
                                || sensor.Name.Contains("Tctl", StringComparison.OrdinalIgnoreCase))
                            {
                                cpuTemp = value;
                            }
                            else
                            {
                                cpuTemp = Math.Max(cpuTemp ?? float.MinValue, value);
                            }
                            break;
                        case SensorType.Temperature when isGpu
                            && sensor.Name.Contains("Core", StringComparison.OrdinalIgnoreCase):
                            gpuTemp = value;
                            break;
                        case SensorType.Load when isCpu
                            && sensor.Name.Contains("Total", StringComparison.OrdinalIgnoreCase):
                            cpuLoad = value;
                            break;
                        case SensorType.Load when isGpu
                            && sensor.Name.Contains("Core", StringComparison.OrdinalIgnoreCase):
                            gpuLoad = value;
                            break;
                    }
                }
            }

            _ = _publishEvent(new IpcEvent
            {
                Kind = "hardwareSample",
                Data = new Dictionary<string, string>
                {
                    ["cpuTempC"] = Format(cpuTemp),
                    ["gpuTempC"] = Format(gpuTemp),
                    ["cpuLoadPct"] = Format(cpuLoad),
                    ["gpuLoadPct"] = Format(gpuLoad),
                },
            });
        }
        catch (Exception ex)
        {
            HelperLog.Write("hardware sample failed: " + ex.Message);
        }

        static string Format(float? value)
            => value is { } v ? v.ToString("F1", CultureInfo.InvariantCulture) : "";
    }

    public void Dispose()
    {
        _timer.Stop();
        _timer.Dispose();
        _computer.Close();
        HelperLog.Write("hardware stream stopped");
    }
}
