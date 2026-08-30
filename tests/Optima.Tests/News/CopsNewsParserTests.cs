using Optima.Core.News;
using Xunit;

namespace Optima.Tests.News;

public sealed class CopsNewsParserTests
{
    [Fact]
    public void ParsesTheLiveUpdatesPage()
    {
        var entries = CopsNewsParser.Parse(NewsFixtures.UpdatesPageHtml);

        Assert.True(entries.Count >= 2, $"expected multiple entries, got {entries.Count}");

        var first = entries[0];
        Assert.Equal("SCATTERSHOT", first.Name);
        Assert.Equal("1.80.0", first.Version);
        Assert.Equal("BETA", first.Status);
        Assert.Contains("CLAN REWORK", first.Headlines);
        Assert.DoesNotContain(first.Headlines, h => h.Contains("patch notes", StringComparison.OrdinalIgnoreCase));
        Assert.StartsWith("https://criticalopsgame.com/news/", first.NotesUrl);
    }

    [Fact]
    public void LatestLiveSkipsBetaEntries()
    {
        var entries = CopsNewsParser.Parse(NewsFixtures.UpdatesPageHtml);
        Assert.Equal("1.70.0", CopsNewsParser.LatestLiveVersion(entries));
    }

    [Theory]
    [InlineData("")]
    [InlineData("<html><body>nothing here</body></html>")]
    [InlineData("not html at all")]
    public void ShapeDriftYieldsEmptyNotThrow(string html)
        => Assert.Empty(CopsNewsParser.Parse(html));

    [Fact]
    public void EntryWithoutTrailingVersionKeepsFullTitle()
    {
        const string html =
            """
            <article><p class="comment date current">LIVE</p><h3><a href="/updates/x/">Anniversary Special</a></h3>
            <div><p>PARTY</p></div></article>
            """;
        var entry = Assert.Single(CopsNewsParser.Parse(html));
        Assert.Equal("Anniversary Special", entry.Name);
        Assert.Equal("", entry.Version);
        Assert.Null(CopsNewsParser.LatestLiveVersion([entry]));
    }
}
