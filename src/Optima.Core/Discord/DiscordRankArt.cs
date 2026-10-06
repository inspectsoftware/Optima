namespace Optima.Core.Discord;

/// <summary>
/// Which rank artwork the presence card's small image should show, per ladder tier.
///
/// The card's small image is the player's rank emblem, so the ladder tier has to become a name
/// Discord can resolve. Two routes exist and both are offered here: an asset uploaded to the
/// application (<see cref="UploadedKey"/>, which is what a polished app ships) and the public
/// artwork in the repository (<see cref="PublicUrl"/>, which works with no portal setup at all).
/// The service prefers an uploaded asset when the application actually has one, because an asset
/// key that does not exist renders as a question mark rather than as an error.
/// </summary>
public static class DiscordRankArt
{
    /// <summary>
    /// Where the rank art is served from, as the published repository holds it. Discord accepts a
    /// plain https URL in an image field (see the library's own remark on the asset key properties),
    /// so the card can show rank art without anyone uploading ten icons to the developer portal.
    /// </summary>
    private const string PublicBaseUrl =
        "https://raw.githubusercontent.com/inspectsoftware/Optima/master/src/Optima.App/Assets/Ranks/";

    /// <summary>
    /// Ladder tier index to the file name in <c>Assets/Ranks</c>. Tier 0 is calibrating, which the
    /// artwork calls "unranked": showing the unranked emblem is honest about a provisional rank.
    /// Unknown tiers get no art rather than a wrong one.
    /// </summary>
    private static string?[] Slugs { get; } =
    [
        "unranked", // 0 calibrating
        "iron",
        "bronze",
        "silver",
        "gold",
        "platinum",
        "diamond",
        "master",
        "specops",
        "elite",    // 9 elite ops
    ];

    /// <summary>The artwork file name for a tier, or null when there is no art for it.</summary>
    public static string? SlugForTier(int? tier)
        => tier is { } value && value >= 0 && value < Slugs.Length ? Slugs[value] : null;

    /// <summary>
    /// The asset key to look for in the application's uploaded assets. Names are normalized before
    /// matching (the portal lowercases and re-punctuates them), so the prefix is only a convention.
    /// </summary>
    public static string UploadedKey(string slug) => "rank-" + slug;

    /// <summary>The public artwork URL for a tier, or empty when the tier has no art.</summary>
    public static string PublicUrlForTier(int? tier)
        => SlugForTier(tier) is { } slug ? PublicBaseUrl + slug + ".png" : string.Empty;
}
