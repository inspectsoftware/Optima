namespace Optima.Core.Health;

public enum IssueSeverity
{
    /// <summary>Worth knowing, nothing to do. Listed, never counted on the rail.</summary>
    Note,
    Warning,
    Error,
    Critical,
}

public enum IssueState
{
    Open,

    /// <summary>A repair is running for it right now.</summary>
    Repairing,

    /// <summary>A repair ran and, where there is a check to prove it, the check passes. Kept on the list so what was done can be seen.</summary>
    Repaired,

    /// <summary>The next step is the player's: a repair that waits for a click, a restart, a prompt that was declined.</summary>
    NeedsUser,
}

/// <summary>
/// One thing that is wrong, however often it happened. A failing call that is retried every ten
/// seconds writes a hundred log lines and is still one problem; the issue is that one problem,
/// with its first occurrence, its latest ones, and what the log said on the way in.
/// </summary>
public sealed record Issue
{
    /// <summary>What makes two occurrences the same issue. Stable across runs, so an ignore holds.</summary>
    public required string Key { get; init; }

    /// <summary>The error guide's code for it. Every issue has one, so every issue has an explanation.</summary>
    public required string Code { get; init; }

    public required IssueSeverity Severity { get; init; }

    public required string Title { get; init; }

    /// <summary>What this occurrence said, as opposed to what the guide says about the code in general.</summary>
    public string Detail { get; init; } = string.Empty;

    public required DateTimeOffset FirstSeen { get; init; }

    public required DateTimeOffset LastSeen { get; init; }

    /// <summary>How many times the log reported it. An issue a check raised stays at one.</summary>
    public int Count { get; init; } = 1;

    /// <summary>The first occurrence and the most recent few. Empty for an issue a check raised.</summary>
    public IReadOnlyList<LogRecord> Evidence { get; init; } = [];

    /// <summary>What the log said just before the first occurrence.</summary>
    public IReadOnlyList<LogRecord> LeadUp { get; init; } = [];

    public IssueState State { get; init; } = IssueState.Open;

    /// <summary>What was last done about it or what it is waiting for, in a sentence.</summary>
    public string RepairNote { get; init; } = string.Empty;

    /// <summary>Counted on the rail: more than a note, and not already repaired.</summary>
    public bool NeedsAttention => Severity >= IssueSeverity.Warning && State != IssueState.Repaired;
}
