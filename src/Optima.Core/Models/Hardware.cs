namespace Optima.Core.Models;

/// <summary>Static hardware / OS facts shown on the dashboard and SYSTEM page.</summary>
public sealed record SystemInventory
{
    public string CpuName { get; init; } = string.Empty;
    public int CpuCores { get; init; }
    public int CpuThreads { get; init; }
    public IReadOnlyList<GpuInfo> Gpus { get; init; } = [];
    public ulong TotalRamBytes { get; init; }
    public string WindowsVersion { get; init; } = string.Empty;
    public IReadOnlyList<DisplayInfo> Displays { get; init; } = [];
    public VirtualizationState Virtualization { get; init; } = new();
}

public sealed record GpuInfo
{
    public required string Name { get; init; }
    public string DriverVersion { get; init; } = string.Empty;
    public ulong VramBytes { get; init; }
    public GpuVendor Vendor { get; init; }
}

public enum GpuVendor
{
    Unknown,
    Nvidia,
    Amd,
    Intel,
}

/// <summary>One tick of live utilization data for the dashboard (§12).</summary>
public sealed record HardwareMetrics
{
    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.UtcNow;
    public double CpuUtilizationPercent { get; init; }
    public double CpuFrequencyMhz { get; init; }
    public double GpuUtilizationPercent { get; init; }
    public ulong GpuMemoryUsedBytes { get; init; }
    public double? GpuTemperatureCelsius { get; init; }
    public double? GpuClockMhz { get; init; }
    public ulong RamUsedBytes { get; init; }
    public ulong RamTotalBytes { get; init; }
    public double GameCpuPercent { get; init; }
    public ulong GameRamBytes { get; init; }
    public double? CurrentFps { get; init; }
    public double? CurrentFrametimeMs { get; init; }
}
