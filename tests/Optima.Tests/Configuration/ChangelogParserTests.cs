using Optima.Core.Configuration;
using Xunit;

namespace Optima.Tests.Configuration;

public sealed class ChangelogParserTests
{
    [Fact]
    public void ParsesEntriesWithDateAndTitle()
    {
        const string markdown =
            """
            # Changelog

            Some preamble that must be ignored.

            ## 2026-08-27 - Big feature drop

            - first change
            - second change

            ## 2026-08-26 - Earlier build

            - old change
            """;

        var entries = ChangelogParser.Parse(markdown);

        Assert.Equal(2, entries.Count);
        Assert.Equal("2026-08-27", entries[0].Date);
        Assert.Equal("Big feature drop", entries[0].Title);
        Assert.Equal(["first change", "second change"], entries[0].Changes);
        Assert.Equal("Earlier build", entries[1].Title);
    }

    [Fact]
    public void JoinsIndentedContinuationLines()
    {
        const string markdown =
            """
            ## 2026-08-27 - Build

            - a long bullet that wraps
              onto the next line
            - short one
            """;

        var entries = ChangelogParser.Parse(markdown);
        Assert.Equal(["a long bullet that wraps onto the next line", "short one"], entries[0].Changes);
    }

    [Fact]
    public void HeadingWithoutDateKeepsWholeTextAsTitle()
    {
        var entries = ChangelogParser.Parse("## Unreleased\n- something");
        var entry = Assert.Single(entries);
        Assert.Equal(string.Empty, entry.Date);
        Assert.Equal("Unreleased", entry.Title);
    }

    [Fact]
    public void EmptyOrPreambleOnlyInputYieldsNoEntries()
    {
        Assert.Empty(ChangelogParser.Parse(string.Empty));
        Assert.Empty(ChangelogParser.Parse("# Changelog\n\njust prose\n- a stray bullet before any entry"));
    }

    [Fact]
    public void EntryWithNoBulletsIsStillListed()
    {
        var entries = ChangelogParser.Parse("## 2026-01-01 - Quiet build\n\nprose only\n");
        var entry = Assert.Single(entries);
        Assert.Empty(entry.Changes);
    }

    [Fact]
    public void ParsesTheRepoChangelogFile()
    {
        // The real file must stay parseable; this guards the format contract.
        var path = Path.Combine(FindRepoRoot(), "CHANGELOG.md");
        var entries = ChangelogParser.Parse(File.ReadAllText(path));
        Assert.NotEmpty(entries);
        Assert.All(entries, e => Assert.False(string.IsNullOrWhiteSpace(e.Title)));
        Assert.All(entries, e => Assert.NotEmpty(e.Changes));
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "CHANGELOG.md")))
        {
            dir = dir.Parent;
        }
        return dir?.FullName ?? throw new InvalidOperationException("CHANGELOG.md not found above the test directory.");
    }
}
