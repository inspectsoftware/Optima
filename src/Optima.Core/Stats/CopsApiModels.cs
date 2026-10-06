using System.Text.Json;

namespace Optima.Core.Stats;

/// <summary>Kills/deaths/assists/wins/losses for one game mode, as the public API reports them.</summary>
public sealed record CopsModeStats(long Kills, long Deaths, long Assists, long Wins, long Losses)
{
    public static readonly CopsModeStats Zero = new(0, 0, 0, 0, 0);

    public long MatchesCounted => Wins + Losses;

    public static CopsModeStats DeltaOf(CopsModeStats before, CopsModeStats after) => new(
        Math.Max(0, after.Kills - before.Kills),
        Math.Max(0, after.Deaths - before.Deaths),
        Math.Max(0, after.Assists - before.Assists),
        Math.Max(0, after.Wins - before.Wins),
        Math.Max(0, after.Losses - before.Losses));

    public bool IsZero => Kills == 0 && Deaths == 0 && Assists == 0 && Wins == 0 && Losses == 0;
}

/// <summary>One season row of the profile's stats.</summary>
public sealed record CopsSeasonStats(int Season, CopsModeStats Ranked, CopsModeStats Casual, CopsModeStats Custom);

/// <summary>The slice of the public profile Optima cares about.</summary>
public sealed record CopsPlayerProfile(
    long UserId,
    string Name,
    int Level,
    IReadOnlyList<CopsSeasonStats> Seasons,
    string? ClanName = null,
    string? ClanTag = null,
    int? Rank = null,
    long? Mmr = null,
    int? HighestRank = null,
    long? LeaderboardPosition = null,
    long? CurrentXp = null,
    long? NextLevelXp = null,
    int? ClanMemberRank = null,
    DateTimeOffset? ClanJoinedAt = null,
    bool Banned = false,
    string? BanDetail = null)
{
    public CopsSeasonStats? CurrentSeason => Seasons.Count == 0 ? null : Seasons.MaxBy(s => s.Season);

    /// <summary>Level progress as a 0..1 fraction, or null when the API did not report the span.</summary>
    public double? LevelProgress => CurrentXp is { } xp && NextLevelXp is > 0
        ? Math.Clamp((double)xp / NextLevelXp.Value, 0, 1)
        : null;
}

/// <summary>Per-mode deltas between two profile snapshots of the same player.</summary>
public sealed record CopsProfileDelta(int Season, CopsModeStats Ranked, CopsModeStats Casual, CopsModeStats Custom)
{
    public bool IsZero => Ranked.IsZero && Casual.IsZero && Custom.IsZero;

    public static CopsProfileDelta? Between(CopsPlayerProfile? before, CopsPlayerProfile? after)
    {
        var currentAfter = after?.CurrentSeason;
        if (currentAfter is null)
        {
            return null;
        }

        var baseline = before?.Seasons.FirstOrDefault(s => s.Season == currentAfter.Season);
        var rankedBase = baseline?.Ranked ?? CopsModeStats.Zero;
        var casualBase = baseline?.Casual ?? CopsModeStats.Zero;
        var customBase = baseline?.Custom ?? CopsModeStats.Zero;

        return new CopsProfileDelta(
            currentAfter.Season,
            CopsModeStats.DeltaOf(rankedBase, currentAfter.Ranked),
            CopsModeStats.DeltaOf(casualBase, currentAfter.Casual),
            CopsModeStats.DeltaOf(customBase, currentAfter.Custom));
    }
}

