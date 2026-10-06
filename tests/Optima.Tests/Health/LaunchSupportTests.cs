using Microsoft.Extensions.Logging.Abstractions;
using Optima.Core.Abstractions;
using Optima.Core.Configuration;
using Optima.Core.Health;
using Optima.Core.Models;
using Xunit;

namespace Optima.Tests.Health;

public sealed class LaunchSupportTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "optima-launchsupport-" + Guid.NewGuid().ToString("N"));
    private readonly FakeMaintenance _display = new();

    private sealed class FakeMaintenance : IVirtualDisplayMaintenance
    {
        public int Reloads { get; private set; }
        public Exception? Throws { get; set; }

        public Task ReloadDriverAsync(CancellationToken ct = default)
        {
            Reloads++;
            return Throws is null ? Task.CompletedTask : Task.FromException(Throws);
        }
    }

    private sealed class FakeCheck(string name, CheckScope scope, Func<CancellationToken, Task<DiagnosticResult>> run) : IDiagnosticCheck
    {
        public int Runs { get; private set; }
        public string Name => name;
        public int Order => 1;
        public CheckScope Scope => scope;

        public Task<DiagnosticResult> RunAsync(CancellationToken ct = default)
        {
            Runs++;
            return run(ct);
        }
    }

    private static Task<DiagnosticResult> Failing(string code)
        => Task.FromResult(new DiagnosticResult { CheckName = code, Status = DiagnosticStatus.Fail, Reason = "broken", IssueCode = code });

    private (IssueEngine Engine, LaunchSupport Support) Create(TimeSpan? budget = null, params IDiagnosticCheck[] checks)
    {
        var engine = new IssueEngine(
            new IssueStateFile(new JsonStore(NullLogger<JsonStore>.Instance), Path.Combine(_directory, "issues.json"), NullLogger<IssueStateFile>.Instance),
            checks, NullLogger<IssueEngine>.Instance, notifyDelay: TimeSpan.Zero);
        return (engine, new LaunchSupport(engine, _display, NullLogger<LaunchSupport>.Instance, budget));
    }

    [Fact]
    public async Task ADisplayThatDidNotAppearGetsADriverReloadAndASecondTry()
    {
        var (engine, support) = Create();

        Assert.True(await support.TryRepairAsync("VDD_NO_DISPLAY"));

        Assert.Equal(1, _display.Reloads);
        // Written down like any other repair, as one a launch ran.
        var attempt = Assert.Single(engine.Attempts);
        Assert.Equal(("VDD_NO_DISPLAY", LaunchSupport.ReloadDriverRepair, RepairTrigger.Launch, RepairOutcome.Fixed),
            (attempt.IssueKey, attempt.RepairId, attempt.Trigger, attempt.Outcome));
    }

    [Theory]
    [InlineData("VDD_NOT_INSTALLED")]
    [InlineData("LAUNCH_FAILED")]
    [InlineData("DISPLAY_MODE_UNSUPPORTED")]
    public async Task AFaultAReloadCannotCureIsNotRetried(string code)
    {
        var (engine, support) = Create();

        Assert.False(await support.TryRepairAsync(code));

        Assert.Equal(0, _display.Reloads);
        Assert.Empty(engine.Attempts);
    }

    [Fact]
    public async Task AReloadThatFailsLeavesTheStepWithItsOwnError()
    {
        var (engine, support) = Create();
        _display.Throws = OptimaException.From("VDD_PIPE_DENIED", "Could not signal the virtual display driver.", "The prompt was declined.");

        Assert.False(await support.TryRepairAsync("VDD_NO_DISPLAY"));

        Assert.Equal(RepairOutcome.Failed, Assert.Single(engine.Attempts).Outcome);
    }

    [Fact]
    public async Task BeforeALaunchOnlyTheChecksMeantForItRun()
    {
        var before = new FakeCheck("Power Plan", CheckScope.Startup | CheckScope.Preflight, _ => Failing("POWER_PLAN_UNAVAILABLE"));
        var startupOnly = new FakeCheck("Virtualization", CheckScope.Startup, _ => Failing("VIRTUALIZATION_OFF"));
        var (engine, support) = Create(checks: [before, startupOnly]);

        await support.PreflightAsync();

        Assert.Equal((1, 0), (before.Runs, startupOnly.Runs));
        Assert.Equal("POWER_PLAN_UNAVAILABLE", Assert.Single(engine.Issues).Code);
    }

    [Fact]
    public async Task ACheckThatHangsIsLeftBehindNotWaitedFor()
    {
        var hangs = new FakeCheck("Power Plan", CheckScope.Preflight, async ct =>
        {
            await Task.Delay(Timeout.Infinite, ct);
            return await Failing("POWER_PLAN_UNAVAILABLE");
        });
        var (_, support) = Create(budget: TimeSpan.FromMilliseconds(50), hangs);

        // The player pressed PLAY: this must come back, and without an error.
        await support.PreflightAsync().WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(1, hangs.Runs);
    }

    [Fact]
    public async Task ACancelledLaunchCancelsItsChecksToo()
    {
        var hangs = new FakeCheck("Power Plan", CheckScope.Preflight, async ct =>
        {
            await Task.Delay(Timeout.Infinite, ct);
            return await Failing("POWER_PLAN_UNAVAILABLE");
        });
        var (_, support) = Create(budget: TimeSpan.FromMinutes(5), hangs);
        using var cancelled = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => support.PreflightAsync(cancelled.Token));
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
