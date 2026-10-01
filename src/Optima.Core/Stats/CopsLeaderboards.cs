using System.Text.Json;

namespace Optima.Core.Stats;

/// <summary>One row of a player leaderboard (elite / ranked / casual kills).</summary>
public sealed record CopsLeaderboardPlayerRow(
    int Rank,
    string Name,
    long Kills,
    long Deaths,
    long Assists,
    double Ratio);

/// <summary>One row of the clan leaderboard.</summary>
public sealed record CopsLeaderboardClanRow(
    int Rank,
    string Name,
    string Tag,
    long Rating,
    int Players,
    double AverageRating,
    long Kills,
    long Deaths,
    long Assists,
    double Kdr,
    long Wins,
    long Losses,
    double Wlr);

/// <summary>
/// Parsers for the leaderboard endpoints (default.prod.copsapi.criticalforce.fi/api/leaderboard/*).
/// The endpoints answer plain arrays, oldest fields optional; every field is read tolerantly so a
/// shape change degrades to empty cells instead of a broken page. There is no server-side search:
/// the site filters the full list client-side, and so does Optima.
/// </summary>
public static class CopsLeaderboardParser
{
    public static IReadOnlyList<CopsLeaderboardPlayerRow> ParsePlayers(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Array)
            {
                return [];
            }
            var rows = new List<CopsLeaderboardPlayerRow>();
            foreach (var item in doc.RootElement.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }
                rows.Add(new CopsLeaderboardPlayerRow(
                    Rank: ReadInt(item, "rank"),
                    Name: ReadString(item, "name"),
                    Kills: ReadLong(item, "kills"),
                    Deaths: ReadLong(item, "deaths"),
                    Assists: ReadLong(item, "assists"),
                    Ratio: ReadDouble(item, "ratio")));
            }
            return rows;
        }
        catch (JsonException)
        {
            return [];
        }
    }

    public static IReadOnlyList<CopsLeaderboardClanRow> ParseClans(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Array)
            {
                return [];
            }
            var rows = new List<CopsLeaderboardClanRow>();
            foreach (var item in doc.RootElement.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }
                rows.Add(new CopsLeaderboardClanRow(
                    Rank: ReadInt(item, "rank"),
                    Name: ReadString(item, "name"),
                    Tag: ReadString(item, "tag"),
                    Rating: ReadLong(item, "rating"),
                    Players: ReadInt(item, "players"),
                    AverageRating: ReadDouble(item, "average_rating"),
                    Kills: ReadLong(item, "kills"),
                    Deaths: ReadLong(item, "deaths"),
                    Assists: ReadLong(item, "assists"),
                    Kdr: ReadDouble(item, "kdr"),
                    Wins: ReadLong(item, "wins"),
                    Losses: ReadLong(item, "losses"),
                    Wlr: ReadDouble(item, "wlr")));
            }
            return rows;
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private static string ReadString(JsonElement obj, string property)
        => obj.TryGetProperty(property, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? string.Empty : string.Empty;

    private static long ReadLong(JsonElement obj, string property)
        => obj.TryGetProperty(property, out var v) && v.TryGetInt64(out var value) ? value : 0;

    private static int ReadInt(JsonElement obj, string property)
        => obj.TryGetProperty(property, out var v) && v.TryGetInt32(out var value) ? value : 0;

    private static double ReadDouble(JsonElement obj, string property)
        => obj.TryGetProperty(property, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out var value) ? value : 0;
}
