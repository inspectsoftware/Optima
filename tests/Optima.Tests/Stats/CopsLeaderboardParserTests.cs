using Optima.Core.Stats;
using Xunit;

namespace Optima.Tests.Stats;

public sealed class CopsLeaderboardParserTests
{
    [Fact]
    public void ParsesClanRows()
    {
        const string json = """
        [
          {"name":"Intellect","tag":"ie","rating":67921,"players":44,"average_rating":1543.659,
           "kills":20770,"deaths":18491,"kdr":1.12,"assists":2813,"wins":755,"losses":578,"wlr":1.31,"rank":1},
          {"name":"Champions Ieague","tag":"UcI","rating":63276,"players":44,"average_rating":1438.09,
           "kills":42262,"deaths":42018,"kdr":1.01,"assists":5887,"wins":1736,"losses":1408,"wlr":1.23,"rank":2}
        ]
        """;

        var rows = CopsLeaderboardParser.ParseClans(json);

        Assert.Equal(2, rows.Count);
        Assert.Equal("Intellect", rows[0].Name);
        Assert.Equal("ie", rows[0].Tag);
        Assert.Equal(67921, rows[0].Rating);
        Assert.Equal(44, rows[0].Players);
        Assert.Equal(1, rows[0].Rank);
        Assert.Equal(1.12, rows[0].Kdr, precision: 2);
        Assert.Equal(1.31, rows[0].Wlr, precision: 2);
    }

    [Fact]
    public void ParsesPlayerRowsWithTolerantDefaults()
    {
        const string json = """
        [
          {"kills":219011,"deaths":238283,"assists":31652,"ratio":0.92,"name":"Lube","rank":1},
          {"name":"mystery","rank":2}
        ]
        """;

        var rows = CopsLeaderboardParser.ParsePlayers(json);

        Assert.Equal(2, rows.Count);
        Assert.Equal("Lube", rows[0].Name);
        Assert.Equal(219011, rows[0].Kills);
        Assert.Equal(0.92, rows[0].Ratio, precision: 2);
        // A row missing stat fields degrades to zeros instead of breaking the page.
        Assert.Equal("mystery", rows[1].Name);
        Assert.Equal(0, rows[1].Kills);
        Assert.Equal(0, rows[1].Ratio);
    }

    [Theory]
    [InlineData("not json at all")]
    [InlineData("{}")]
    [InlineData("[]")]
    [InlineData("")]
    public void BrokenAnswersParseToEmpty(string json)
    {
        Assert.Empty(CopsLeaderboardParser.ParsePlayers(json));
        Assert.Empty(CopsLeaderboardParser.ParseClans(json));
    }
}
