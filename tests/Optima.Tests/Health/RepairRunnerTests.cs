using Microsoft.Extensions.Logging.Abstractions;
using Optima.Core.Abstractions;
using Optima.Core.Configuration;
using Optima.Core.Health;
using Optima.Core.Models;
using Xunit;

namespace Optima.Tests.Health;

public sealed class RepairRunnerTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "optima-repairs-" + Guid.NewGuid().ToString("N"));
    private readonly JsonStore _store = new(NullLogger<JsonStore>.Instance);
    private DateTimeOffset _now = new(2026, 10, 6, 21, 0, 0, TimeSpan.FromHours(3));
    private RepairEnvironment _environment = new(AutoRepairMode.SafeOnly, GameRunning: false, WindowVisible: true, HelperConnected: false, ElevationDeclinedThisRun: false);

    private readonly FakeRepair _start = new("start-platform", RepairTier.Safe);
    private readonly FakeRepair _restart = new("restart-platform", RepairTier.Disruptive);
    private readonly FakeRepair _enable = new("enable-hypervisor", RepairTier.Elevated);
    private readonly FakeRepair _discard = new("discard-display-restore", RepairTier.Safe);
    private readonly FakeCheck _hypervisor = new("Windows Hypervisor", "HYPERVISOR_OFF");
    private readonly FakeCheck _pendingRestore = new("Pending Display Restore", "VDD_RESTORE_PENDING");

    private sealed class FakeRepair(string id, RepairTier tier) : IRepairAction
    {
        public int Runs { get; private set; }
        public RepairResult Result { get; set; } = new(RepairOutcome.Fixed, "done by the fake");
        public Exception? Throws { get; set; }
        public string Id => id;
        public string Title => id.Replace('-', ' ');
        public string Changes => "nothing, it is a fake";
        public RepairTier Tier => tier;

        public Task<RepairResult> RunAsync(Issue issue, CancellationToken ct = default)
        {
            Runs++;
            return Throws is null ? Task.FromResult(Result) : Task.FromException<RepairResult>(Throws);
        }
    }

    private sealed class FakeCheck(string name, string code) : IDiagnosticCheck
    {
        public DiagnosticStatus Status { get; set; } = DiagnosticStatus.Fail;
        public string Name => name;
        public int Order => 1;

        public Task<DiagnosticResult> RunAsync(CancellationToken ct = default)
            => Task.FromResult(new DiagnosticResult { CheckName = name, Status = Status, Reason = "says the fake", IssueCode = code });
    }

    private (IssueEngine Engine, RepairRunner Runner) NewRun()
    {
        var engine = new IssueEngine(
            new IssueStateFile(_store, Path.Combine(_directory, "issues.json"), NullLogger<IssueStateFile>.Instance),
            [_hypervisor, _pendingRestore],
            NullLogger<IssueEngine>.Instance,
            clock: () => _now,
            notifyDelay: TimeSpan.Zero);
        var runner = new RepairRunner(engine, [_start, _restart, _enable, _discard], () => _environment,
            NullLogger<RepairRunner>.Instance, () => _now);
        return (engine, runner);
    }

    private static DiagnosticResult Failing(string code)
        => new() { CheckName = "a check", Status = DiagnosticStatus.Fail, Reason = "it is broken", IssueCode = code };

    [Fact]
    public async Task ASafeRepairRunsByItselfWhenTheIssueAppears()
    {
        var (engine, runner) = NewRun();
        engine.Report(Failing("LAUNCH_FAILED"));

        await runner.EvaluateAsync();

        Assert.Equal((1, 0), (_start.Runs, _restart.Runs));
        var issue = Assert.Single(engine.Issues);
        Assert.Equal(IssueState.Repaired, issue.State);
        Assert.StartsWith("Optima ran \"start platform\" at 21:00: done.", issue.RepairNote);
        // Still on the list, so what was done can be seen; no longer counted as a problem.
        Assert.False(issue.NeedsAttention);

        var attempt = Assert.Single(engine.Attempts);
        Assert.Equal(("LAUNCH_FAILED", "start-platform", RepairTrigger.Background, RepairOutcome.Fixed),
            (attempt.IssueKey, attempt.RepairId, attempt.Trigger, attempt.Outcome));
    }

    [Fact]
    public async Task WhenTheIssueComesBackTheLadderIsClimbedOneRungAtATime()
    {
        _environment = _environment with { Mode = AutoRepairMode.Escalate };
        var (engine, runner) = NewRun();

        engine.Report(Failing("LAUNCH_FAILED"));
        await runner.EvaluateAsync();
        Assert.Equal((1, 0), (_start.Runs, _restart.Runs));

        // It failed again two minutes later: the safe repair gets its second try.
        _now = _now.AddMinutes(2);
        engine.Report(Failing("LAUNCH_FAILED"));
        Assert.Equal(IssueState.Open, Assert.Single(engine.Issues).State);
        await runner.EvaluateAsync();
        Assert.Equal((2, 0), (_start.Runs, _restart.Runs));

        // And again: the safe rung is used up, so the interrupting one is next.
        _now = _now.AddMinutes(2);
        engine.Report(Failing("LAUNCH_FAILED"));
        await runner.EvaluateAsync();
        Assert.Equal((2, 1), (_start.Runs, _restart.Runs));

        // And once more: nothing is left for Optima to try, and it does not loop.
        _now = _now.AddMinutes(2);
        engine.Report(Failing("LAUNCH_FAILED"));
        await runner.EvaluateAsync();
        Assert.Equal((2, 1), (_start.Runs, _restart.Runs));
        Assert.True(Assert.Single(engine.Issues).NeedsAttention);
    }

    [Fact]
    public async Task InSafeOnlyModeTheInterruptingRungWaitsOnTheCardForAClick()
    {
        var (engine, runner) = NewRun();
        foreach (var _ in Enumerable.Range(0, 3))
        {
            engine.Report(Failing("LAUNCH_FAILED"));
            await runner.EvaluateAsync();
            _now = _now.AddMinutes(2);
        }

        Assert.Equal((2, 0), (_start.Runs, _restart.Runs));
        var issue = Assert.Single(engine.Issues);
        Assert.Equal(IssueState.NeedsUser, issue.State);
        Assert.Contains("waits for you", issue.RepairNote);
    }

    [Fact]
    public async Task NothingRunsWhileTheGameIsRunningAndItRunsOnceTheGameCloses()
    {
        _environment = _environment with { GameRunning = true };
        var (engine, runner) = NewRun();
        engine.Report(Failing("LAUNCH_FAILED"));

        await runner.EvaluateAsync();
        Assert.Equal(0, _start.Runs);
        Assert.Contains("game", Assert.Single(engine.Issues).RepairNote);

        _environment = _environment with { GameRunning = false };
        await runner.EvaluateAsync();
        Assert.Equal(1, _start.Runs);
    }

    [Fact]
    public async Task ARepairThePlayerStartsRunsWhateverTheMode()
    {
        _environment = _environment with { Mode = AutoRepairMode.Off };
        var (engine, runner) = NewRun();
        engine.Report(Failing("LAUNCH_FAILED"));
        await runner.EvaluateAsync();
        Assert.Equal(0, _start.Runs);

        var result = await runner.RunAsync("LAUNCH_FAILED", "restart-platform");

        Assert.Equal(RepairOutcome.Fixed, result.Outcome);
        Assert.Equal(1, _restart.Runs);
        Assert.Equal(RepairTrigger.User, Assert.Single(engine.Attempts).Trigger);
        Assert.StartsWith("You ran \"restart platform\"", Assert.Single(engine.Issues).RepairNote);
    }

    [Fact]
    public async Task ARepairIsProvedByTheCheckNotByItsOwnWord()
    {
        var (engine, runner) = NewRun();
        engine.Report(Failing("HYPERVISOR_OFF"));

        // The repair says it worked. The check that raised the issue still fails.
        await runner.RunAsync("HYPERVISOR_OFF", "enable-hypervisor");
        var issue = Assert.Single(engine.Issues);
        Assert.Equal(IssueState.Open, issue.State);
        Assert.Contains("the check still fails", issue.RepairNote);
        Assert.True(issue.NeedsAttention);

        _hypervisor.Status = DiagnosticStatus.Pass;
        await runner.RunAsync("HYPERVISOR_OFF", "enable-hypervisor");
        Assert.Equal(IssueState.Repaired, Assert.Single(engine.Issues).State);
    }

    [Fact]
    public async Task ARepairThatThrowsIsAFailedAttemptNotACrash()
    {
        var (engine, runner) = NewRun();
        _start.Throws = new InvalidOperationException("the bootstrapper is gone");
        engine.Report(Failing("LAUNCH_FAILED"));

        await runner.EvaluateAsync();

        var attempt = Assert.Single(engine.Attempts);
        Assert.Equal(RepairOutcome.Failed, attempt.Outcome);
        Assert.Contains("the bootstrapper is gone", attempt.Summary);
        var issue = Assert.Single(engine.Issues);
        Assert.Equal(IssueState.Open, issue.State);
        Assert.Contains("it did not work", issue.RepairNote);
    }

    [Fact]
    public async Task ARepairThatIsOnlyEverRunByHandIsNeverRunUnasked()
    {
        _environment = _environment with { Mode = AutoRepairMode.Escalate };
        var (engine, runner) = NewRun();
        engine.Report(Failing("VDD_RESTORE_PENDING"));

        await runner.EvaluateAsync();
        Assert.Equal(0, _discard.Runs);

        // Offered as a button, and it runs when pressed. The check then proves it.
        Assert.Equal("discard-display-restore", Assert.Single(runner.ActionsFor(Assert.Single(engine.Issues))).Id);
        _pendingRestore.Status = DiagnosticStatus.Pass;
        await runner.RunAsync("VDD_RESTORE_PENDING", "discard-display-restore");
        Assert.Equal(1, _discard.Runs);
        Assert.Equal(IssueState.Repaired, Assert.Single(engine.Issues).State);
    }

    [Fact]
    public async Task AnIssueOffersItsLadderAndItsByHandRepairsAsButtons()
    {
        var (engine, runner) = NewRun();
        engine.Report(Failing("GAME_START_TIMEOUT"));
        engine.Report(Failing("GAME_NOT_FOUND"));

        var buttons = engine.Issues.ToDictionary(i => i.Code, i => runner.ActionsFor(i).Select(a => a.Id).ToList());

        Assert.Equal(["start-platform", "restart-platform"], buttons["GAME_START_TIMEOUT"]);
        Assert.Empty(buttons["GAME_NOT_FOUND"]);

        // On a timeout the platform is usually sitting on a sign-in: stopping it is never automatic.
        _environment = _environment with { Mode = AutoRepairMode.Escalate };
        foreach (var _ in Enumerable.Range(0, 4))
        {
            await runner.EvaluateAsync();
            _now = _now.AddMinutes(2);
            engine.Report(Failing("GAME_START_TIMEOUT"));
        }
        Assert.Equal(0, _restart.Runs);
    }

    [Fact]
    public async Task WhatWasTriedIsRememberedByTheNextRun()
    {
        var (engine, runner) = NewRun();
        engine.Report(Failing("LAUNCH_FAILED"));
        await runner.EvaluateAsync();
        _now = _now.AddMinutes(2);
        engine.Report(Failing("LAUNCH_FAILED"));
        await runner.EvaluateAsync();
        Assert.Equal(2, _start.Runs);

        // Optima is restarted and the launch fails again: the safe rung is not tried as if it were new.
        var (nextEngine, nextRunner) = NewRun();
        nextEngine.Start();
        Assert.Equal(2, nextEngine.Attempts.Count);
        _now = _now.AddMinutes(30);
        nextEngine.Report(Failing("LAUNCH_FAILED"));
        await nextRunner.EvaluateAsync();

        Assert.Equal(2, _start.Runs);
        Assert.Equal(IssueState.NeedsUser, Assert.Single(nextEngine.Issues).State);
        nextEngine.Dispose();
    }

    [Fact]
    public async Task EveryRepairThatRanIsAnnounced()
    {
        var (engine, runner) = NewRun();
        var announced = new List<RepairAttempt>();
        runner.Attempted += announced.Add;
        engine.Report(Failing("LAUNCH_FAILED"));

        await runner.EvaluateAsync();
        await runner.RunAsync("LAUNCH_FAILED", "restart-platform");

        Assert.Equal(["start-platform", "restart-platform"], announced.Select(a => a.RepairId));
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
