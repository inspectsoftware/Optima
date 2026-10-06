using Optima.Core.Monitoring;

namespace Optima.Core.Models;

/// <summary>
/// Exactly which facts the Discord card carries, chosen field by field on the presence chooser.
/// The card is visible to every friend, so each switch here is a privacy choice as much as a
/// cosmetic one. The defaults describe the old Standard card, so a configuration that never
/// opened the chooser keeps broadcasting what it always did.
/// </summary>
public sealed record DiscordPresenceOptions
{
    /// <summary>Your in-game name, on the card's live line.</summary>
    public bool ShowPlayerName { get; init; } = true;

    /// <summary>The ranked tier and division, beside the game on the details line.</summary>
    public bool ShowRank { get; init; } = true;

    /// <summary>The rank emblem as the card's small image.</summary>
    public bool ShowRankEmblem { get; init; } = true;

    /// <summary>The ranked win-loss record.</summary>
    public bool ShowRankedRecord { get; init; } = true;

    /// <summary>The ranked rating (MMR), added to the rank's hover caption.</summary>
    public bool ShowRankedRating { get; init; }

    /// <summary>The live frame rate while the game runs.</summary>
    public bool ShowFps { get; init; } = true;

    /// <summary>The session timer, which also gives Discord its elapsed counter.</summary>
    public bool ShowElapsedTime { get; init; } = true;

    /// <summary>Which line Discord renders as the status above your name.</summary>
    public PresenceStatusDisplay StatusDisplay { get; init; } = PresenceStatusDisplay.Details;

    /// <summary>The game and the fps: no name, no rank, no record.</summary>
    public static DiscordPresenceOptions Minimal { get; } = new()
    {
        ShowPlayerName = false,
        ShowRank = false,
        ShowRankEmblem = false,
        ShowRankedRecord = false,
        ShowRankedRating = false,
        ShowFps = true,
        ShowElapsedTime = true,
    };

    /// <summary>Name, rank and record beside the live fps: what most players want.</summary>
    public static DiscordPresenceOptions Standard { get; } = new();

    /// <summary>Standard plus the ranked rating.</summary>
    public static DiscordPresenceOptions Full { get; } = new() { ShowRankedRating = true };
}
