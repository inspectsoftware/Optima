using System.Diagnostics;
using Microsoft.Win32;
using Optima.Core.Abstractions;
using Optima.Core.Boost;
using Optima.Platform.Windows.NativeMethods;
using Microsoft.Extensions.Logging;

namespace Optima.Platform.Windows.Services;

/// <summary>
/// Core parking and the power floor through the documented power-setting API, and the per-app graphics preference
/// through the same registry value the Windows Settings app writes. Neither needs administrator rights.
/// </summary>
public sealed class WindowsGameExtras : IGameExtras
{
    private const string GpuPreferencesKey = @"Software\Microsoft\DirectX\UserGpuPreferences";
    private const string AdapterEntry = "GpuPreference=";
    private const uint AllCores = 100;

    private readonly ILogger<WindowsGameExtras> _logger;

    public WindowsGameExtras(ILogger<WindowsGameExtras> logger)
    {
        _logger = logger;
    }

    public CoreParkingSnapshot? KeepCoresUnparked()
    {
        var scheme = PowerNative.GetActiveScheme();
        var (ac, dc) = PowerNative.ReadMinUnparkedCores(scheme);
        if (ac >= AllCores && dc >= AllCores)
        {
            return null;
        }
        PowerNative.WriteMinUnparkedCores(scheme, AllCores, AllCores);
        PowerNative.ReactivateIfActive(scheme);
        _logger.LogInformation("Core parking floor raised to 100% on plan {Scheme} (was {Ac}% on mains, {Dc}% on battery)", scheme, ac, dc);
        return new CoreParkingSnapshot(scheme, ac, dc);
    }

    public void RestoreCoreParking(CoreParkingSnapshot snapshot)
    {
        PowerNative.WriteMinUnparkedCores(snapshot.Scheme, snapshot.AcMinCoresPercent, snapshot.DcMinCoresPercent);
        PowerNative.ReactivateIfActive(snapshot.Scheme);
        _logger.LogInformation("Core parking floor restored on plan {Scheme}", snapshot.Scheme);
    }

