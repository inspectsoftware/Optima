using System.Text.RegularExpressions;
using Optima.Core.Models;
using Xunit;

namespace Optima.Tests.Models;

/// <summary>
/// Keeps the LOGS error guide honest: every error code the app can raise must have a catalog
/// entry, and every entry must actually explain itself. The scan runs over the repository
/// source, so a new OptimaException or DriverInstallResult.Fail fails this test until the
/// guide gains an entry.
/// </summary>
public sealed class ErrorCatalogTests
{
    private static readonly Regex CodeFromException = new(
        "OptimaException\\.From\\(\\s*\"(?<code>[A-Z_0-9]+)\"",
        RegexOptions.Compiled);

    private static readonly Regex CodeFromInstaller = new(
        "Code\\s*=\\s*\"(?<code>[A-Z_0-9]+)\"",
        RegexOptions.Compiled);

    /// <summary>
    /// The launch pipeline names its codes inline, for results and for warnings alike, so there the
    /// scan takes every code-shaped literal instead of looking for one call.
    /// </summary>
    private static readonly Regex CodeLiteral = new(
        "\"(?<code>[A-Z][A-Z_0-9]{3,})\"",
        RegexOptions.Compiled);

    /// <summary>Outcomes the pipeline reports with a code that are not faults and have nothing to fix.</summary>
    private static readonly string[] NotFaults = ["SESSION_ACTIVE", "CANCELLED"];

    [Fact]
    public void EveryRaisedErrorCodeHasACatalogEntry()
    {
        var sourceRoot = FindSourceRoot();
        var raised = new HashSet<string>();

        foreach (var file in Directory.EnumerateFiles(sourceRoot, "*.cs", SearchOption.AllDirectories))
        {
            if (file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                || file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")
                || file.Contains($"{Path.DirectorySeparatorChar}tests{Path.DirectorySeparatorChar}"))
            {
                continue;
            }
            var text = File.ReadAllText(file);
            foreach (Match match in CodeFromException.Matches(text))
            {
                raised.Add(match.Groups["code"].Value);
            }
            if (text.Contains("DriverInstallResult.Fail"))
            {
                foreach (Match match in CodeFromInstaller.Matches(text))
                {
                    raised.Add(match.Groups["code"].Value);
                }
            }
            if (Path.GetFileName(file) == "LaunchOrchestrator.cs")
            {
                foreach (Match match in CodeLiteral.Matches(text))
                {
                    raised.Add(match.Groups["code"].Value);
                }
            }
        }
        raised.ExceptWith(NotFaults);

        Assert.NotEmpty(raised);
        var missing = raised.Where(c => ErrorCatalog.Find(c) is null).ToList();
        Assert.True(missing.Count == 0, "error codes without a guide entry: " + string.Join(", ", missing));
    }

    [Theory]
    [InlineData("UNEXPECTED")]
    [InlineData("GPG_NOT_FOUND")]
    [InlineData("POWER_PLAN_UNAVAILABLE")]
    [InlineData("LAUNCH_STEP_SKIPPED")]
    [InlineData("SESSION_NOT_SAVED")]
    public void TheLaunchPipelinesOwnCodesAreInTheGuide(string code)
        => Assert.NotNull(ErrorCatalog.Find(code));

    [Fact]
    public void EveryCatalogEntryExplainsItself()
    {
        foreach (var entry in ErrorCatalog.All)
        {
            Assert.False(string.IsNullOrWhiteSpace(entry.Code));
            Assert.False(string.IsNullOrWhiteSpace(entry.Title));
            Assert.False(string.IsNullOrWhiteSpace(entry.WhatHappened));
            Assert.False(string.IsNullOrWhiteSpace(entry.WhyItHappens));
            Assert.NotEmpty(entry.HowToFix);
            Assert.All(entry.HowToFix, fix => Assert.False(string.IsNullOrWhiteSpace(fix)));
        }
    }

    [Fact]
    public void FindIsCaseInsensitiveAndNullForUnknown()
    {
        Assert.NotNull(ErrorCatalog.Find("vdd_no_display"));
        Assert.NotNull(ErrorCatalog.Find("VDD_NO_DISPLAY"));
        Assert.Null(ErrorCatalog.Find("NOT_A_REAL_CODE"));
    }

    [Fact]
    public void TheReloadDriverGuidanceMentionsTheReloadStep()
    {
        // The user-facing symptom is a "RELOAD_DRIVER" failure; the guide must answer it.
        var entry = ErrorCatalog.Find("VDD_NO_DISPLAY")!;
        Assert.Contains(entry.HowToFix, fix => fix.Contains("RELOAD", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void PlaintextFormatCarriesTheWholeEntry()
    {
        var text = ErrorCatalog.FormatPlaintext(ErrorCatalog.Find("VDD_NO_DISPLAY")!);

        Assert.StartsWith("[VDD_NO_DISPLAY] ", text);
        Assert.Contains("What: ", text);
        Assert.Contains("Why: ", text);
        Assert.Contains("How to fix:", text);
        Assert.Contains("- " + ErrorCatalog.Find("VDD_NO_DISPLAY")!.HowToFix[0], text);
    }

    private static string FindSourceRoot()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null)
        {
            var candidate = Path.Combine(dir, "src");
            if (Directory.Exists(candidate))
            {
                return candidate;
            }
            dir = Path.GetDirectoryName(dir);
        }
        throw new InvalidOperationException("could not locate the src folder from " + AppContext.BaseDirectory);
    }
}
