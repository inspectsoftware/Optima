namespace Optima.Core.Abstractions;

/// <summary>The core-parking floor of one power plan as it was before BOOST raised it.</summary>
public sealed record CoreParkingSnapshot(Guid Scheme, uint AcMinCoresPercent, uint DcMinCoresPercent);

/// <summary>The two system settings BOOST adjusts around the game's process without touching the process.</summary>
public interface IGameExtras
{
    /// <summary>
    /// Raises "minimum unparked cores" of the active power plan to 100% (on mains and on battery),
    /// so Windows does not put cores to sleep mid-match. Null when the plan already keeps them all awake.
    /// </summary>
    CoreParkingSnapshot? KeepCoresUnparked();

    void RestoreCoreParking(CoreParkingSnapshot snapshot);

    /// <summary>The executable behind a process id, or null when it cannot be read.</summary>
    string? GetProcessPath(int processId);

    /// <summary>Whether Windows' per-app graphics preference for this executable is "High performance".</summary>
    bool IsGpuHighPerformance(string executablePath);

    /// <summary>
    /// Sets or clears Windows' per-app graphics preference (Settings, Display, Graphics) for an
    /// executable. Clearing only removes a "High performance" entry, never one the user set otherwise.
    /// </summary>
    void SetGpuHighPerformance(string executablePath, bool enabled);
}
