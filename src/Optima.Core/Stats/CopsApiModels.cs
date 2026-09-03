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
    IReadOnlyList<CopsSeasonStats> Seasons)
{
    public CopsSeasonStats? CurrentSeason => Seasons.Count == 0 ? null : Seasons.MaxBy(s => s.Season);
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

            return name.Length == 0 && userId == 0 && seasons.Count == 0
                ? null
                : new CopsPlayerProfile(userId, name, level, seasons);
        }
        catch (JsonException)
        {
            return null;
        }
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
}
