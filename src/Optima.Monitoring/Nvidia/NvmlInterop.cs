using System.Runtime.InteropServices;

namespace Optima.Monitoring.Nvidia;

/// <summary>Minimal binding to NVIDIA's documented NVML management library (nvml.dll ships with the GeForce driver).</summary>
internal static class NvmlInterop
{
    private const string Dll = "nvml.dll";

    [StructLayout(LayoutKind.Sequential)]
    internal struct NvmlUtilization
    {
        public uint Gpu;
        public uint Memory;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct NvmlMemory
    {
        public ulong Total;
        public ulong Free;
        public ulong Used;
    }

    [DllImport(Dll, EntryPoint = "nvmlInit_v2")]
    internal static extern int Init();

    [DllImport(Dll, EntryPoint = "nvmlShutdown")]
    internal static extern int Shutdown();

    [DllImport(Dll, EntryPoint = "nvmlDeviceGetCount_v2")]
    internal static extern int GetDeviceCount(out uint count);

    [DllImport(Dll, EntryPoint = "nvmlDeviceGetHandleByIndex_v2")]
    internal static extern int GetDeviceHandle(uint index, out IntPtr device);

    [DllImport(Dll, EntryPoint = "nvmlDeviceGetTemperature")]
    internal static extern int GetTemperature(IntPtr device, int sensorType, out uint temperatureCelsius);

    [DllImport(Dll, EntryPoint = "nvmlDeviceGetUtilizationRates")]
    internal static extern int GetUtilizationRates(IntPtr device, out NvmlUtilization utilization);

    [DllImport(Dll, EntryPoint = "nvmlDeviceGetClockInfo")]
    internal static extern int GetClockInfo(IntPtr device, int clockType, out uint clockMhz);

    [DllImport(Dll, EntryPoint = "nvmlDeviceGetMemoryInfo")]
    internal static extern int GetMemoryInfo(IntPtr device, out NvmlMemory memory);

    internal const int SensorGpu = 0;
    internal const int ClockGraphics = 0;
}

/// <summary>Safe wrapper that answers "n/a" (nulls) whenever NVML is unavailable.</summary>
public sealed class NvmlGpuReader : IDisposable
{
    private readonly IntPtr _device;
    private readonly bool _available;

    public NvmlGpuReader()
    {
        try
        {
            if (NvmlInterop.Init() != 0)
            {
                return;
            }
            if (NvmlInterop.GetDeviceCount(out var count) != 0 || count == 0)
            {
                NvmlInterop.Shutdown();
                return;
            }
            if (NvmlInterop.GetDeviceHandle(0, out _device) != 0)
            {
                NvmlInterop.Shutdown();
                return;
            }
            _available = true;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
        {
            _available = false;
        }
    }

    public bool IsAvailable => _available;

    public (double? UtilizationPercent, ulong? MemoryUsedBytes, double? TemperatureC, double? ClockMhz) Read()
    {
        if (!_available)
        {
            return (null, null, null, null);
        }

        double? utilization = NvmlInterop.GetUtilizationRates(_device, out var rates) == 0 ? rates.Gpu : null;
        ulong? memoryUsed = NvmlInterop.GetMemoryInfo(_device, out var memory) == 0 ? memory.Used : null;
        double? temperature = NvmlInterop.GetTemperature(_device, NvmlInterop.SensorGpu, out var temp) == 0 ? temp : null;
        double? clock = NvmlInterop.GetClockInfo(_device, NvmlInterop.ClockGraphics, out var mhz) == 0 ? mhz : null;
        return (utilization, memoryUsed, temperature, clock);
    }

    public void Dispose()
    {
        if (_available)
        {
            try
            {
                NvmlInterop.Shutdown();
            }
            catch (Exception)
            {
            }
        }
    }
}
