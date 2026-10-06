using Optima.Core.Models;

namespace Optima.Core.Abstractions;

/// <summary>A process relevant to the platform / game, as seen by the monitor.</summary>
public sealed record TrackedProcess
{
    public required int ProcessId { get; init; }
    public required string Name { get; init; }
    public string MainWindowTitle { get; init; } = string.Empty;
    public TrackedProcessKind Kind { get; init; }
    public DateTimeOffset? StartedAt { get; init; }
}

public enum TrackedProcessKind
{
    Other,
    Platform,
    Emulator,
    GameWindow,
}

/// <summary>Watches Google Play Games / emulator / game processes (§9).</summary>
public interface IProcessMonitor
{
    Task<IReadOnlyList<TrackedProcess>> GetTrackedProcessesAsync(CancellationToken ct = default);

    Task<GameRuntimeState> GetGameStateAsync(CancellationToken ct = default);

    Task<int?> WaitForGameStartAsync(TimeSpan timeout, CancellationToken ct = default);

    Task WaitForGameExitAsync(CancellationToken ct = default);
}

/// <summary>Outcome of a game kill request.</summary>
public sealed record GameKillResult(bool Killed, string Message);

/// <summary>Hard-kills the emulator process tree hosting the game.</summary>
public interface IGameTerminator
{
    Task<GameKillResult> KillGameAsync(CancellationToken ct = default);
}

/// <summary>Applies reversible scheduling tweaks to processes (§9).</summary>
public interface IProcessOptimizer
{
    Task<ProcessStateSnapshot?> ApplyAsync(int processId, PerformanceProfile profile, CancellationToken ct = default);

    /// <summary>
    /// Puts the profile's wanted settings back on a process that was already tuned, without
    /// capturing a new baseline. A session keeper calls this while the game runs so a setting
    /// something else changed is corrected; it writes nothing when the process is already right.
    /// True when something had drifted and was put back.
    /// </summary>
    Task<bool> ReassertAsync(ProcessStateSnapshot baseline, PerformanceProfile profile, CancellationToken ct = default);

    Task RestoreAsync(ProcessStateSnapshot snapshot, CancellationToken ct = default);
}
