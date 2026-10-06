using System.Text.RegularExpressions;
using Xunit;

namespace Optima.Tests.Views;

/// <summary>
/// The Diagnostics and Logs pages were merged into DEBUG, and some thirty sentences across the app
/// still told people to go and look at one of them: error fixes, status lines, the error guide. A
/// sentence that names a page that does not exist is worse than no sentence, and nothing but a
/// search finds them, so this is the search. It reads every source and markup file, comments
/// included, so the next reader is not sent to a page that is gone either.
/// </summary>
public class StalePageNameTests
{
    private static readonly Regex GonePage = new(
        @"Logs pages?\b|LOGS pages?\b|Diagnostics pages?\b|DIAGNOSTICS pages?\b|[Ss]ee Logs\b|DIAGNOSTICS tab\b|LOGS tab\b|from Diagnostics\b|[Rr]un Diagnostics\b",
        RegexOptions.Compiled);

    [Fact]
    public void NothingStillPointsAtThePagesDebugReplaced()
    {
        var repoRoot = FindRepositoryRoot();
        if (repoRoot is null)
        {
            return;
        }

        var stale = new List<string>();
        var separator = Path.DirectorySeparatorChar;
        var files = Directory.EnumerateFiles(Path.Combine(repoRoot, "src"), "*.*", SearchOption.AllDirectories)
            .Where(f => f.EndsWith(".cs", StringComparison.Ordinal) || f.EndsWith(".xaml", StringComparison.Ordinal))
            .Where(f => !f.Contains($"{separator}obj{separator}") && !f.Contains($"{separator}bin{separator}"))
            .Append(Path.Combine(repoRoot, "README.md"));

        foreach (var file in files)
        {
            var lines = File.ReadAllLines(file);
            for (var i = 0; i < lines.Length; i++)
            {
                if (GonePage.Match(lines[i]) is { Success: true } match)
                {
                    stale.Add($"{Path.GetRelativePath(repoRoot, file)}:{i + 1} \"{match.Value}\"");
                }
            }
        }

        Assert.True(stale.Count == 0, "still pointing at a page that is gone:\n" + string.Join("\n", stale));
    }

    [Theory]
    [InlineData("Check the Logs page for details")]
    [InlineData("kill failed. See Logs.")]
    [InlineData("Run detection again from the Diagnostics page")]
    [InlineData("Run Diagnostics to verify the environment")]
    [InlineData("run the checks from the DIAGNOSTICS tab")]
    public void TheSearchFindsTheSentencesItWasWrittenFor(string sentence)
        => Assert.Matches(GonePage, sentence);

    [Theory]
    [InlineData("See the log on the Debug page.")]
    [InlineData("Run the checks on the Debug page to verify the environment")]
    [InlineData("System.Diagnostics.Process.Start(info)")]
    [InlineData("the Checks tab on the Debug page")]
    public void TheSearchLeavesTheNewWordingAndOrdinaryCodeAlone(string line)
        => Assert.DoesNotMatch(GonePage, line);

    private static string? FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (Directory.Exists(Path.Combine(directory.FullName, "src", "Optima.App")))
            {
                return directory.FullName;
            }
            directory = directory.Parent;
        }
        return null;
    }
}
