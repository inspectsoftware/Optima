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

    /// <summary>
    /// The adapter choice in Windows' per-app graphics preference (Settings, Display, Graphics) for
    /// an executable: "0" (let Windows decide), "1" (power saving) or "2" (high performance), or
    /// null when it has none.
    /// </summary>
    string? GetGpuPreference(string executablePath);

    /// <summary>
    /// Sets the adapter choice for an executable, or takes it out when <paramref name="preference"/>
    /// is null. The other per-app choices kept in the same Windows value stay as they are.
    /// </summary>
    void SetGpuPreference(string executablePath, string? preference);
}
