using System.Globalization;
using Optima.Core.Models;

namespace Optima.Core.Monitoring;

/// <summary>
/// Per-player context for the presence card, taken from the public profile. The rank fields are
/// optional so a card can still be composed for a player whose ranked data has not loaded yet.
/// </summary>
public sealed record PlayerSeasonBadge(
    string Name,
    long RankedWins,
    long RankedLosses,
    string RankLabel = "",
    int? RankTier = null,
    long? Mmr = null)
{
    /// <summary>"41W-23L", or empty before the season has recorded a match (a "0W-0L" is noise).</summary>
    public string Record => RankedWins == 0 && RankedLosses == 0
        ? string.Empty
        : $"{RankedWins.ToString(CultureInfo.InvariantCulture)}W-{RankedLosses.ToString(CultureInfo.InvariantCulture)}L";
}

/// <summary>Which field Discord renders as the member-list status line for this activity.</summary>
public enum PresenceStatusDisplay
{
    /// <summary>"Playing Project Optima" (the application name).</summary>
    Name,

    /// <summary>"Playing &lt;details&gt;" - what the player is doing.</summary>
    Details,

    /// <summary>"Playing &lt;state&gt;" - the live context line.</summary>
    State,
}

/// <summary>
/// One Discord activity card, fully described: both text lines, the hover captions, the click
/// targets, which rank art the small image should use, and how the status line renders.
///
/// The card is composed of live facts and nothing else (Discord's own guidance: snippets rather
/// than sentences, no repeated information, every field meaningful). It is deliberately not
/// Discord-shaped: the service maps it onto the RPC types, so the card's content stays testable
/// without a Discord client.
/// </summary>
public sealed record PresenceCard
{
    public string Details { get; init; } = string.Empty;

    public string State { get; init; } = string.Empty;

    /// <summary>Click target for the details line, or empty for a plain (unlinked) line.</summary>
    public string DetailsUrl { get; init; } = string.Empty;

    public string LargeImageText { get; init; } = string.Empty;

    /// <summary>Click target for the large image, or empty for an unlinked image.</summary>
    public string LargeImageUrl { get; init; } = string.Empty;

    public string SmallImageText { get; init; } = string.Empty;

    public PresenceStatusDisplay StatusDisplay { get; init; } = PresenceStatusDisplay.Name;

    /// <summary>Ranked tier index, which selects the small-image art; null when there is no rank.</summary>
    public int? RankTier { get; init; }

    /// <summary>
    /// Everything that reaches Discord, so a change to any field re-pushes the card. Built from the
    /// fields rather than written by hand: a hand-kept list is how a new field ends up silently
    /// dropped, which looks exactly like presence having stopped updating.
    /// </summary>
    public string Signature => string.Join(
        '\u001f',
        Details,
        State,
        DetailsUrl,
        LargeImageText,
        LargeImageUrl,
        SmallImageText,
        ((int)StatusDisplay).ToString(CultureInfo.InvariantCulture),
        RankTier?.ToString(CultureInfo.InvariantCulture) ?? string.Empty);
}

/// <summary>
/// Builds the Rich Presence card from the facts Optima actually has. Pure, so the shape of the card
/// stays testable and a change to its wording is a test change rather than a manual Discord check.
/// </summary>
public static class PresenceComposer
{
    // Discord caps details/state around 128 bytes; stay comfortably inside.
    private const int MaxLength = 120;

    private const string GameUrl = "https://criticalopsgame.com/";

    private const string ProjectUrl = "https://github.com/inspectsoftware/Optima";

    /// <summary>The application name Discord shows on line 1, used as the large image's hover text.</summary>
    private const string AppName = "Optima";

    public static PresenceCard Compose(
        GamePresence state,
        PlayerSeasonBadge? player,
        DiscordPresenceOptions? options = null,
        MatchState? match = null)
    {
        // Which facts are allowed out is decided field by field here, so the privacy rule lives with
        // the wording instead of being re-derived by every caller.
        options ??= DiscordPresenceOptions.Standard;

        var name = options.ShowPlayerName ? player?.Name : null;
        var rank = options.ShowRank ? player?.RankLabel : null;
        var record = options.ShowRankedRecord ? player?.Record : null;

        var card = new PresenceCard
        {
            LargeImageText = AppName,
            LargeImageUrl = ProjectUrl,
            SmallImageText = SmallCaption(player, options),
            StatusDisplay = options.StatusDisplay,
            RankTier = EmblemTier(player, options),
        };

        // Discord puts "Playing" in front of the status line itself. When that line is the details,
        // the card says "Critical Ops" so the member list reads "Playing Critical Ops", and the idle
        // and launching cards fall back to the app name rather than "Playing Idling...".
        var statusIsDetails = options.StatusDisplay == PresenceStatusDisplay.Details;
        var game = statusIsDetails ? "Critical Ops" : "Playing Critical Ops";
        var notPlaying = statusIsDetails ? PresenceStatusDisplay.Name : options.StatusDisplay;

        return state switch
        {
            GamePresence.InGame => card with
            {
                // In a match the map and the playlist follow the game.
                Details = match?.Phase == MatchPhase.InMatch
                    ? Join(game, ModeLabel(match.Mode), match.Map)
                    : game,
                State = match?.Phase == MatchPhase.InMenu
                    ? Join("In menu", name, rank, record)
                    : Join(name, rank, record),
                DetailsUrl = GameUrl,
            },
            GamePresence.Starting => card with
            {
                Details = "Launching", State = Join(name, rank), DetailsUrl = GameUrl, StatusDisplay = notPlaying,
            },
            _ => card with { Details = "Idling...", State = Join(name, rank), StatusDisplay = notPlaying },
        };
    }

    /// <summary>The playlist shown before the map, once the match mode is known.</summary>
    private static string? ModeLabel(MatchMode mode) => mode switch
    {
        MatchMode.Ranked => "Ranked",
        // Casual covers TDM, casual defuse and custom: the game's log does not tell them apart.
        MatchMode.Casual => "Casual",
        _ => null,
    };

    /// <summary>The small image's tier, or null when the chooser keeps the emblem off.</summary>
    private static int? EmblemTier(PlayerSeasonBadge? player, DiscordPresenceOptions options)
        => options.ShowRankEmblem ? player?.RankTier : null;

    /// <summary>The caption belongs to the emblem, so it follows the same switch.</summary>
    private static string SmallCaption(PlayerSeasonBadge? player, DiscordPresenceOptions options)
        => options.ShowRankEmblem ? RankCaption(player, options) : string.Empty;

    /// <summary>The small image's hover caption: the rank, and (when chosen) the rating.</summary>
    private static string RankCaption(PlayerSeasonBadge? player, DiscordPresenceOptions options)
    {
        if (player is null)
        {
            return string.Empty;
        }

        var label = player.RankLabel;
        if (options.ShowRankedRating && player.Mmr is > 0)
        {
            var mmr = player.Mmr.Value.ToString(CultureInfo.InvariantCulture) + " MMR";
            label = label.Length == 0 ? mmr : label + " · " + mmr;
        }
        return Truncate(label);
    }

    /// <summary>Joins the non-empty parts with the same separator the rest of the card uses.</summary>
    private static string Join(params string?[] parts)
        => Truncate(string.Join(" · ", parts.Where(p => !string.IsNullOrWhiteSpace(p))));

    private static string Truncate(string text)
        => text.Length <= MaxLength ? text : text[..(MaxLength - 1)] + "…";
}
