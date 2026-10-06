using System.ComponentModel;
using Microsoft.Extensions.Logging;
using Optima.Core.Health;
using Optima.Core.Models;
using Xunit;

namespace Optima.Tests.Health;

public sealed class LogRecordTests
{
    private static readonly DateTimeOffset At = new(2026, 10, 5, 22, 9, 8, 968, TimeSpan.FromHours(-4));
    private static readonly RedactionIdentity Alice = new("alice", "GAMING-PC");
    private static readonly ReportEnvironment Build = new("0.7.5", "Microsoft Windows NT 10.0.26100.0");

    private static LogRecord Line(
        LogLevel level, string message, Exception? exception = null, string source = "Optima.Core.Launch.LaunchOrchestrator",
        IReadOnlyDictionary<string, string>? properties = null, int secondsEarlier = 0)
        => new()
        {
            Timestamp = At.AddSeconds(-secondsEarlier),
            Level = level,
            Source = source,
            Message = message,
            Exception = exception is null ? null : ExceptionDetail.Capture(exception),
            Properties = properties ?? LogRecord.NoProperties,
        };

    /// <summary>The line the whole debugging center started from.</summary>
    private static LogRecord TheLaunchFailure() => Line(
        LogLevel.Error,
        "Unexpected session failure during ApplyingPerformanceProfile",
        new Win32Exception(2, "PowerSetActiveScheme(8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c) failed"),
        properties: new Dictionary<string, string> { ["Phase"] = "ApplyingPerformanceProfile" });

    [Theory]
    [InlineData(LogLevel.Trace, "TRACE")]
    [InlineData(LogLevel.Debug, "DEBUG")]
    [InlineData(LogLevel.Information, "INFO")]
    [InlineData(LogLevel.Warning, "WARN")]
    [InlineData(LogLevel.Error, "ERROR")]
    [InlineData(LogLevel.Critical, "CRITICAL")]
    public void TheLevelReadsAsThePageShowsIt(LogLevel level, string expected)
        => Assert.Equal(expected, Line(level, "x").LevelText);

    [Fact]
    public void TheListShowsTheLastPartOfTheSourceAndTheRecordKeepsAllOfIt()
    {
        var record = Line(LogLevel.Information, "x");

        Assert.Equal("LaunchOrchestrator", record.ShortSource);
        Assert.Equal("Optima.Core.Launch.LaunchOrchestrator", record.Source);
        Assert.Equal(string.Empty, Line(LogLevel.Information, "Optima exited", source: string.Empty).ShortSource);
    }

    [Fact]
    public void TheLineCarriesTheShortFormOfItsException()
    {
        Assert.Equal("plain", Line(LogLevel.Information, "plain").Line);
        Assert.StartsWith(
            "Unexpected session failure during ApplyingPerformanceProfile (Win32Exception: PowerSetActiveScheme(8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c) failed (Win32 2",
            TheLaunchFailure().Line);
    }

    [Fact]
    public void AMessageThatAlreadySaysWhatTheExceptionSaysIsNotFollowedByItAgain()
    {
        var typed = OptimaException.From("POWER_PLAN_UNAVAILABLE", "This PC does not offer that power plan", "Windows does not list it.");

        var record = Line(LogLevel.Warning, "Session step skipped (POWER_PLAN_UNAVAILABLE): This PC does not offer that power plan", typed);

        Assert.Equal("Session step skipped (POWER_PLAN_UNAVAILABLE): This PC does not offer that power plan", record.Line);
    }

    [Fact]
    public void AWindowsErrorCodeIsAlwaysOnTheLine()
    {
        var record = Line(LogLevel.Error, "PowerSetActiveScheme failed", new Win32Exception(5, "PowerSetActiveScheme failed"));

        Assert.StartsWith("PowerSetActiveScheme failed (Win32Exception: PowerSetActiveScheme failed (Win32 5", record.Line);
    }

    [Fact]
    public void TheErrorCodeComesFromTheExceptionBeforeTheMessage()
    {
        var typed = OptimaException.From("VDD_NO_DISPLAY", "The virtual display did not appear", "It never attached.");
        var named = new Dictionary<string, string> { ["Code"] = "LAUNCH_STEP_SKIPPED" };

        Assert.Equal("VDD_NO_DISPLAY", Line(LogLevel.Error, "Session failed", typed, properties: named).ErrorCode);
        Assert.Equal("LAUNCH_STEP_SKIPPED", Line(LogLevel.Warning, "Session step skipped", properties: named).ErrorCode);
        Assert.Equal(string.Empty, Line(LogLevel.Information, "nothing coded").ErrorCode);
    }

