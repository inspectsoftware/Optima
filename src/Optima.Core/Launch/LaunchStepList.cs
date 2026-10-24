namespace Optima.Core.Launch;

/// <summary>State of one row in the session page's step list.</summary>
public enum StepState
{
    Todo,
    Live,
    Done,

    /// <summary>Not part of this session: switched off, or given up on. It looks like a step still to come.</summary>
    Skipped,
    Failed,
}

/// <summary>
/// The session page's step list, moved along by the launch's progress reports. The rows are in the
/// order things happen: the install is checked, the profile goes on, the game starts, the virtual
/// display comes on once the game is running, and everything is put back when it exits. Kept apart
/// from the page so these rules can be tested; the page copies the states and lines onto its rows.
/// </summary>
public sealed class LaunchStepList
{
    public const int Launcher = 0;
    public const int Profile = 1;
    public const int Game = 2;
    public const int Display = 3;
    public const int Restore = 4;

    private readonly StepState[] _states = new StepState[Restore + 1];
    private readonly string?[] _details = new string?[Restore + 1];

    /// <summary>The game has been seen running in this session: the display step or the watch has begun.</summary>
    private bool _gameSeenRunning;

    public IReadOnlyList<StepState> States => _states;

    /// <summary>What each row says beside its title; null for the row's own line.</summary>
    public IReadOnlyList<string?> Details => _details;

    public void Reset()
    {
        Array.Clear(_states);
        Array.Clear(_details);
        _gameSeenRunning = false;
    }

    public void Apply(LaunchProgress progress)
    {
        switch (progress.Phase)
        {
            case LaunchPhase.Idle:
                return;
            case LaunchPhase.Completed:
                Complete(cancelled: progress.Skipped);
                return;
            case LaunchPhase.Failed:
                Fail();
                return;
            case LaunchPhase.Validating:
                // Every session starts here, one that watch mode attached to as well as one PLAY
                // started, and nothing from the last one may show through.
                Reset();
                break;
        }

        _gameSeenRunning |= progress.Phase is LaunchPhase.ConfiguringDisplay or LaunchPhase.Monitoring;

        if (progress is { Phase: LaunchPhase.ConfiguringDisplay, Skipped: true })
        {
            // Why is on the session's notices; the row only says that it was not set up. Not "off": the
            // display can have come on and then refused the chosen mode.
            _states[Display] = StepState.Skipped;
            _details[Display] = "not set up";
            return;
        }

        var live = progress.Phase switch
        {
            LaunchPhase.Validating => Launcher,
            LaunchPhase.ApplyingPerformanceProfile => Profile,
            LaunchPhase.StartingPlatform or LaunchPhase.WaitingForGame or LaunchPhase.Monitoring => Game,
            LaunchPhase.ConfiguringDisplay => Display,
            LaunchPhase.Restoring => Restore,
            _ => -1,
        };
        if (live < 0)
        {
            return;
        }

        if (live == Display)
        {
            // The display only comes on once the game has been seen running.
            _details[Game] = "running";
        }
        if (progress.Phase is LaunchPhase.Monitoring or LaunchPhase.Restoring)
        {
            // The display row comes after the game's, and is settled by the time the game is being
            // watched: it came on, it was given up on (marked when that happened), or it was never asked for.
            switch (_states[Display])
            {
                case StepState.Live or StepState.Done:
                    _states[Display] = StepState.Done;
                    _details[Display] = "on while the game runs";
                    break;
                case StepState.Todo:
                    _states[Display] = StepState.Skipped;
                    _details[Display] = "off on the Display page";
                    break;
            }
        }

        for (var i = 0; i < live; i++)
        {
            if (_states[i] is not StepState.Skipped)
            {
                _states[i] = StepState.Done;
            }
        }
        _states[live] = StepState.Live;
        _details[live] = progress.Phase == LaunchPhase.Monitoring ? "running" : progress.Message;
    }

    /// <summary>
    /// The session ended and everything was put back. A skipped step stays skipped, and a step the
    /// session never got to stays to come: a cancel ends it there, before a display that waits for
    /// the game has come on. Nor is the step a cancel stopped in ticked off: a game that was still
    /// being waited for never ran, and a display still coming on never came on. A game that was
    /// already running did run, and keeps its tick.
    /// </summary>
    public void Complete(bool cancelled = false)
    {
        for (var i = 0; i < Restore; i++)
        {
            if (cancelled && _states[i] == StepState.Live && !(i == Game && _gameSeenRunning))
            {
                _states[i] = StepState.Skipped;
                _details[i] = "cancelled";
            }
            else if (_states[i] is StepState.Live or StepState.Done)
            {
                _states[i] = StepState.Done;
            }
        }
        _states[Restore] = StepState.Done;
        _details[Restore] = "everything restored";
    }

    /// <summary>The session failed: the step that was running is the one that failed.</summary>
    public void Fail()
    {
        // A failure is reported twice (by the phase and by the result); the second report finds no
        // live step and must not blame the first one.
        if (Array.IndexOf(_states, StepState.Failed) >= 0)
        {
            return;
        }
        var live = Array.IndexOf(_states, StepState.Live);
        _states[live < 0 ? Launcher : live] = StepState.Failed;
    }
}
