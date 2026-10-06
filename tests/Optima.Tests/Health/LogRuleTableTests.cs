using System.ComponentModel;
using Microsoft.Extensions.Logging;
using Optima.Core.Health;
using Optima.Core.Models;
using Xunit;

namespace Optima.Tests.Health;

public sealed class LogRuleTableTests
{
    private static readonly DateTimeOffset At = new(2026, 10, 5, 22, 9, 8, TimeSpan.FromHours(-4));

    internal static LogRecord Line(
        LogLevel level, string template, string? message = null, Exception? exception = null,
        string source = "Optima.App.ViewModels.PlayerStatsViewModel", string? code = null)
        => new()
        {
            Timestamp = At,
            Level = level,
            Source = source,
            Message = message ?? template,
            Template = template,
            Exception = exception is null ? null : ExceptionDetail.Capture(exception),
            Properties = code is null ? LogRecord.NoProperties : new Dictionary<string, string> { ["Code"] = code },
        };

    /// <summary>An exception that was really thrown, so it has a frame to be told apart by.</summary>
    internal static Exception ThrownFromHere(string message)
    {
        try
        {
            throw new InvalidOperationException(message);
        }
        catch (InvalidOperationException ex)
        {
            return ex;
        }
    }

    private static Exception ThrownFromSomewhereElse(string message)
    {
        try
        {
            throw new InvalidOperationException(message);
        }
        catch (InvalidOperationException ex)
        {
            return ex;
        }
    }

    [Theory]
    [InlineData(LogLevel.Trace)]
    [InlineData(LogLevel.Debug)]
    [InlineData(LogLevel.Information)]
    public void ALineBelowAWarningIsNeverAnIssue(LogLevel level)
        => Assert.Null(LogRuleTable.Classify(Line(level, "Unhandled UI exception")));

    [Fact]
    public void AWarningNobodyClaimsStaysALineInTheLog()
        => Assert.Null(LogRuleTable.Classify(Line(LogLevel.Warning, "Global hotkey {Key} is already in use")));

    [Fact]
    public void EveryErrorNobodyClaimsBecomesAnUnclassifiedIssue()
    {
        var match = LogRuleTable.Classify(Line(LogLevel.Error, "Page initialization failed for {Page}",
            "Page initialization failed for SESSIONS", ThrownFromHere("no such table")));

        Assert.NotNull(match);
        Assert.Equal(LogRuleTable.Unclassified, match.Code);
        Assert.Equal(IssueSeverity.Error, match.Severity);
        // It has no title of its own; what the line said is the only name it has.
        Assert.Equal("Page initialization failed for SESSIONS", match.Title);
        Assert.Contains("no such table", match.Detail);
    }

    [Fact]
    public void TheSameCallSiteCollapsesToOneIssue()
    {
        Exception Same(string message) => ThrownFromHere(message);

        var first = LogRuleTable.Classify(Line(LogLevel.Error, "Page initialization failed for {Page}",
            "Page initialization failed for SESSIONS", Same("first time")));
        var second = LogRuleTable.Classify(Line(LogLevel.Error, "Page initialization failed for {Page}",
            "Page initialization failed for DISPLAY", Same("second time")));

        // Different pages, different messages: the same line of code failing the same way.
        Assert.Equal(first!.Key, second!.Key);
    }

    [Fact]
    public void DifferentThrowSitesAreDifferentIssues()
    {
        var here = LogRuleTable.Classify(Line(LogLevel.Error, "Unobserved task exception", exception: ThrownFromHere("x")));
        var elsewhere = LogRuleTable.Classify(Line(LogLevel.Error, "Unobserved task exception", exception: ThrownFromSomewhereElse("x")));

        Assert.Equal("BACKGROUND_TASK_FAILED", here!.Code);
        Assert.Equal("BACKGROUND_TASK_FAILED", elsewhere!.Code);
        Assert.NotEqual(here.Key, elsewhere.Key);
    }

