using Optima.Core.Discord;
using Xunit;

namespace Optima.Tests.Services;

/// <summary>
/// The presence card's small image is the player's rank emblem. Getting the tier wrong is not a crash:
/// the card wears someone else's rank, which is worse.
/// </summary>
public sealed class DiscordRankArtTests
{
    [Theory]
    [InlineData(0, "unranked")]
    [InlineData(1, "iron")]
    [InlineData(4, "gold")]
    [InlineData(8, "specops")]
    [InlineData(9, "elite")]
    public void EveryLadderTierHasItsOwnArtwork(int tier, string slug)
    {
        Assert.Equal(slug, DiscordRankArt.SlugForTier(tier));
    }

    [Fact]
    public void AnUnknownTierGetsNoArtRatherThanTheWrongOne()
    {
        Assert.Null(DiscordRankArt.SlugForTier(null));
        Assert.Null(DiscordRankArt.SlugForTier(-1));
        Assert.Null(DiscordRankArt.SlugForTier(10));
    }

    [Fact]
    public void ThePublicArtworkIsAHttpsUrlNamedAfterTheFile()
    {
        var url = DiscordRankArt.PublicUrlForTier(4);

        Assert.StartsWith("https://", url);
        Assert.EndsWith("/gold.png", url);
    }

    [Fact]
    public void ATierWithoutArtHasNoUrl()
    {
        Assert.Equal("", DiscordRankArt.PublicUrlForTier(null));
        Assert.Equal("", DiscordRankArt.PublicUrlForTier(99));
    }

    [Fact]
    public void TheUploadedKeyIsNamespacedSoItCannotCollideWithTheAppMark()
    {
        Assert.Equal("rank-gold", DiscordRankArt.UploadedKey("gold"));
    }

    /// <summary>
    /// Unlike the app mark, a rank emblem must never fall back to a different upload: that would put
    /// one tier's emblem on every card.
    /// </summary>
    [Fact]
    public void AMissingRankEmblemDoesNotBorrowAnotherUpload()
    {
        var assets = new List<(string, ulong)> { ("optima-presence", 99), ("something-else", 100) };

        Assert.Null(DiscordArtCatalog.Match(assets, "rank-gold"));
    }

    [Fact]
    public void AnUploadedRankEmblemIsFoundThroughThePortalRenaming()
    {
        var assets = new List<(string, ulong)> { ("Rank Gold", 7), ("optima-presence", 99) };

        Assert.Equal("Rank Gold", DiscordArtCatalog.Match(assets, "rank-gold"));
    }

    [Fact]
    public void AMissingAppMarkStillFallsBackToTheNewestUpload()
    {
        var assets = new List<(string, ulong)> { ("older", 100), ("newer", 200) };

        Assert.Equal("newer", DiscordArtCatalog.Pick(assets, "optima-presence"));
    }
}
