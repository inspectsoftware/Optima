namespace Optima.Core.Health;

/// <summary>A rung of an issue's ladder: which repair, and what it asks of the player.</summary>
public sealed record RepairStep(string Id, RepairTier Tier);

/// <summary>Everything the decision depends on, so that the decision is a function of it and nothing else.</summary>
public sealed record RepairRequest
{
    public required IReadOnlyList<RepairStep> Ladder { get; init; }

    /// <summary>The repairs already run for this issue, in this run and earlier ones.</summary>
    public IReadOnlyList<RepairAttempt> History { get; init; } = [];

    public required AutoRepairMode Mode { get; init; }

    public required DateTimeOffset Now { get; init; }

    /// <summary>A session is running, or the game is, however it was started.</summary>
    public bool GameRunning { get; init; }

    /// <summary>Optima's window is on screen: the player would see what a repair does and any prompt it raises.</summary>
    public bool WindowVisible { get; init; }

    /// <summary>The elevated helper is already running, so an elevated repair raises no prompt.</summary>
    public bool HelperConnected { get; init; }

    public bool ElevationDeclinedThisRun { get; init; }

    public int AutomaticRepairsLastHour { get; init; }

    public DateTimeOffset? LastDisruptiveAt { get; init; }
}

public enum RepairVerdict
{
    /// <summary>Run the step now, unasked.</summary>
    Run,

    /// <summary>Not now; the reason says what it is waiting for. Asked again when things change.</summary>
    Defer,

    /// <summary>The step is the right one and is the player's to start.</summary>
    AskUser,

    /// <summary>Nothing more for Optima to do about this issue by itself.</summary>
    Stop,
}

public sealed record RepairDecision(RepairVerdict Verdict, RepairStep? Step, string Reason);

/// <summary>
/// Whether Optima may run a repair that nobody asked for. Pure, so every rule is a test.
///
/// The ladder is climbed one rung at a time, and a rung is only reached when the ones below it
/// were tried and the issue is still there. What bounds it: nothing runs while a game does; a
/// repair that interrupts needs the window on screen; an administrator prompt appears at most once
/// per issue per day, never over a hidden window, and a prompt that was declined is not shown
/// again until the player asks for it.
/// </summary>
public static class RepairPolicy
{
    /// <summary>Attempts older than this no longer count against a rung: the problem may be a new one.</summary>
    public static readonly TimeSpan AttemptWindow = TimeSpan.FromHours(24);

    public static readonly TimeSpan SafeRetryAfter = TimeSpan.FromSeconds(60);

    public static readonly TimeSpan DisruptiveSpacing = TimeSpan.FromMinutes(10);

    public const int AutomaticRepairsPerHour = 6;

    public static RepairDecision Decide(RepairRequest request)
    {
        if (request.Mode == AutoRepairMode.Off)
        {
            return Stop("automatic repair is off");
        }
        if (request.Ladder.Count == 0)
        {
            return Stop("there is no repair Optima can run for this by itself");
        }

        var recent = request.History
            .Where(a => a.Trigger == RepairTrigger.Background && request.Now - a.At < AttemptWindow)
            .ToList();
        if (recent.Any(a => a.Outcome == RepairOutcome.NeedsUser))
        {
            return Stop("the last repair left a step that is yours");
        }

        if (request.GameRunning)
        {
            return Defer("waiting for the game to close: nothing is repaired while it runs");
        }
        if (request.AutomaticRepairsLastHour >= AutomaticRepairsPerHour)
        {
            return Defer("six repairs ran in the last hour; waiting before trying more");
        }

        foreach (var step in request.Ladder)
        {
            var tried = recent.Where(a => a.RepairId == step.Id).ToList();
            var allowed = step.Tier == RepairTier.Safe ? 2 : 1;
            if (tried.Count >= allowed)
            {
                continue;
            }
            if (tried.Count > 0 && request.Now - tried.Max(a => a.At) < SafeRetryAfter)
            {
                return Defer("trying the same repair once more in a minute");
            }
            return Gate(step, request, recent);
        }
        return Stop("every repair Optima can run by itself was tried");
    }

    private static RepairDecision Gate(RepairStep step, RepairRequest request, IReadOnlyList<RepairAttempt> recent)
    {
        if (step.Tier == RepairTier.Safe)
        {
            return new RepairDecision(RepairVerdict.Run, step, "safe to run unasked");
        }
        if (request.Mode != AutoRepairMode.Escalate)
        {
            return Ask(step, step.Tier == RepairTier.Elevated
                ? "needs administrator rights, so it waits for you"
                : "interrupts what is running, so it waits for you");
        }

        // An elevated repair with the helper already running raises no prompt: it is only disruptive.
        if (step.Tier == RepairTier.Elevated && !request.HelperConnected)
        {
            if (request.ElevationDeclinedThisRun)
            {
                return Ask(step, "an administrator prompt was declined; it is not shown again until you ask");
            }
            if (recent.Any(a => a.Tier == RepairTier.Elevated))
            {
                return Ask(step, "an administrator prompt was already shown for this today");
            }
            if (!request.WindowVisible)
            {
                return Defer("needs an administrator prompt, which is not raised over a hidden window");
            }
            return new RepairDecision(RepairVerdict.Run, step, "the safe repairs did not help; this one needs administrator rights");
        }

        if (!request.WindowVisible)
        {
            return Defer("waiting for the window: a repair that interrupts is not run out of sight");
        }
        if (request.LastDisruptiveAt is { } last && request.Now - last < DisruptiveSpacing)
        {
            return Defer("another interrupting repair ran a moment ago");
        }
        return new RepairDecision(RepairVerdict.Run, step, "the safe repairs did not help");
    }

    private static RepairDecision Stop(string reason) => new(RepairVerdict.Stop, null, reason);

    private static RepairDecision Defer(string reason) => new(RepairVerdict.Defer, null, reason);

    private static RepairDecision Ask(RepairStep step, string reason) => new(RepairVerdict.AskUser, step, reason);
}
