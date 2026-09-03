namespace Optima.Core.Models;

/// <summary>One Critical Ops match in history.</summary>
public sealed record MatchRecord
{
    public long Id { get; init; }

    public long? SessionId { get; init; }

    public DateTimeOffset StartedAt { get; init; }

    public required string Mode { get; init; }

    public required string Result { get; init; }

    public long? Kills { get; init; }
    public long? Deaths { get; init; }
    public long? Assists { get; init; }
    public string? Map { get; init; }

    public string Source { get; init; } = "manual";

    public string? Note { get; init; }
}
