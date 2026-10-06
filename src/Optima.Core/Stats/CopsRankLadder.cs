namespace Optima.Core.Stats;

/// <summary>One Critical Ops ranked tier as Optima renders it.</summary>
public sealed record CopsRankInfo(
    int Tier,
    string Name,
    string ColorHex)
{
    public string ShortName => Name.Split(' ')[0];
}

/// <summary>
/// The ranked ladder, taken from Critical Force's own ranked page (criticalopsgame.com/intel/ranked,
/// updated 2026-07-27): Iron 0-1199, Bronze 1200-1299, Silver 1300-1399, Gold 1400-1499,
/// Platinum 1500-1599, Diamond 1600-1699, Master 1700-1799, Spec Ops 1800+, Elite Ops 1950+ (top 250).
/// The API's stats.ranked.rank indexes the placed tiers 1..9 in that order; 0 means still calibrating.
/// Divisions are not in the API, so Optima derives them from the rating (each tier spans 100 rating,
/// four 25-point divisions). The MMR ranges are the authority; the tier index is the cross-check.
/// </summary>
public static class CopsRankLadder
{
    private sealed record TierRange(string Name, string ColorHex, long MinMmr, long MaxMmrExclusive);

    private static IReadOnlyList<TierRange> Ranges { get; } =
    [
        new("Calibrating", "#757D88", 0, 1),          // rank 0: placements not finished
        new("Iron", "#9A8B7E", 1100, 1200),
        new("Bronze", "#B4784C", 1200, 1300),
        new("Silver", "#C7CFDD", 1300, 1400),
        new("Gold", "#E8B45A", 1400, 1500),
        new("Platinum", "#6FB7E8", 1500, 1600),
        new("Diamond", "#7FD6A4", 1600, 1700),
        new("Master", "#E88A9E", 1700, 1800),
        new("Spec Ops", "#A98BE8", 1800, 1950),
        new("Elite Ops", "#FF6B6B", 1950, long.MaxValue),
    ];

    public static IReadOnlyList<CopsRankInfo> Tiers { get; } =
    [
        new(0, "Calibrating", "#757D88"),
        new(1, "Iron", "#9A8B7E"),
        new(2, "Bronze", "#B4784C"),
        new(3, "Silver", "#C7CFDD"),
        new(4, "Gold", "#E8B45A"),
        new(5, "Platinum", "#6FB7E8"),
        new(6, "Diamond", "#7FD6A4"),
        new(7, "Master", "#E88A9E"),
        new(8, "Spec Ops", "#A98BE8"),
        new(9, "Elite Ops", "#FF6B6B"),
    ];

    /// <summary>
    /// Resolves the display rank from the API facts. A tier index of 0 means the player is
    /// still calibrating (placements unfinished), whatever the provisional MMR says. Otherwise
    /// the MMR decides the tier per the official ranges; the index is a cross-check.
    /// </summary>
    public static CopsRankInfo? Resolve(int? tierIndex, long? mmr)
    {
        if (tierIndex == 0)
        {
            return Tiers[0];
        }
        if (mmr is not { } rating)
        {
            return Find(tierIndex);
        }

        var byRating = Ranges.FirstOrDefault(r => rating >= r.MinMmr && rating < r.MaxMmrExclusive);
        if (byRating is null || byRating.Name == "Calibrating")
        {
            return Find(tierIndex);
        }
        return new CopsRankInfo(TierIndexOf(byRating.Name), byRating.Name, byRating.ColorHex);
    }

    /// <summary>The division inside the tier (1-4), derived from the rating; null when not applicable.</summary>
    public static int? Division(int? tierIndex, long? mmr)
    {
        if (mmr is not { } rating || rating < 1100 || rating >= 1800)
        {
            return null;
        }
        var tier = Resolve(tierIndex, rating);
        // Calibrating has no divisions: a provisional rating is not inside a tier, and dividing it by the
        // 25-point steps produced a confident "Calibrating 4" for a player who has not placed at all.
        if (tier is null || tier.Name == "Calibrating")
        {
            return null;
        }
        var offset = (int)(rating - Ranges.First(r => r.Name == tier.Name).MinMmr);
        return Math.Min(4, offset / 25 + 1);
    }

    public static CopsRankInfo? Find(int? tier)
        => tier is { } value && value >= 0 && value < Tiers.Count ? Tiers[value] : null;

    /// <summary>The rating band a tier spans, or null for tiers that are open ended (Elite Ops).</summary>
    public static (long Min, long MaxExclusive)? RatingBand(int? tierIndex, long? mmr)
    {
        if (Resolve(tierIndex, mmr) is not { } tier || tier.Name == "Calibrating")
        {
            return null;
        }
        var range = Ranges.FirstOrDefault(r => r.Name == tier.Name);
        return range is null || range.MaxMmrExclusive == long.MaxValue
            ? null
            : (range.MinMmr, range.MaxMmrExclusive);
    }

    /// <summary>The tier above the player's current one, or null at the top of the ladder.</summary>
    public static CopsRankInfo? NextTier(int? tierIndex, long? mmr)
    {
        if (Resolve(tierIndex, mmr) is not { } tier || tier.Name == "Calibrating")
        {
            // A player who has not placed is not climbing towards Iron: they are being placed, and a
            // "1,000 rating to Iron" progress bar would be inventing a ladder position they do not hold.
            return null;
        }
        return Tiers.FirstOrDefault(t => t.Tier == tier.Tier + 1);
    }

    /// <summary>
    /// How much rating is left before the next tier, and where the player sits inside the current
    /// band (0..1). Both are null for an unranked player, for Elite Ops and when the API gave no
    /// rating: the card shows progress only when there is a real band to fill.
    /// </summary>
    public static (long Remaining, double Progress)? ProgressToNextTier(int? tierIndex, long? mmr)
    {
        if (RatingBand(tierIndex, mmr) is not { } band || NextTier(tierIndex, mmr) is null)
        {
            return null;
        }
        var rating = mmr!.Value;
        var span = band.MaxExclusive - band.Min;
        var progress = span <= 0 ? 0 : Math.Clamp((double)(rating - band.Min) / span, 0, 1);
        return (Math.Max(0, band.MaxExclusive - rating), progress);
    }

    /// <summary>
    /// The rank as the app writes it: "Gold 2" with the division when the rating places one,
    /// otherwise the bare tier name ("Master"). One definition, so the presence card, the stats
    /// panel and the profile card cannot drift into three spellings of the same rank.
    /// </summary>
    public static string Label(int? tierIndex, long? mmr)
    {
        if (Resolve(tierIndex, mmr) is not { } rank)
        {
            return string.Empty;
        }

        return Division(tierIndex, mmr) is { } division
            ? $"{rank.ShortName} {division}"
            : rank.Name;
    }

    private static int TierIndexOf(string name)
        => Tiers.First(t => t.Name == name).Tier;
}
