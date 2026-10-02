using System.Text.RegularExpressions;
using Xunit;

namespace Optima.Tests.Views;

/// <summary>
/// WPF resolves ElementName silently: a binding whose source name does not exist reports nothing
/// the user can see and simply leaves the element empty. The recovery prompt shipped that way once
/// — heading and body bound to a name no element declared, so the dialog appeared as a blank glass
/// panel above its buttons. Every ElementName reference must therefore point at a name that the
/// same file declares.
/// </summary>
public class XamlNameReferenceTests
{
    private static readonly Regex ElementNameReference = new(
        @"ElementName=(?<name>[A-Za-z_][A-Za-z0-9_]*)",
        RegexOptions.Compiled);

    private static readonly Regex DeclaredName = new(
        @"(?:x:Name|Name)=""(?<name>[A-Za-z_][A-Za-z0-9_]*)""",
        RegexOptions.Compiled);

    [Fact]
    public void EveryElementNameReferencePointsAtANameTheFileDeclares()
    {
        var repoRoot = FindRepositoryRoot();
        if (repoRoot is null)
        {
            return;
        }

        var dangling = new List<string>();
        foreach (var file in Directory.EnumerateFiles(
            Path.Combine(repoRoot, "src", "Optima.App"), "*.xaml", SearchOption.AllDirectories))
        {
            var separator = Path.DirectorySeparatorChar;
            if (file.Contains($"{separator}obj{separator}") || file.Contains($"{separator}bin{separator}"))
            {
                continue;
            }

            var text = File.ReadAllText(file);
            var declared = DeclaredName.Matches(text).Select(m => m.Groups["name"].Value).ToHashSet(StringComparer.Ordinal);
            foreach (Match reference in ElementNameReference.Matches(text))
            {
                var name = reference.Groups["name"].Value;
                if (!declared.Contains(name))
                {
                    dangling.Add($"{Path.GetFileName(file)} -> ElementName={name}");
                }
            }
        }

        Assert.Empty(dangling);
    }

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
