using Optima.Core.Boost;

namespace Optima.Core.Abstractions;

/// <summary>A running process and when it started, which tells it from a later one given its id.</summary>
public sealed record RunningProcess(int ProcessId, DateTimeOffset? StartedAt);

/// <summary>
/// One thread's own CPU sets, with its creation time (a FILETIME), which tells it from a later
/// thread given its id.
/// </summary>
public sealed record ThreadCpuSets(int ThreadId, long CreatedAt, IReadOnlyList<uint> CpuSetIds);

/// <summary>The core-parking floor of one power plan as it was before BOOST raised it.</summary>
public sealed record CoreParkingSnapshot(Guid Scheme, uint AcMinCoresPercent, uint DcMinCoresPercent);

/// <summary>
/// The minimum processor state (on mains) and USB selective suspend (on mains and on battery) of one
/// power plan as they were before BOOST changed them. A value is null where the plan already had
/// what Boost wants, so Boost left it alone and has nothing to put back.
/// </summary>
public sealed record PowerFloorSnapshot(Guid Scheme, uint? AcMinProcessorPercent, uint? AcUsbSuspend, uint? DcUsbSuspend)
{
    /// <summary>The minimum processor state Boost holds on mains: the processor does not clock down between frames.</summary>
    public const uint FullSpeed = 100;

    /// <summary>USB selective suspend's "Disabled" index: Windows does not put an idle mouse or headset to sleep.</summary>
    public const uint SuspendOff = 0;

    /// <summary>
    /// What Boost changes on a plan with these values, or null when it already has them all. The
    /// minimum processor state on battery is never touched: on a laptop off the charger it would
    /// cost battery and heat for little.
    /// </summary>
    public static PowerFloorSnapshot? Of(Guid scheme, uint acMinProcessorPercent, uint acUsbSuspend, uint dcUsbSuspend)
    {
        var snapshot = new PowerFloorSnapshot(scheme,
            acMinProcessorPercent >= FullSpeed ? null : acMinProcessorPercent,
            acUsbSuspend == SuspendOff ? null : acUsbSuspend,
            dcUsbSuspend == SuspendOff ? null : dcUsbSuspend);
        return snapshot is { AcMinProcessorPercent: null, AcUsbSuspend: null, DcUsbSuspend: null } ? null : snapshot;
    }
}

/// <summary>How a read or change of a process's GPU scheduling priority went.</summary>
public enum GpuPriorityResult
{
    Done,

    /// <summary>Windows said no: the process cannot be opened for it, or a privilege this user lacks is needed.</summary>
    Refused,

    /// <summary>The process is gone, or has not used the graphics card yet, so Windows has no GPU priority for it.</summary>
    Unavailable,
}

/// <summary>The system settings and CPU sets BOOST adjusts around the game's process.</summary>
public interface IGameExtras
{
    /// <summary>
    /// Raises "minimum unparked cores" of the active power plan to 100% (on mains and on battery),
    /// so Windows does not put cores to sleep mid-match. Null when the plan already keeps them all awake.
    /// </summary>
    CoreParkingSnapshot? KeepCoresUnparked();

    void RestoreCoreParking(CoreParkingSnapshot snapshot);

    /// <summary>
    /// Sets the active power plan's minimum processor state to 100% on mains and turns USB selective
    /// suspend off (on mains and on battery), each only where the plan does not have it already.
    /// Null when it has both, and nothing was changed.
    /// </summary>
    PowerFloorSnapshot? RaisePowerFloor();

    /// <summary>Writes back the values <see cref="RaisePowerFloor"/> changed, and only those.</summary>
    void RestorePowerFloor(PowerFloorSnapshot snapshot);

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

    /// <summary>Every logical processor as Windows' CPU set table describes it.</summary>
    IReadOnlyList<LogicalCpu> ReadProcessors();

    /// <summary>
    /// Gives a process default CPU sets, or takes them away when <paramref name="cpuSetIds"/> is
    /// null. They end with the process. False when it is gone or cannot be opened.
    /// </summary>
    bool PlaceProcess(int processId, IReadOnlyList<uint>? cpuSetIds);

    /// <summary>A process's default CPU sets as Windows has them now: empty for none, null when unreadable.</summary>
    IReadOnlyList<uint>? ReadPlacement(int processId);

    /// <summary>The threads of a process that have CPU sets of their own, which replace the process default for them.</summary>
    IReadOnlyList<ThreadCpuSets> ReadThreadPlacement(int processId);

    /// <summary>
    /// Gives one thread of a process <paramref name="cpuSetIds"/> as its own CPU sets. False when it
    /// is no longer that thread (another process's, or a later one given its id) or Windows refused.
    /// </summary>
    bool PlaceThread(int processId, ThreadCpuSets thread, IReadOnlyList<uint> cpuSetIds);

    /// <summary>
    /// How many threads of a process have a hard affinity with none of <paramref name="logicalProcessors"/>
    /// in it. Windows follows such a pin over any CPU set, so those threads run off the chosen cores.
    /// </summary>
    int CountPinnedThreads(int processId, IReadOnlyList<int> logicalProcessors);

    /// <summary>
    /// The running processes of this user with one of the given names (without ".exe"). Windows,
    /// the game, Google Play Games and Optima are never among them, whatever the names say.
    /// </summary>
    IReadOnlyList<RunningProcess> FindProcesses(IReadOnlyList<string> names);

    /// <summary>When a process started, or null when it is not running.</summary>
    DateTimeOffset? StartTimeOf(int processId);

    /// <summary>
    /// A process's GPU scheduling priority class (0 idle, 1 below normal, 2 normal, 3 above normal,
    /// 4 high, 5 realtime), the one the graphics kernel uses to order its work against other programs'.
    /// </summary>
    GpuPriorityResult ReadGpuPriority(int processId, out int priorityClass);

    /// <summary>Gives a process a GPU scheduling priority class. It ends with the process.</summary>
    GpuPriorityResult SetGpuPriority(int processId, int priorityClass);
}