/// <summary>Tolerant parser for the public profile endpoint.</summary>
public static class CopsProfileParser
{
    public static CopsPlayerProfile? Parse(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            if (root.ValueKind == JsonValueKind.Array)
            {
                if (root.GetArrayLength() == 0)
                {
                    return null;
                }
                root = root[0];
            }
            return ParseElement(root);
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException)
        {
            // InvalidOperationException: a node that should be an object arrived as null or a scalar.
            return null;
        }
    }

    private static CopsPlayerProfile? ParseElement(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        long userId = 0;
        var name = "";
        var level = 0;
        if (root.TryGetProperty("basicInfo", out var basic))
        {
            if (basic.TryGetProperty("userID", out var id) && id.TryGetInt64(out var idValue))
            {
                userId = idValue;
            }
            if (basic.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String)
            {
                name = n.GetString() ?? "";
            }
            if (basic.TryGetProperty("playerLevel", out var pl)
                && pl.TryGetProperty("level", out var lv) && lv.TryGetInt32(out var lvValue))
            {
                level = lvValue;
            }
        }

        long? currentXp = null;
        long? nextLevelXp = null;
        if (root.TryGetProperty("basicInfo", out var basicXp)
            && basicXp.TryGetProperty("playerLevel", out var levelInfo) && levelInfo.ValueKind == JsonValueKind.Object)
        {
            if (levelInfo.TryGetProperty("current_xp", out var cx) && cx.TryGetInt64(out var cxv))
            {
                currentXp = cxv;
            }
            if (levelInfo.TryGetProperty("next_level_xp", out var nx) && nx.TryGetInt64(out var nxv))
            {
                nextLevelXp = nxv;
            }
        }

        var seasons = new List<CopsSeasonStats>();
        if (root.TryGetProperty("stats", out var stats)
            && stats.TryGetProperty("seasonal_stats", out var seasonal)
            && seasonal.ValueKind == JsonValueKind.Array)
        {
            foreach (var row in seasonal.EnumerateArray())
            {
                var season = row.TryGetProperty("season", out var s) && s.TryGetInt32(out var sv) ? sv : 0;
                seasons.Add(new CopsSeasonStats(
                    season,
                    ReadMode(row, "ranked"),
                    ReadMode(row, "casual"),
                    ReadMode(row, "custom")));
            }
        }

        // Ranked ladder facts live next to the seasonal rows.
        int? rank = null;
        long? mmr = null;
        int? highestRank = null;
        long? leaderboardPosition = null;
        if (root.TryGetProperty("stats", out var statsRoot))
        {
            if (statsRoot.TryGetProperty("ranked", out var ranked)
                && ranked.ValueKind == JsonValueKind.Object)
            {
                if (ranked.TryGetProperty("rank", out var r) && r.TryGetInt32(out var rv))
                {
                    rank = rv;
                }
                if (ranked.TryGetProperty("mmr", out var m) && m.TryGetInt64(out var mv))
                {
                    mmr = mv;
                }
                if (ranked.TryGetProperty("highest_rank", out var hr) && hr.TryGetInt32(out var hrv))
                {
                    highestRank = hrv;
                }
            }
            if (statsRoot.TryGetProperty("leaderboard_data", out var lb)
                && lb.ValueKind == JsonValueKind.Object
                && lb.TryGetProperty("position", out var pos)
                && pos.TryGetInt64(out var posv))
            {
                leaderboardPosition = posv;
            }
        }

        // Clan membership travels with the profile when the player is in one.
        string? clanName = null;
        string? clanTag = null;
        int? clanMemberRank = null;
        DateTimeOffset? clanJoinedAt = null;
        if (root.TryGetProperty("clan", out var clan)
            && clan.ValueKind == JsonValueKind.Object
            && clan.TryGetProperty("basicInfo", out var clanBasic)
            && clanBasic.ValueKind == JsonValueKind.Object)
        {
            if (clanBasic.TryGetProperty("name", out var cn) && cn.ValueKind == JsonValueKind.String)
            {
                clanName = cn.GetString();
            }
            if (clanBasic.TryGetProperty("tag", out var ct) && ct.ValueKind == JsonValueKind.String)
            {
                clanTag = ct.GetString();
            }
            if (clan.TryGetProperty("memberRank", out var mr) && mr.TryGetInt32(out var mrv))
            {
                clanMemberRank = mrv;
            }
            // The API writes the join time without a timezone, in game-server local terms; treated
            // as UTC it is still the right day, which is all the card claims.
            if (clan.TryGetProperty("joined", out var joined) && joined.ValueKind == JsonValueKind.String
                && DateTimeOffset.TryParse(joined.GetString(), System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.AssumeUniversal, out var joinedAt))
            {
                clanJoinedAt = joinedAt;
            }
        }

        // Standing. The trap here is that a ban object is also what the API returns for an account
        // in clean standing (Type 0, Reason 2, SecondsLeft 0 is a real response for an ordinary
        // player), so a ban needs positive evidence rather than the presence of the field: either a
        // clock still running or an explicit active flag. A shape with none of those keys at all is
        // treated as a ban, because there presence is the only thing to go on.
        var banned = false;
        string? banDetail = null;
        if (root.TryGetProperty("ban", out var ban) && ban.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined))
        {
            if (ban.ValueKind == JsonValueKind.String)
            {
                banned = true;
                banDetail = ban.GetString();
            }
            else if (ban.ValueKind == JsonValueKind.Object)
            {
                var secondsLeft = ReadLongAny(ban, "SecondsLeft", "seconds_left", "secondsLeft");
                var active = ReadBoolAny(ban, "Active", "active", "IsBanned", "is_banned");
                var known = ban.TryGetProperty("Type", out _) || ban.TryGetProperty("type", out _)
                    || secondsLeft is not null || active is not null
                    || ban.TryGetProperty("Reason", out _) || ban.TryGetProperty("reason", out _);

                banned = active == true || secondsLeft is > 0 || !known;
                if (banned)
                {
                    var reason = ReadStringAny(ban, "ReasonName", "reason_name", "detail");
                    banDetail = reason
                        ?? (secondsLeft is > 0 ? FormatRemaining(secondsLeft.Value) + " left" : null);
                }
            }
        }

        return name.Length == 0 && userId == 0 && seasons.Count == 0
            ? null
            : new CopsPlayerProfile(
                userId, name, level, seasons, clanName, clanTag, rank, mmr, highestRank, leaderboardPosition,
                currentXp, nextLevelXp, clanMemberRank, clanJoinedAt, banned, banDetail);
    }

    private static CopsModeStats ReadMode(JsonElement seasonRow, string mode)
    {
        if (!seasonRow.TryGetProperty(mode, out var m) || m.ValueKind != JsonValueKind.Object)
        {
            return CopsModeStats.Zero;
        }
        return new CopsModeStats(ReadLong(m, "k"), ReadLong(m, "d"), ReadLong(m, "a"), ReadLong(m, "w"), ReadLong(m, "l"));
    }

    private static long ReadLong(JsonElement obj, string property)
        => obj.TryGetProperty(property, out var v) && v.TryGetInt64(out var value) ? value : 0;

    /// <summary>Reads a number under any of several spellings, or null when none of them is there.</summary>
    private static long? ReadLongAny(JsonElement obj, params string[] properties)
    {
        foreach (var property in properties)
        {
            if (obj.TryGetProperty(property, out var v) && v.TryGetInt64(out var value))
            {
                return value;
            }
        }
        return null;
    }

    private static bool? ReadBoolAny(JsonElement obj, params string[] properties)
    {
        foreach (var property in properties)
        {
            if (obj.TryGetProperty(property, out var v) && v.ValueKind is JsonValueKind.True or JsonValueKind.False)
            {
                return v.GetBoolean();
            }
        }
        return null;
    }

    private static string? ReadStringAny(JsonElement obj, params string[] properties)
    {
        foreach (var property in properties)
        {
            if (obj.TryGetProperty(property, out var v) && v.ValueKind == JsonValueKind.String
                && v.GetString() is { Length: > 0 } text)
            {
                return text;
            }
        }
        return null;
    }

    /// <summary>A ban's remaining time in the units a person would say out loud.</summary>
    private static string FormatRemaining(long seconds)
        => seconds >= 86400 ? $"{seconds / 86400}d" : seconds >= 3600 ? $"{seconds / 3600}h" : $"{seconds / 60}m";
}
