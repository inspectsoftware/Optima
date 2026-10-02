using System.Text.RegularExpressions;
using Xunit;

namespace Optima.Tests.Views;

/// <summary>
/// The sibling of <see cref="XamlNameReferenceTests"/>, for the other reference WPF resolves quietly:
/// <c>{StaticResource Missing}</c> throws only when the element is actually built, which is why a view
/// whose page is rarely opened can be broken for months without anyone noticing. Every key a view asks
/// for statically must be declared by some dictionary in the app.
/// </summary>
public class XamlResourceReferenceTests
{
    private static readonly Regex DeclaredKey = new(
        @"x:Key=""(?<key>[^""]+)""",
        RegexOptions.Compiled);

    private static readonly Regex StaticReference = new(
        @"StaticResource\s+(?<key>[A-Za-z_][A-Za-z0-9_.]*)",
        RegexOptions.Compiled);

    [Fact]
    public void EveryStaticResourceReferenceIsDeclaredSomewhere()
    {
        var repoRoot = FindRepositoryRoot();
        if (repoRoot is null)
        {
            return;
        }

        var app = Path.Combine(repoRoot, "src", "Optima.App");
        var files = Directory
            .EnumerateFiles(app, "*.xaml", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"))
            .ToList();

        var declared = files
            .SelectMany(f => DeclaredKey.Matches(File.ReadAllText(f)))
            .Select(m => m.Groups["key"].Value)
            .ToHashSet(StringComparer.Ordinal);

        var missing = new List<string>();
        foreach (var file in files)
        {
            var text = File.ReadAllText(file);
            foreach (Match reference in StaticReference.Matches(text))
            {
                var key = reference.Groups["key"].Value;
                if (!declared.Contains(key))
                {
                    missing.Add($"{Path.GetFileName(file)} -> {key}");
                }
            }
        }

        Assert.Empty(missing);
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
