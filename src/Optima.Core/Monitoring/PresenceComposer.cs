using Optima.Core.Monitoring;

namespace Optima.Core.Monitoring;

/// <summary>Per-player context for presence text, taken from the public profile.</summary>
public sealed record PlayerSeasonBadge(string Name, long RankedWins, long RankedLosses);

/// <summary>One presence string set, already Discord-shaped: short, single-line, no repetition.</summary>
public sealed record PresenceText(string Details, string State, string LargeImageText)
{
    public static readonly PresenceText Empty = new("", "", "");
}

/// <summary>
/// Builds the Rich Presence strings from the facts Optima actually has (per Discord's best
/// practices: snippets, not sentences; no repeated information; every field meaningful).
/// Pure so the shape of the card stays testable.
/// </summary>
public static class PresenceComposer
{
    // Discord caps details/state around 128 bytes; stay comfortably inside.
    private const int MaxLength = 120;

    public static PresenceText Compose(
        GamePresence state,
        double? fps,
        PlayerSeasonBadge? player,
        string? profileName)
    {
        return state switch
        {
            GamePresence.InGame => ComposeInGame(fps, player),
            GamePresence.Starting => ComposeStarting(profileName),
            _ => new PresenceText("Optima Launcher", "Browsing the launcher", "Optima"),
        };
    }

    private static PresenceText ComposeInGame(double? fps, PlayerSeasonBadge? player)
    {
        var details = "Critical Ops";
        if (player is not null)
        {
            details += $" · {player.RankedWins}W-{player.RankedLosses}L";
        }

        string state;
        if (fps is { } value && player is not null)
        {
            state = $"{player.Name} · {value:F0} fps";
        }
        else if (fps is { } live)
        {
            state = $"{live:F0} fps";
        }
        else
        {
            state = player?.Name ?? "";
        }

        return new PresenceText(Truncate(details), Truncate(state), "Critical Ops session");
    }

    private static PresenceText ComposeStarting(string? profileName)
    {
        var state = string.IsNullOrWhiteSpace(profileName) ? "" : "applying " + profileName.Trim();
        return new PresenceText("Launching Critical Ops", Truncate(state), "Optima");
    }

    private static string Truncate(string text)
        => text.Length <= MaxLength ? text : text[..(MaxLength - 1)] + "…";
}
