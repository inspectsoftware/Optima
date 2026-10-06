namespace Optima.Core.Health;

/// <summary>How much a repair asks of the player, which is what decides whether it may run unasked.</summary>
public enum RepairTier
{
    /// <summary>Reversible, needs no administrator rights, interrupts nothing.</summary>
    Safe,

    /// <summary>Stops or restarts something the player can see, such as Google Play Games.</summary>
    Disruptive,

    /// <summary>Needs the elevated helper, so it can put an administrator prompt on the screen.</summary>
    Elevated,
}

public enum RepairOutcome
{
    Fixed,

    /// <summary>There turned out to be nothing to do: the thing to repair was already as it should be.</summary>
    NotNeeded,
    Failed,

    /// <summary>The repair did its part and the rest is the player's: a restart, a prompt that was declined.</summary>
    NeedsUser,
}

public sealed record RepairResult(RepairOutcome Outcome, string Summary);

/// <summary>Who asked for a repair. The policy only governs the ones nobody asked for.</summary>
public enum RepairTrigger
{
    Background,
    User,
}

/// <summary>How far Optima goes on its own.</summary>
public enum AutoRepairMode
{
    /// <summary>Optima lists issues and repairs nothing until asked.</summary>
    Off,

    /// <summary>Safe repairs run unasked; anything that interrupts or needs administrator rights waits for a click.</summary>
    SafeOnly,

    /// <summary>Safe repairs first; where they do not help, Optima goes on to the disruptive and elevated ones itself.</summary>
    Escalate,
}

/// <summary>One repair that was run, kept across restarts: the policy counts these, and the page shows them.</summary>
public sealed record RepairAttempt(
    string IssueKey, string RepairId, RepairTier Tier, RepairTrigger Trigger, DateTimeOffset At, RepairOutcome Outcome, string Summary);

/// <summary>
/// One thing Optima can do about an issue. It says what it changes before it changes it, because
/// the same text is the button's tooltip and the line in the history.
/// </summary>
public interface IRepairAction
{
    string Id { get; }

    /// <summary>The button: what pressing it does, in a few words.</summary>
    string Title { get; }

    /// <summary>What it changes on the PC, and what it costs the player.</summary>
    string Changes { get; }

    RepairTier Tier { get; }

    Task<RepairResult> RunAsync(Issue issue, CancellationToken ct = default);
}

/// <summary>
/// Which repairs belong to which issue. <see cref="Ladder"/> is what Optima may try on its own, in
/// order, each rung only after the one before it did not help. <see cref="ByHandOnly"/> are
/// offered as buttons and never run unasked: a repair that makes a choice for the player (restore
/// the backup, or keep the file?) is not Optima's to make.
/// </summary>
public sealed record RepairPlan(IReadOnlyList<string> Ladder, IReadOnlyList<string> ByHandOnly, string? VerifyCheck = null)
{
    public static RepairPlan None { get; } = new([], []);

    public IEnumerable<string> All => Ladder.Concat(ByHandOnly);
}

public static class RepairCatalog
{
    private static readonly Dictionary<string, RepairPlan> Plans = new(StringComparer.OrdinalIgnoreCase)
    {
        // Every launch strategy failed: the platform is usually not running, or is stuck.
        ["LAUNCH_FAILED"] = new(["start-platform", "restart-platform"], []),
        // The platform opened and the game never appeared. It is often sitting on a sign-in or an
        // update, so stopping it is the player's call, never a rung.
        ["GAME_START_TIMEOUT"] = new(["start-platform"], ["restart-platform"]),
        ["RESTORE_STEP_FAILED"] = new(["retry-restore"], []),
        ["HYPERVISOR_OFF"] = new([], ["enable-hypervisor"], VerifyCheck: "Windows Hypervisor"),
        ["VDD_RESTORE_PENDING"] = new([], ["discard-display-restore"], VerifyCheck: "Pending Display Restore"),
    };

    public static RepairPlan For(string code) => Plans.TryGetValue(code, out var plan) ? plan : RepairPlan.None;

    /// <summary>Every repair id a plan names, for the test that holds the registrations to them.</summary>
    public static IReadOnlyList<string> RepairIds { get; } = [.. Plans.Values.SelectMany(p => p.All).Distinct()];

    public static IReadOnlyList<string> Codes { get; } = [.. Plans.Keys];
}
