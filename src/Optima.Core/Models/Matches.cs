namespace Optima.Core.Models;

/// <summary>
/// One Critical Ops match in history. Auto rows come from public-profile deltas around a
/// session (only when the delta contains exactly one match, so numbers are attributable);
/// manual rows are user-entered and always editable, which is the feature's honesty floor.
/// </summary>
public sealed record MatchRecord
{
    public long Id { get; init; }

    /// <summary>Session the match belongs to; null when no Optima session was recorded around it.</summary>
    public long? SessionId { get; init; }

    public DateTimeOffset StartedAt { get; init; }

    /// <summary>"ranked", "casual" or "custom".</summary>
    public required string Mode { get; init; }

    /// <summary>"win", "loss" or "unknown".</summary>
    public required string Result { get; init; }

    public long? Kills { get; init; }
    public long? Deaths { get; init; }
    public long? Assists { get; init; }
    public string? Map { get; init; }

    /// <summary>"auto" (API delta) or "manual"; edits mark rows "edited".</summary>
    public string Source { get; init; } = "manual";

    public string? Note { get; init; }
}
