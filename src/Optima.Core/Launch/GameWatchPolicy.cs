using Optima.Core.Models;

namespace Optima.Core.Launch;

public enum WatchAction
{
    None,
    Attach,
}

/// <summary>Pure decision logic for watch mode (§5), one instance per watch loop.</summary>
public sealed class GameWatchPolicy
{
    private const int DebouncePolls = 2;

    private int _consecutiveRunning;
    private bool _attached;

    public WatchAction OnPoll(bool watchEnabled, bool sessionActive, GameRuntimeState state)
    {
        if (state is not GameRuntimeState.Running)
        {
            _consecutiveRunning = 0;
            if (state is GameRuntimeState.NotRunning or GameRuntimeState.Exited)
            {
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
