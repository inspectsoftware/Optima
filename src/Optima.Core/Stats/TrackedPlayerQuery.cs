using System.Globalization;
using Optima.Core.Models;

namespace Optima.Core.Stats;

/// <summary>What one press of Track decided.</summary>
public enum TrackOutcome
{
    /// <summary>The box was empty; nothing was looked up and nothing changes.</summary>
    Empty,

    /// <summary>The API answered with a profile; its own name and id become the tracked player.</summary>
    Found,

    /// <summary>The API answered that there is no such player; the tracked player stays as it was.</summary>
    NotFound,

    /// <summary>The API could not be reached; what was typed becomes the tracked player as typed.</summary>
    Unreachable,
}

/// <summary>The outcome of a Track press and the identity it stores, when it stores one.</summary>
public sealed record TrackDecision(TrackOutcome Outcome, string Ign, long? AccountId)
{
    /// <summary>Whether this press changes Optima's tracked player.</summary>
    public bool Tracks => Outcome is TrackOutcome.Found or TrackOutcome.Unreachable;
}

/// <summary>
/// The PLAYER card in Settings: one box that takes an in-game name or an account id, and the
/// choice of what gets stored once it has been looked up. Kept here rather than in the view model
/// so the reading of the box and that choice can be tested.
/// </summary>
public static class TrackedPlayerQuery
{
    /// <summary>The typed text as an account id when it is digits only, otherwise null.</summary>
    public static long? AsAccountId(string? typed)
    {
        var text = typed?.Trim() ?? string.Empty;
        return text.Length > 0
            && text.All(char.IsAsciiDigit)
            && long.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var id)
            && id > 0
                ? id
                : null;
    }

    /// <summary>
    /// Looks the box up. Digits are tried as an account id first and, when no account has that id,
    /// as a name too: nothing stops a player from calling themselves 1337.
    /// </summary>
    public static Task<CopsLookupResult> LookupAsync(CopsApiClient api, string typed, CancellationToken ct = default)
        => api.LookupPlayerAsync(typed.Trim(), AsAccountId(typed), ct);

    /// <summary>
    /// What a lookup means for the tracked player. A profile that answered is stored by its own name
    /// and id, so a name typed in the wrong case or an id typed for a renamed player stores what the
    /// game has. With the API unreachable the typed text is kept as it is (digits as the id, anything
    /// else as the name), so the player is still set and the stats fill in once the API answers.
    /// </summary>
    public static TrackDecision Decide(string typed, CopsLookupResult lookup)
    {
        var text = typed.Trim();
        if (text.Length == 0)
        {
            return new TrackDecision(TrackOutcome.Empty, string.Empty, null);
        }

        if (lookup.IsFound)
        {
            var profile = lookup.Profile!;
            return new TrackDecision(TrackOutcome.Found, profile.Name, profile.UserId > 0 ? profile.UserId : AsAccountId(text));
        }

        if (lookup.Status == CopsLookupStatus.Error)
        {
            var id = AsAccountId(text);
            return new TrackDecision(TrackOutcome.Unreachable, id is null ? text : string.Empty, id);
        }

        return new TrackDecision(TrackOutcome.NotFound, string.Empty, null);
    }

    /// <summary>
    /// The account id to keep when only a name is typed, as the setup wizard asks for it. The stored id
    /// belongs to the stored name and every lookup tries the id first, so a different name drops it:
    /// otherwise the old player went on being tracked by id under the new name.
    /// </summary>
    public static long? IdForName(string storedIgn, long? storedId, string typedIgn)
        => string.Equals(storedIgn.Trim(), typedIgn.Trim(), StringComparison.OrdinalIgnoreCase) ? storedId : null;

    /// <summary>The saved list with this identity in it: an entry for the same player is replaced, not doubled.</summary>
    public static IReadOnlyList<PlayerAccount> WithSaved(IReadOnlyList<PlayerAccount> saved, string ign, long? accountId)
    {
        var account = new PlayerAccount { Ign = ign.Trim(), AccountId = accountId };
        var list = saved.ToList();
        var existing = list.FindIndex(a => a.Matches(account.Ign, accountId));
        if (existing >= 0)
        {
            // The key stays, so the title-bar switcher's row is the same row; the label is kept too.
            list[existing] = account with { Key = list[existing].Key, Label = list[existing].Label };
        }
        else
        {
            list.Add(account);
        }
        return list;
    }

    /// <summary>The line under the name on the result card, such as "lv 61 · Gold 2 · ranked 214W-188L · K/D 1.18".</summary>
    public static string Summary(CopsPlayerProfile profile)
    {
        var parts = new List<string>();
        if (profile.Level > 0)
        {
            parts.Add("lv " + profile.Level.ToString(CultureInfo.InvariantCulture));
        }

        var rank = CopsRankLadder.Label(profile.Rank, profile.Mmr);
        if (rank.Length > 0)
        {
            parts.Add(rank);
        }

        if (profile.CurrentSeason?.Ranked is { } ranked && !ranked.IsZero)
        {
            parts.Add($"ranked {ranked.Wins}W-{ranked.Losses}L");
            parts.Add("K/D " + (ranked.Deaths == 0
                ? ranked.Kills.ToString(CultureInfo.InvariantCulture)
                : (ranked.Kills / (double)ranked.Deaths).ToString("F2", CultureInfo.InvariantCulture)));
        }

        return string.Join(" · ", parts);
    }
}
