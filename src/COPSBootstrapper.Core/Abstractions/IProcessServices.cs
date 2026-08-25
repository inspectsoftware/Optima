using COPSBootstrapper.Core.Models;

namespace COPSBootstrapper.Core.Abstractions;

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

    /// <summary>Current runtime state of the target game (window title + emulator process heuristics).</summary>
    Task<GameRuntimeState> GetGameStateAsync(CancellationToken ct = default);

    /// <summary>Waits until the game is detected as running, or the timeout elapses. Returns the emulator process id, or null.</summary>
    Task<int?> WaitForGameStartAsync(TimeSpan timeout, CancellationToken ct = default);

    /// <summary>Completes when the running game exits.</summary>
    Task WaitForGameExitAsync(CancellationToken ct = default);
}

/// <summary>Applies reversible scheduling tweaks to processes (§9). Every change returns its undo state.</summary>
public interface IProcessOptimizer
{
    /// <summary>Applies priority / affinity / power-throttling settings, returning the original state for restore.</summary>
    Task<ProcessStateSnapshot?> ApplyAsync(int processId, PerformanceProfile profile, CancellationToken ct = default);

    Task RestoreAsync(ProcessStateSnapshot snapshot, CancellationToken ct = default);
}