    [Fact]
    public void AnExportLineHasItsDateItsFullSourceAndItsWholeException()
    {
        var export = LogReport.FormatExport([Line(LogLevel.Information, "Optima starting", source: string.Empty), TheLaunchFailure()], Alice);

        Assert.StartsWith("2026-10-05 22:09:08.968 -04:00 [INFO] : Optima starting", export);
        Assert.Contains(
            "2026-10-05 22:09:08.968 -04:00 [ERROR] Optima.Core.Launch.LaunchOrchestrator: Unexpected session failure during ApplyingPerformanceProfile",
            export);
        Assert.Contains("System.ComponentModel.Win32Exception", export);
        Assert.Contains("Win32 2: ", export);
    }

    [Fact]
    public void AnExportIsRedacted()
    {
        var record = Line(LogLevel.Warning, @"Could not read C:\Users\alice\AppData\Local\Optima\config.json on GAMING-PC, token=abc123");

        var export = LogReport.FormatExport([record], Alice);

        Assert.DoesNotContain("alice", export, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("GAMING-PC", export);
        Assert.DoesNotContain("abc123", export);
        Assert.Contains(@"C:\Users\[user]\AppData\Local\Optima\config.json on [machine], token=[REDACTED]", export);
    }

    [Fact]
    public void AReportStandsOnItsOwn()
    {
        var leadUp = new[]
        {
            Line(LogLevel.Information, "[Validating] Checking Google Play Games and Critical Ops…", secondsEarlier: 2),
            Line(LogLevel.Information, "[ApplyingPerformanceProfile] Applying performance profile…", secondsEarlier: 1),
        };

        var report = LogReport.FormatReport(TheLaunchFailure(), leadUp, Build, Alice);

        // Which build on which Windows, when, and from where.
        Assert.StartsWith("Optima 0.7.5 on Microsoft Windows NT 10.0.26100.0", report);
        Assert.Contains("2026-10-05 22:09:08.968 -04:00  ERROR  Optima.Core.Launch.LaunchOrchestrator", report);
        // What Windows said: the part the original log line never had.
        Assert.Contains("Windows error 2: ", report);
        // The whole exception, the message's arguments, and how it got there.
        Assert.Contains("Exception:", report);
        Assert.Contains("  Phase = ApplyingPerformanceProfile", report);
        Assert.Contains("Leading up to it:", report);
        Assert.Contains("22:09:06.968 INFO     LaunchOrchestrator: [Validating] Checking Google Play Games and Critical Ops…", report);
        Assert.True(report.IndexOf("[Validating]", StringComparison.Ordinal) < report.IndexOf("[ApplyingPerformanceProfile] Applying", StringComparison.Ordinal));
    }

    [Fact]
    public void AReportBringsTheGuidesEntryForAKnownCode()
    {
        var record = Line(LogLevel.Warning, "Session step skipped (POWER_PLAN_UNAVAILABLE): This PC does not offer that power plan",
            properties: new Dictionary<string, string> { ["Code"] = "POWER_PLAN_UNAVAILABLE" });

        var report = LogReport.FormatReport(record, [], Build, Alice);

        Assert.Contains("[POWER_PLAN_UNAVAILABLE] This PC does not offer that power plan", report);
        Assert.Contains("How to fix:", report);
        Assert.DoesNotContain("Leading up to it:", report);
    }

    [Fact]
    public void AReportNamesACodeTheGuideDoesNotKnow()
    {
        var record = Line(LogLevel.Error, "Something new",
            properties: new Dictionary<string, string> { ["Code"] = "NOT_IN_THE_GUIDE" });

        Assert.Contains("code: NOT_IN_THE_GUIDE", LogReport.FormatReport(record, [], Build, Alice));
    }

    [Fact]
    public void AReportIsRedacted()
    {
        Exception thrown;
        try
        {
            throw new IOException(@"The process cannot access C:\Users\alice\AppData\Local\Optima\sessions.db");
        }
        catch (IOException ex)
        {
            thrown = ex;
        }

        var report = LogReport.FormatReport(Line(LogLevel.Error, "Saving the session failed for alice", thrown), [], Build, Alice);

        Assert.DoesNotContain("alice", report, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(@"C:\Users\[user]\AppData\Local\Optima\sessions.db", report);
    }
}
