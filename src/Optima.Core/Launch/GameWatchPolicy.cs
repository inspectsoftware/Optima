using Optima.Core.Monitoring;

namespace Optima.Core.Launch;

public enum WatchAction
{
    None,
    Attach,
}

/// <summary>
/// Pure decision logic for watch mode (§5): at most one attach per game run.
///
/// This used to consume the raw presence poll and wait for two consecutive "running" ticks. That was
/// a second, slower copy of the state machine the presence service already runs — the same service
/// that debounces the other direction over three polls — so watch mode now rides the presence edges
/// every other consumer gets, and only remembers whether this run has already been attached.
/// </summary>
public sealed class GameWatchPolicy
{
    private bool _attached;

    public WatchAction OnPresenceChange(bool watchEnabled, bool sessionActive, PresenceChange change)
    {
        if (change.Current == GamePresence.NotRunning)
        {
            // The run is over; the next one may attach again.
            _attached = false;
            return WatchAction.None;
        }

        // Only the in-game edge attaches; "starting" means the emulator is up but no game window yet.
        return change.Current == GamePresence.InGame
            ? Decide(watchEnabled, sessionActive)
            : WatchAction.None;
    }

    /// <summary>
    /// A session Optima ran can end while the game stays up (cancelled launch, or the run stopped from
    /// the launcher), and that same game run should then be attached to. The decision is re-offered
    /// when the session gate closes.
    /// </summary>
    public WatchAction OnSessionEnded(bool watchEnabled, bool sessionActive, bool gameRunning)
        => gameRunning ? Decide(watchEnabled, sessionActive) : WatchAction.None;

    private WatchAction Decide(bool watchEnabled, bool sessionActive)
    {
        if (!watchEnabled || sessionActive || _attached)
        {
            return WatchAction.None;
        }

        _attached = true;
        return WatchAction.Attach;
    }
}
