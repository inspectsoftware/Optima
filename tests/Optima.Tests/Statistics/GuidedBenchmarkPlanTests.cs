using Optima.Core.Statistics;
using Xunit;

namespace Optima.Tests.Statistics;

public sealed class GuidedBenchmarkPlanTests
{
    private static GuidedBenchmarkPlan Plan(int runs = 2)
    {
        var plan = new GuidedBenchmarkPlan("A", "B", runs);
        plan.SetBaseline(["game-dvr-off"], "hashA", "hashB");
        return plan;
    }

    private static BenchmarkRunOutcome CompleteRun(GuidedBenchmarkPlan plan, bool success = true, bool hasData = true, long id = 1)
    {
        plan.BeginRun();
        return plan.ReportResult(success, hasData, id);
    }

    [Fact]
    public void RunsAlternateBetweenProfiles()
    {
        var plan = Plan(runs: 2);
        Assert.Equal("A", plan.NextProfileName);
        CompleteRun(plan, id: 1);
        Assert.Equal("B", plan.NextProfileName);
        CompleteRun(plan, id: 2);
        Assert.Equal("A", plan.NextProfileName);
        CompleteRun(plan, id: 3);
        Assert.Equal("B", plan.NextProfileName);
        CompleteRun(plan, id: 4);

        Assert.Equal(BenchmarkPlanState.Completed, plan.State);
        Assert.Null(plan.NextProfileName);
        Assert.Equal([1L, 3L], plan.AcceptedSessionIdsA);
        Assert.Equal([2L, 4L], plan.AcceptedSessionIdsB);
    }

    [Fact]
    public void FailedRunIsRequeuedForTheSameProfile()
    {
        var plan = Plan();
        Assert.Equal(BenchmarkRunOutcome.Retry, CompleteRun(plan, success: false));
        Assert.Equal("A", plan.NextProfileName);
        Assert.Equal(0, plan.CompletedRuns);
    }

    [Fact]
    public void SuccessfulRunWithoutDataIsRequeued()
    {
        var plan = Plan();
        Assert.Equal(BenchmarkRunOutcome.Retry, CompleteRun(plan, success: true, hasData: false));
        Assert.Equal("A", plan.NextProfileName);
    }

    [Fact]
    public void TooManyFailuresAbortThePlan()
    {
        var plan = Plan(runs: 1); // total 2, allowance 4 => 6 attempts max
        for (var i = 0; i < 5; i++)
        {
            Assert.Equal(BenchmarkRunOutcome.Retry, CompleteRun(plan, success: false));
        }
        Assert.Equal(BenchmarkRunOutcome.Aborted, CompleteRun(plan, success: false));
        Assert.Equal(BenchmarkPlanState.Aborted, plan.State);
    }

    [Fact]
    public void TweakDriftIsDetected()
    {
        var plan = Plan();
        var drift = plan.CheckDrift(["game-dvr-off", "hags-on"], "hashA", "hashB");
        Assert.True(drift.HasDrift);
        Assert.Contains("hags-on", drift.Message);
    }

    [Fact]
    public void ProfileEditIsDetected()
    {
        var plan = Plan();
        var drift = plan.CheckDrift(["game-dvr-off"], "hashA", "DIFFERENT");
        Assert.True(drift.HasDrift);
        Assert.Contains("B", drift.Message);
    }

    [Fact]
    public void UnchangedConfigurationHasNoDrift()
        => Assert.False(Plan().CheckDrift(["game-dvr-off"], "hashA", "hashB").HasDrift);

    [Fact]
    public void ProgressStringsTrackTheState()
    {
        var plan = Plan(runs: 2);
        Assert.Equal("run 1 of 4 · next: A", plan.Progress);
        plan.BeginRun();
        Assert.Equal("run 1 of 4 in progress · profile A", plan.Progress);
        plan.ReportResult(true, true, 1);
        Assert.Equal("run 2 of 4 · next: B", plan.Progress);
    }

    [Fact]
    public void BeginRunTwiceThrows()
    {
        var plan = Plan();
        plan.BeginRun();
        Assert.Throws<InvalidOperationException>(plan.BeginRun);
    }
}