    [Fact]
    public void ALineThatNamesItsCodeIsThatCode()
    {
        var typed = LogRuleTable.Classify(Line(LogLevel.Error, "Session failed: {Code}", "Session failed: VDD_NO_DISPLAY",
            OptimaException.From("VDD_NO_DISPLAY", "The virtual display did not appear", "It never attached.")));
        var named = LogRuleTable.Classify(Line(LogLevel.Warning, "Session step skipped ({Code}): {Title}",
            "Session step skipped (LAUNCH_STEP_SKIPPED): Background cleanup was skipped", code: "LAUNCH_STEP_SKIPPED"));

        Assert.Equal(("VDD_NO_DISPLAY", "VDD_NO_DISPLAY", IssueSeverity.Error), (typed!.Key, typed.Code, typed.Severity));
        // The guide's title, not the log line's wording.
        Assert.Equal("The virtual display did not appear", typed.Title);
        Assert.Equal(("LAUNCH_STEP_SKIPPED", IssueSeverity.Warning), (named!.Code, named.Severity));
    }

    [Fact]
    public void APowerPlanThePcDoesNotOfferIsANoteNotAProblem()
    {
        var match = LogRuleTable.Classify(Line(LogLevel.Warning, "Session step skipped ({Code}): {Title}",
            "Session step skipped (POWER_PLAN_UNAVAILABLE): This PC does not offer that power plan", code: "POWER_PLAN_UNAVAILABLE"));

        // Logged as a warning, and on a Modern Standby PC it is logged every session. A list that
        // carried it as a warning would never be empty there.
        Assert.Equal(IssueSeverity.Note, match!.Severity);
    }

    [Fact]
    public void TheLaunchFailureThatStartedThisIsRecognised()
    {
        var match = LogRuleTable.Classify(Line(LogLevel.Error, "Unexpected session failure during {Phase}",
            "Unexpected session failure during ApplyingPerformanceProfile",
            new Win32Exception(2, "PowerSetActiveScheme(8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c) failed"),
            source: "Optima.Core.Launch.LaunchOrchestrator"));

        Assert.Equal("UNEXPECTED", match!.Code);
        Assert.Equal("Something went wrong during the session", match.Title);
        Assert.Contains("(Win32 2", match.Detail);
    }

    [Theory]
    [InlineData(LogLevel.Critical, "Unhandled UI exception", "OPTIMA_CRASHED", IssueSeverity.Critical)]
    [InlineData(LogLevel.Critical, "Unhandled AppDomain exception", "OPTIMA_CRASHED", IssueSeverity.Critical)]
    [InlineData(LogLevel.Error, "The previous run of Optima ended on a fatal error at {At}, on {Origin}: {Summary}\n{Detail}", "PREVIOUS_RUN_CRASHED", IssueSeverity.Error)]
    [InlineData(LogLevel.Warning, "The previous run of Optima ({Version}, started {StartedAt}) did not shut down normally: it was ended from outside, or the PC lost power", "UNCLEAN_EXIT", IssueSeverity.Note)]
    [InlineData(LogLevel.Error, "Failed restoring {What}, continuing with remaining restore steps", "RESTORE_STEP_FAILED", IssueSeverity.Error)]
    [InlineData(LogLevel.Warning, "Emergency restore timed out; the recovery prompt will appear on next start", "RESTORE_STEP_FAILED", IssueSeverity.Error)]
    [InlineData(LogLevel.Error, "Corrupt JSON at {Path}; renaming aside and using defaults", "SETTINGS_CORRUPT", IssueSeverity.Error)]
    [InlineData(LogLevel.Warning, "Corrupt JSON at {Path}; recovered from the backup of the previous save", "SETTINGS_RECOVERED", IssueSeverity.Note)]
    [InlineData(LogLevel.Error, "Elevated helper not found at {Path}", "HELPER_MISSING", IssueSeverity.Error)]
    public void ALineFromAKnownPlaceIsTheCodeThatPlaceStandsFor(LogLevel level, string template, string code, IssueSeverity severity)
    {
        var match = LogRuleTable.Classify(Line(level, template));

        Assert.Equal(code, match!.Code);
        Assert.Equal(code, match.Key);
        Assert.Equal(severity, match.Severity);
    }

    [Fact]
    public void TheEnginesOwnLinesAreNeverReadBackIn()
    {
        var own = Line(LogLevel.Error, "Check {Check} could not run", source: "Optima.Core.Health.IssueEngine");

        Assert.Null(LogRuleTable.Classify(own));
    }

    [Fact]
    public void EveryCodeARuleCanRaiseIsInTheGuide()
    {
        var missing = LogRuleTable.Codes.Where(code => ErrorCatalog.Find(code) is null).ToList();

        Assert.True(missing.Count == 0, "issue codes without a guide entry: " + string.Join(", ", missing));
    }
}