    public PowerFloorSnapshot? RaisePowerFloor()
    {
        var scheme = PowerNative.GetActiveScheme();
        var (minAc, _) = PowerNative.ReadValue(scheme, PowerNative.MinProcessorState);
        var (usbAc, usbDc) = PowerNative.ReadValue(scheme, PowerNative.UsbSelectiveSuspend);
        if (PowerFloorSnapshot.Of(scheme, minAc, usbAc, usbDc) is not { } snapshot)
        {
            return null;
        }
        try
        {
            PowerNative.WriteValue(scheme, PowerNative.MinProcessorState,
                snapshot.AcMinProcessorPercent is null ? null : PowerFloorSnapshot.FullSpeed, dc: null);
            PowerNative.WriteValue(scheme, PowerNative.UsbSelectiveSuspend,
                snapshot.AcUsbSuspend is null ? null : PowerFloorSnapshot.SuspendOff,
                snapshot.DcUsbSuspend is null ? null : PowerFloorSnapshot.SuspendOff);
            PowerNative.ReactivateIfActive(scheme);
        }
        catch
        {
            // Nothing is held when this throws, so a value already written would be read at the
            // next try as the plan's own and kept for good. What was written is put back first;
            // writing back one that was never changed writes the value it already has.
            try
            {
                RestorePowerFloor(snapshot);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "The power floor could not be taken back after a failed change on plan {Scheme}", scheme);
            }
            throw;
        }
        _logger.LogInformation("Power floor set on plan {Scheme} (minimum processor state was {Min}% on mains, USB selective suspend {UsbAc} on mains, {UsbDc} on battery)",
            scheme, minAc, usbAc, usbDc);
        return snapshot;
    }

    public void RestorePowerFloor(PowerFloorSnapshot snapshot)
    {
        PowerNative.WriteValue(snapshot.Scheme, PowerNative.MinProcessorState, snapshot.AcMinProcessorPercent, dc: null);
        PowerNative.WriteValue(snapshot.Scheme, PowerNative.UsbSelectiveSuspend, snapshot.AcUsbSuspend, snapshot.DcUsbSuspend);
        PowerNative.ReactivateIfActive(snapshot.Scheme);
        _logger.LogInformation("Power floor restored on plan {Scheme}", snapshot.Scheme);
    }

    public string? GetProcessPath(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            return process.MainModule?.FileName;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return null;
        }
    }

    public string? GetGpuPreference(string executablePath)
    {
        using var key = Registry.CurrentUser.OpenSubKey(GpuPreferencesKey);
        return GpuPreferenceOf(key?.GetValue(executablePath) as string);
    }

    public void SetGpuPreference(string executablePath, string? preference)
    {
        using var key = Registry.CurrentUser.CreateSubKey(GpuPreferencesKey);
        var updated = WithGpuPreference(key.GetValue(executablePath) as string, preference);
        if (updated.Length == 0)
        {
            key.DeleteValue(executablePath, throwOnMissingValue: false);
        }
        else
        {
            key.SetValue(executablePath, updated, RegistryValueKind.String);
        }
        _logger.LogInformation("Graphics preference for {Path} {Action}", executablePath,
            preference is null ? "cleared" : "set to " + preference);
    }

    public IReadOnlyList<LogicalCpu> ReadProcessors() => CpuSetNative.ReadProcessors();

    public bool PlaceProcess(int processId, IReadOnlyList<uint>? cpuSetIds)
    {
        var placed = CpuSetNative.SetDefaultCpuSets(processId, cpuSetIds);
        _logger.LogInformation("CPU sets of process {Pid} {Action}: {Result}", processId,
            cpuSetIds is null ? "cleared" : "set to " + string.Join(',', cpuSetIds), placed ? "done" : "refused or gone");
        return placed;
    }

    public IReadOnlyList<uint>? ReadPlacement(int processId) => CpuSetNative.GetDefaultCpuSets(processId);

    public IReadOnlyList<ThreadCpuSets> ReadThreadPlacement(int processId) => CpuSetNative.GetThreadCpuSets(processId);

    public bool PlaceThread(int processId, ThreadCpuSets thread, IReadOnlyList<uint> cpuSetIds)
        => CpuSetNative.SetThreadCpuSets(processId, thread, cpuSetIds);

    public int CountPinnedThreads(int processId, IReadOnlyList<int> logicalProcessors)
        => CpuSetNative.CountPinnedThreads(processId, logicalProcessors);

    public IReadOnlyList<RunningProcess> FindProcesses(IReadOnlyList<string> names)
    {
        var wanted = names
            .Select(n => n.Trim().EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? n.Trim()[..^4] : n.Trim())
            .Where(n => n.Length > 0 && !WindowsBackgroundCleanupService.IsProtected(n))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        using var self = Process.GetCurrentProcess();
        var found = new List<RunningProcess>();
        foreach (var (id, name) in ProcessSnapshot.GetRunning().Where(p => wanted.Contains(p.Name)))
        {
            // Another user's programs are not this user's to move.
            if (!ProcessIdToSessionId((uint)id, out var session) || session != self.SessionId)
            {
                continue;
            }
            if (StartTimeOf(id) is { } started)
            {
                found.Add(new RunningProcess(id, started));
            }
        }
        return found;
    }

    public DateTimeOffset? StartTimeOf(int processId)
    {
        using var handle = ProcessQuery.Open(processId);
        return handle is null || ProcessQuery.HasExited(handle) ? null : ProcessQuery.GetStartTime(handle);
    }

    public GpuPriorityResult ReadGpuPriority(int processId, out int priorityClass) => GpuPriorityNative.Read(processId, out priorityClass);

    public GpuPriorityResult SetGpuPriority(int processId, int priorityClass)
    {
        var result = GpuPriorityNative.Set(processId, priorityClass);
        _logger.LogInformation("GPU priority of process {Pid} set to class {Class}: {Result}", processId, priorityClass, result);
        return result;
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll")]
    private static extern bool ProcessIdToSessionId(uint processId, out uint sessionId);

    /// <summary>The adapter choice in a Windows value such as "SwapEffectUpgradeEnable=1;GpuPreference=1;".</summary>
    public static string? GpuPreferenceOf(string? data)
        => Entries(data).FirstOrDefault(IsAdapterEntry)?[AdapterEntry.Length..];

    /// <summary>
    /// The value holds other per-app choices beside the adapter (windowed optimizations, Auto HDR),
    /// so only the adapter entry is replaced or taken out.
    /// </summary>
    public static string WithGpuPreference(string? data, string? preference)
    {
        var entries = Entries(data).Where(entry => !IsAdapterEntry(entry)).ToList();
        if (preference is not null)
        {
            entries.Add(AdapterEntry + preference);
        }
        return entries.Count == 0 ? string.Empty : string.Join(';', entries) + ";";
    }

    private static string[] Entries(string? data)
        => (data ?? string.Empty).Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static bool IsAdapterEntry(string entry) => entry.StartsWith(AdapterEntry, StringComparison.OrdinalIgnoreCase);
}
