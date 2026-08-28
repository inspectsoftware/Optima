using Optima.Core.Models;

namespace Optima.Core.Launch;

public enum WatchAction
{
    None,
    /// <summary>Attach to the running game now: apply the profile and monitor until exit.</summary>
    Attach,
}

/// <summary>
/// Pure decision logic for watch mode (§5), one instance per watch loop. Attaches only after
/// the game has been Running for two consecutive polls (a process that dies instantly is not
/// a session), never while the orchestrator already runs a session (the PLAY button owns it),
/// and never twice for the same game run. State resets when the game is gone.
/// </summary>
public sealed class GameWatchPolicy
{
    private const int DebouncePolls = 2;

    private int _consecutiveRunning;
    private bool _attached;

    /// <summary>Called once per poll tick; returning Attach marks this run as being handled.</summary>
    public WatchAction OnPoll(bool watchEnabled, bool sessionActive, GameRuntimeState state)
    {
        if (state is not GameRuntimeState.Running)
        {
            _consecutiveRunning = 0;
            if (state is GameRuntimeState.NotRunning or GameRuntimeState.Exited)
            {
                // The game is gone; a future start is a new run.
                _attached = false;
            }
            return WatchAction.None;
        }

        _consecutiveRunning++;
        if (!watchEnabled || sessionActive || _attached || _consecutiveRunning < DebouncePolls)
        {
            return WatchAction.None;
        }

        _attached = true;
        return WatchAction.Attach;
    }
}
