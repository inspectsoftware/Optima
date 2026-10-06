using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Optima.Core.Abstractions;
using Optima.Core.Configuration;
using Optima.Core.Health;
using Optima.Core.Models;
using Xunit;

namespace Optima.Tests.Health;

public sealed class IssueEngineTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "optima-issues-" + Guid.NewGuid().ToString("N"));
    private readonly JsonStore _store = new(NullLogger<JsonStore>.Instance);
    private DateTimeOffset _now = new(2026, 10, 5, 22, 0, 0, TimeSpan.FromHours(-4));
    private int _announced;

    private string IssuesFile => Path.Combine(_directory, "issues.json");

    /// <summary>A run of the app: a new engine over the same file. It is not started, so no background scan interferes.</summary>
    private IssueEngine NewRun(params IDiagnosticCheck[] checks)
    {
        var engine = new IssueEngine(
            new IssueStateFile(_store, IssuesFile, NullLogger<IssueStateFile>.Instance),
            checks,
            NullLogger<IssueEngine>.Instance,
            clock: () => _now,
            notifyDelay: TimeSpan.Zero);
        engine.Changed += () => _announced++;
        return engine;
    }

    private static LogRecord Error(string template, string? message = null, Exception? exception = null,
        string source = "Optima.App.ViewModels.SessionsViewModel")
        => LogRuleTableTests.Line(LogLevel.Error, template, message, exception, source);

    private static LogRecord Info(string message)
        => LogRuleTableTests.Line(LogLevel.Information, message, source: "Optima.Core.Launch.LaunchOrchestrator");

    private sealed class FakeCheck(string name, CheckScope scope, Func<DiagnosticResult> run) : IDiagnosticCheck
    {
        public int Runs { get; private set; }
        public string Name => name;
        public int Order => 1;
        public CheckScope Scope => scope;

        public Task<DiagnosticResult> RunAsync(CancellationToken ct = default)
        {
            Runs++;
            return Task.FromResult(run());
        }
    }

    private static DiagnosticResult Result(DiagnosticStatus status, string? code, string reason = "because", string fix = "")
        => new() { CheckName = "Critical Ops", Status = status, Reason = reason, RecommendedFix = fix, IssueCode = code };

    [Fact]
    public void AnErrorInTheLogBecomesAnIssueWithWhatLedUpToIt()
    {
        var engine = NewRun();
        engine.Ingest(Info("[Validating] Checking Google Play Games and Critical Ops…"));
        engine.Ingest(Info("[ApplyingPerformanceProfile] Applying performance profile…"));

        engine.Ingest(Error("Failed restoring {What}, continuing with remaining restore steps", "Failed restoring power plan, continuing with remaining restore steps"));

        var issue = Assert.Single(engine.Issues);
        Assert.Equal("RESTORE_STEP_FAILED", issue.Code);
        Assert.Equal("A system setting could not be put back", issue.Title);
        Assert.Equal(1, issue.Count);
        Assert.Single(issue.Evidence);
        Assert.Equal(2, issue.LeadUp.Count);
        Assert.Contains("[Validating]", issue.LeadUp[0].Message);
        Assert.True(issue.NeedsAttention);
    }

    [Fact]
    public void ARepeatedErrorIsOneIssueWithItsFirstAndItsLatestOccurrences()
    {
        var engine = NewRun();
        var thrown = LogRuleTableTests.ThrownFromHere("the table is locked");
        for (var i = 1; i <= 20; i++)
        {
            engine.Ingest(Error("Saving the session failed on attempt {Attempt}", $"Saving the session failed on attempt {i}", thrown));
        }

        var issue = Assert.Single(engine.Issues);
        Assert.Equal(20, issue.Count);
        // Twenty lines, six kept: the first, because it is closest to the cause, and the latest five.
        Assert.Equal(6, issue.Evidence.Count);
        Assert.EndsWith("attempt 1", issue.Evidence[0].Message);
        Assert.EndsWith("attempt 16", issue.Evidence[1].Message);
        Assert.EndsWith("attempt 20", issue.Evidence[^1].Message);
        Assert.Contains("attempt 20", issue.Detail);
    }

    [Fact]
    public void AStormStaysBounded()
    {
        var engine = NewRun();
        for (var i = 0; i < IssueEngine.MaxUnclassified + 40; i++)
        {
            // Every one a different call site: a distinct template each.
            engine.Ingest(Error($"Failure number {i} in {{Place}}", $"Failure number {i} in the storm"));
        }

        var issues = engine.Issues;
        Assert.Equal(IssueEngine.MaxUnclassified + 1, issues.Count);
        var overflow = Assert.Single(issues, i => i.Code == IssueEngine.OverflowCode);
        Assert.Equal(40, overflow.Count);
        Assert.Equal("More errors than the list can show", overflow.Title);
    }

    [Fact]
    public void TheEnginesOwnLogsNeverOpenIssues()
    {
        var engine = NewRun();

        engine.Ingest(Error("Check {Check} could not run", source: "Optima.Core.Health.IssueEngine"));
        engine.Ingest(Error("The issue state could not be saved", source: "Optima.Core.Health.IssueStateFile"));

        Assert.Empty(engine.Issues);
        Assert.Equal(0, _announced);
    }

    [Fact]
    public void ADamagedIssueFileDoesNotRaiseAnIssueAboutItself()
    {
        var engine = NewRun();
        LogRecord Corrupt(string path) => LogRuleTableTests.Line(
            LogLevel.Error, "Corrupt JSON at {Path}; renaming aside and using defaults", source: "Optima.Core.Configuration.JsonStore")
            with { Properties = new Dictionary<string, string> { ["Path"] = path } };

        engine.Ingest(Corrupt(IssuesFile));
        Assert.Empty(engine.Issues);

        // Any other file being damaged is exactly what the issue is for.
        engine.Ingest(Corrupt(Path.Combine(_directory, "config.json")));
        Assert.Equal("SETTINGS_CORRUPT", Assert.Single(engine.Issues).Code);
    }

    [Fact]
    public void AFailingCheckOpensAnIssueAndAPassingOneClosesIt()
    {
        var engine = NewRun();

        engine.Report(Result(DiagnosticStatus.Fail, "GAME_NOT_FOUND", "Critical Ops is not installed in Google Play Games.", "Open Google Play Games and install Critical Ops."));

        var issue = Assert.Single(engine.Issues);
        Assert.Equal(IssueSeverity.Error, issue.Severity);
        Assert.Equal("Critical Ops is not installed in Google Play Games", issue.Title);
        Assert.Equal("Critical Ops is not installed in Google Play Games. Open Google Play Games and install Critical Ops.", issue.Detail);
        Assert.Empty(issue.Evidence);

        engine.Report(Result(DiagnosticStatus.Pass, "GAME_NOT_FOUND"));
        Assert.Empty(engine.Issues);
    }

    [Fact]
    public void APassingCheckClosesAnIssueTheLogOpened()
    {
        var engine = NewRun();
        engine.Ingest(LogRuleTableTests.Line(LogLevel.Error, "Session failed: {Code}", "Session failed: GAME_NOT_FOUND",
            OptimaException.From("GAME_NOT_FOUND", "Critical Ops is not installed in Google Play Games", "Detection found nothing.")));
        Assert.Single(engine.Issues);

        // The check is the proof that it is gone, whichever way the issue came in.
        engine.Report(Result(DiagnosticStatus.Pass, "GAME_NOT_FOUND"));

        Assert.Empty(engine.Issues);
    }

    [Fact]
    public void ACheckThatKeepsFailingIsTheSameOccurrenceSeenAgain()
    {
        var engine = NewRun();
        engine.Report(Result(DiagnosticStatus.Warning, "HELPER_MISSING", "missing"));
        var first = _now;
        _now = _now.AddMinutes(10);

        engine.Report(Result(DiagnosticStatus.Warning, "HELPER_MISSING", "still missing"));

        var issue = Assert.Single(engine.Issues);
        Assert.Equal(1, issue.Count);
        Assert.Equal(first, issue.FirstSeen);
        Assert.Equal(_now, issue.LastSeen);
        Assert.Equal("still missing", issue.Detail);
        Assert.Equal(IssueSeverity.Warning, issue.Severity);
    }

    [Fact]
    public void AResultWithoutAnIssueCodeIsAdviceAndStaysOffTheList()
    {
        var engine = NewRun();

        engine.Report(Result(DiagnosticStatus.Warning, code: null, "Fastest active display runs at 60 Hz."));
        engine.Report(Result(DiagnosticStatus.Skipped, "GAME_NOT_FOUND"));

        Assert.Empty(engine.Issues);
    }

    [Fact]
    public void ANoteIsListedAndDoesNotAskForAttention()
    {
        var engine = NewRun();

        engine.Report(Result(DiagnosticStatus.Warning, "POWER_PLAN_UNAVAILABLE", "This PC does not offer High performance."));

        var issue = Assert.Single(engine.Issues);
        Assert.Equal(IssueSeverity.Note, issue.Severity);
        Assert.False(issue.NeedsAttention);
    }

    [Fact]
    public void ADismissedIssueComesBackWhenItHappensAgain()
    {
        var engine = NewRun();
        var line = Error("Failed restoring {What}, continuing with remaining restore steps");
        engine.Ingest(line);

        engine.Dismiss("RESTORE_STEP_FAILED");
        Assert.Empty(engine.Issues);

        engine.Ingest(line);
        Assert.Equal(1, Assert.Single(engine.Issues).Count);
    }

    [Fact]
    public async Task AnIgnoredIssueStaysAwayInTheNextRunToo()
    {
        var line = Error("Failed restoring {What}, continuing with remaining restore steps");
        var engine = NewRun();
        engine.Ingest(line);

        await engine.IgnoreAsync("RESTORE_STEP_FAILED");
        engine.Ingest(line);
        Assert.Empty(engine.Issues);
        var ignored = Assert.Single(engine.Ignored);
        Assert.Equal("A system setting could not be put back", ignored.Title);

        var nextRun = NewRun();
        nextRun.Start();
        nextRun.Ingest(line);
        nextRun.Report(Result(DiagnosticStatus.Fail, "RESTORE_STEP_FAILED"));
        Assert.Empty(nextRun.Issues);
        Assert.Single(nextRun.Ignored);
        nextRun.Dispose();
    }

    [Fact]
    public async Task AnIssueThatIsNoLongerIgnoredIsRaisedAgain()
    {
        var line = Error("Failed restoring {What}, continuing with remaining restore steps");
        var engine = NewRun();
        engine.Ingest(line);
        await engine.IgnoreAsync("RESTORE_STEP_FAILED");

        await engine.StopIgnoringAsync("RESTORE_STEP_FAILED");
        engine.Ingest(line);

        Assert.Single(engine.Issues);
        Assert.Empty(engine.Ignored);

        using var nextRun = NewRun();
        nextRun.Start();
        Assert.Empty(nextRun.Ignored);
    }

    [Fact]
    public void TheMostSeriousIssueIsFirstAndTheMostRecentFirstAmongEquals()
    {
        var engine = NewRun();
        engine.Report(Result(DiagnosticStatus.Warning, "POWER_PLAN_UNAVAILABLE"));
        engine.Ingest(Error("Failed restoring {What}, continuing with remaining restore steps") with { Timestamp = _now });
        engine.Ingest(Error("Corrupt JSON at {Path}; renaming aside and using defaults") with { Timestamp = _now.AddMinutes(1) });
        engine.Ingest(LogRuleTableTests.Line(LogLevel.Critical, "Unhandled UI exception"));

        Assert.Equal(
            ["OPTIMA_CRASHED", "SETTINGS_CORRUPT", "RESTORE_STEP_FAILED", "POWER_PLAN_UNAVAILABLE"],
            engine.Issues.Select(i => i.Code));
    }

    [Fact]
    public void EveryChangeToTheListIsAnnounced()
    {
        var engine = NewRun();

        engine.Ingest(Info("nothing to see"));
        Assert.Equal(0, _announced);

        engine.Ingest(Error("Failed restoring {What}, continuing with remaining restore steps"));
        engine.Report(Result(DiagnosticStatus.Fail, "GAME_NOT_FOUND"));
        engine.Report(Result(DiagnosticStatus.Pass, "GAME_NOT_FOUND"));
        engine.Dismiss("RESTORE_STEP_FAILED");
        // Nothing left to dismiss or to close: nothing changed, nothing announced.
        engine.Dismiss("RESTORE_STEP_FAILED");
        engine.Report(Result(DiagnosticStatus.Pass, "GAME_NOT_FOUND"));

        Assert.Equal(4, _announced);
    }

    [Fact]
    public async Task AScanAtStartupRunsOnlyTheChecksThatMayRunUnasked()
    {
        var quiet = new FakeCheck("Disk Space", CheckScope.Startup, () => Result(DiagnosticStatus.Fail, "DISK_SPACE_LOW"));
        var asked = new FakeCheck("Optima Overhead", CheckScope.OnDemand, () => Result(DiagnosticStatus.Fail, "HELPER_MISSING"));
        var engine = NewRun(quiet, asked);

        await engine.ScanAsync(CheckScope.Startup);

        Assert.Equal((1, 0), (quiet.Runs, asked.Runs));
        Assert.Equal("DISK_SPACE_LOW", Assert.Single(engine.Issues).Code);

        // Asked for from the page, every check runs.
        await engine.ScanAsync(CheckScope.OnDemand);
        Assert.Equal((2, 1), (quiet.Runs, asked.Runs));
        Assert.Equal(2, engine.Issues.Count);
    }

    [Fact]
    public async Task ACheckThatThrowsDoesNotStopTheScan()
    {
        var broken = new FakeCheck("Virtualization", CheckScope.Startup, () => throw new InvalidOperationException("WMI is not answering"));
        var fine = new FakeCheck("Disk Space", CheckScope.Startup, () => Result(DiagnosticStatus.Fail, "DISK_SPACE_LOW"));
        var engine = NewRun(broken, fine);

        await engine.ScanAsync(CheckScope.Startup);

        Assert.Equal("DISK_SPACE_LOW", Assert.Single(engine.Issues).Code);
    }

    [Fact]
    public void AnIssueCopiesAsAReportThatStandsOnItsOwn()
    {
        var engine = NewRun();
        engine.Ingest(Info("[ApplyingPerformanceProfile] Applying performance profile…"));
        engine.Ingest(Error("Failed restoring {What}, continuing with remaining restore steps",
            "Failed restoring power plan, continuing with remaining restore steps",
            new System.ComponentModel.Win32Exception(5, @"PowerSetActiveScheme failed for C:\Users\alice\plan")));

        var report = IssueReport.Format(
            Assert.Single(engine.Issues),
            new ReportEnvironment("0.7.5", "Microsoft Windows NT 10.0.26100.0"),
            new RedactionIdentity("alice", "GAMING-PC"));

        Assert.StartsWith("Optima 0.7.5 on Microsoft Windows NT 10.0.26100.0", report);
        Assert.Contains("[ERROR] A system setting could not be put back", report);
        Assert.Contains("code: RESTORE_STEP_FAILED, seen once, first 2026-10-05 22:09:08.000 -04:00", report);
        Assert.Contains("How to fix:", report);
        Assert.Contains("Occurrences:", report);
        Assert.Contains("Win32 5: ", report);
        Assert.Contains("Leading up to it:", report);
        Assert.Contains("[ApplyingPerformanceProfile]", report);
        Assert.DoesNotContain("alice", report, StringComparison.OrdinalIgnoreCase);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}
