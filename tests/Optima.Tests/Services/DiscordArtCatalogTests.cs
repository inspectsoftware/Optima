using Optima.Core.Discord;
using Xunit;

namespace Optima.Tests.Services;

/// <summary>
/// Which uploaded asset the presence card points at. Getting this wrong is not a crash: Discord
/// renders the card with a question mark where the image should be, which is how the app spent an
/// evening claiming to show a mark that the application did not have.
/// </summary>
public sealed class DiscordArtCatalogTests
{
    [Fact]
    public void NoUploadedAssetsMeansNoKey()
        => Assert.Null(DiscordArtCatalog.Pick([], "optima-presence"));

    [Fact]
    public void TheAssetNamedAfterTheArtworkWins()
    {
        var assets = new List<(string, ulong)>
        {
            ("something-else", 10),
            ("optima-presence", 1),
        };

        Assert.Equal("optima-presence", DiscordArtCatalog.Pick(assets, "optima-presence"));
    }

    [Theory]
    [InlineData("Optima Presence")]
    [InlineData("OptimaPresence")]
    [InlineData("optima_presence")]
    [InlineData("optima-presence-2")]
    public void NameMatchingIgnoresWhatThePortalRewrites(string uploaded)
    {
        // The portal lowercases names and turns spaces into separators, so a name that only differs in
        // punctuation or case is still the same asset.
        var assets = new List<(string, ulong)> { (uploaded, 5), ("unrelated", 99) };

        var expected = uploaded is "optima-presence-2" ? "unrelated" : uploaded;
        Assert.Equal(expected, DiscordArtCatalog.Pick(assets, "optima-presence"));
    }

    [Fact]
    public void WithoutANameMatchTheNewestUploadIsUsed()
    {
        var assets = new List<(string, ulong)> { ("older", 100), ("newer", 200) };

        Assert.Equal("newer", DiscordArtCatalog.Pick(assets, "optima-presence"));
    }

    [Fact]
    public void AssetsAreReadFromTheDiscordAnswer()
    {
        var assets = DiscordArtCatalog.ParseAssets(
            """[{"id":"1067178061846462464","name":"optima-presence"},{"id":"1","name":"other"},{"id":"2"}]""");

        Assert.Equal(2, assets.Count);
        Assert.Equal("optima-presence", assets[0].Name);
        Assert.Equal(1067178061846462464UL, assets[0].Id);
        Assert.Equal("other", assets[1].Name);
    }

    [Fact]
    public void AnAnswerThatIsNotAListOfAssetsIsNobodySProblem()
    {
        Assert.Empty(DiscordArtCatalog.ParseAssets("{\"message\":\"401: Unauthorized\"}"));
        Assert.Empty(DiscordArtCatalog.ParseAssets("[]"));
    }

    [Fact]
    public void AnUnparseableIdDoesNotBeatARealOne()
    {
        var assets = new List<(string, ulong)> { ("no-id", 0), ("real", 42) };

        Assert.Equal("real", DiscordArtCatalog.Pick(assets, "optima-presence"));
    }
}
