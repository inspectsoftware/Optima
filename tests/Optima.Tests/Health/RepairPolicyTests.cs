using Optima.Core.Health;
using Optima.Core.Models;
using Xunit;

namespace Optima.Tests.Health;

public sealed class RepairPolicyTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 6, 21, 0, 0, TimeSpan.FromHours(3));

    private static readonly RepairStep StartPlatform = new("start-platform", RepairTier.Safe);
    private static readonly RepairStep RestartPlatform = new("restart-platform", RepairTier.Disruptive);
    private static readonly RepairStep EnableHypervisor = new("enable-hypervisor", RepairTier.Elevated);

    private static RepairRequest Request(
        IReadOnlyList<RepairStep> ladder,
        AutoRepairMode mode = AutoRepairMode.Escalate,
        bool gameRunning = false,
        bool windowVisible = true,
        bool helperConnected = false,
        bool declined = false,
        int lastHour = 0,
        TimeSpan? lastDisruptive = null,
        params RepairAttempt[] history)
        => new()
        {
            Ladder = ladder,
            History = history,
            Mode = mode,
            Now = Now,
            GameRunning = gameRunning,
            WindowVisible = windowVisible,
            HelperConnected = helperConnected,
            ElevationDeclinedThisRun = declined,
            AutomaticRepairsLastHour = lastHour,
            LastDisruptiveAt = lastDisruptive is { } ago ? Now - ago : null,
        };

    private static RepairAttempt Tried(RepairStep step, TimeSpan ago,
        RepairOutcome outcome = RepairOutcome.Failed, RepairTrigger trigger = RepairTrigger.Background)
        => new("LAUNCH_FAILED", step.Id, step.Tier, trigger, Now - ago, outcome, "it did not help");

    private static readonly TimeSpan Minutes5 = TimeSpan.FromMinutes(5);

    [Fact]
    public void SwitchedOffOptimaRepairsNothing()
    {
        var decision = RepairPolicy.Decide(Request([StartPlatform], mode: AutoRepairMode.Off));

        Assert.Equal(RepairVerdict.Stop, decision.Verdict);
    }

    [Fact]
    public void AnIssueWithoutALadderIsNotOptimasToRepair()
        => Assert.Equal(RepairVerdict.Stop, RepairPolicy.Decide(Request([])).Verdict);

    [Theory]
    [InlineData(AutoRepairMode.SafeOnly)]
    [InlineData(AutoRepairMode.Escalate)]
    public void ASafeRepairRunsUnaskedEvenWithTheWindowHidden(AutoRepairMode mode)
    {
        var decision = RepairPolicy.Decide(Request([StartPlatform, RestartPlatform], mode, windowVisible: false));

        Assert.Equal((RepairVerdict.Run, StartPlatform), (decision.Verdict, decision.Step));
    }

    [Fact]
    public void NothingRunsWhileAGameIsRunning()
    {
        foreach (var step in new[] { StartPlatform, RestartPlatform, EnableHypervisor })
        {
            var decision = RepairPolicy.Decide(Request([step], gameRunning: true, helperConnected: true));

            Assert.Equal(RepairVerdict.Defer, decision.Verdict);
            Assert.Contains("game", decision.Reason);
        }
    }

    [Fact]
    public void ASafeRepairGetsASecondTryAMinuteLater()
    {
        var tooSoon = RepairPolicy.Decide(Request([StartPlatform, RestartPlatform],
            history: Tried(StartPlatform, TimeSpan.FromSeconds(10))));
        var later = RepairPolicy.Decide(Request([StartPlatform, RestartPlatform],
            history: Tried(StartPlatform, TimeSpan.FromMinutes(2))));

        Assert.Equal(RepairVerdict.Defer, tooSoon.Verdict);
        Assert.Equal((RepairVerdict.Run, StartPlatform), (later.Verdict, later.Step));
    }

    [Fact]
    public void TheNextRungIsReachedOnlyWhenTheOneBelowWasUsedUp()
    {
        var once = RepairPolicy.Decide(Request([StartPlatform, RestartPlatform],
            history: Tried(StartPlatform, Minutes5)));
        var twice = RepairPolicy.Decide(Request([StartPlatform, RestartPlatform],
            history: [Tried(StartPlatform, TimeSpan.FromMinutes(8)), Tried(StartPlatform, Minutes5)]));

        Assert.Equal(StartPlatform, once.Step);
        Assert.Equal((RepairVerdict.Run, RestartPlatform), (twice.Verdict, twice.Step));
    }

    [Fact]
    public void ARepairThatSaidItWorkedStillCountsWhenTheIssueIsBack()
    {
        // The policy is only asked about open issues: a rung that reported success and left the
        // issue open, or open again, is a rung that did not help.
        var decision = RepairPolicy.Decide(Request([StartPlatform, RestartPlatform], history:
            [Tried(StartPlatform, TimeSpan.FromMinutes(8), RepairOutcome.Fixed), Tried(StartPlatform, Minutes5, RepairOutcome.Fixed)]));

        Assert.Equal(RestartPlatform, decision.Step);
    }

    [Theory]
    [InlineData(RepairTier.Disruptive)]
    [InlineData(RepairTier.Elevated)]
    public void InSafeOnlyModeAnythingMoreWaitsForAClick(RepairTier tier)
    {
        var step = new RepairStep("x", tier);

        var decision = RepairPolicy.Decide(Request([step], AutoRepairMode.SafeOnly));

        Assert.Equal((RepairVerdict.AskUser, step), (decision.Verdict, decision.Step));
    }

    [Fact]
    public void AnInterruptingRepairIsNotRunOutOfSight()
    {
        var hidden = RepairPolicy.Decide(Request([RestartPlatform], windowVisible: false));
        var visible = RepairPolicy.Decide(Request([RestartPlatform]));

        Assert.Equal(RepairVerdict.Defer, hidden.Verdict);
        Assert.Equal((RepairVerdict.Run, RestartPlatform), (visible.Verdict, visible.Step));
    }

    [Fact]
    public void InterruptingRepairsAreKeptTenMinutesApart()
    {
        var soon = RepairPolicy.Decide(Request([RestartPlatform], lastDisruptive: TimeSpan.FromMinutes(3)));
        var later = RepairPolicy.Decide(Request([RestartPlatform], lastDisruptive: TimeSpan.FromMinutes(11)));

        Assert.Equal(RepairVerdict.Defer, soon.Verdict);
        Assert.Equal(RepairVerdict.Run, later.Verdict);
    }

    [Fact]
    public void AnAdministratorPromptIsNeverRaisedOverAHiddenWindow()
    {
        var decision = RepairPolicy.Decide(Request([EnableHypervisor], windowVisible: false));

        Assert.Equal(RepairVerdict.Defer, decision.Verdict);
        Assert.Contains("hidden window", decision.Reason);
    }

    [Fact]
    public void ADeclinedPromptStopsTheLadderUntilThePlayerAsks()
    {
        var decision = RepairPolicy.Decide(Request([EnableHypervisor], declined: true));

        Assert.Equal((RepairVerdict.AskUser, EnableHypervisor), (decision.Verdict, decision.Step));
        Assert.Contains("declined", decision.Reason);
    }

    [Fact]
    public void AnIssueGetsOnePromptADay()
    {
        var other = new RepairStep("restart-helper", RepairTier.Elevated);

        var decision = RepairPolicy.Decide(Request([EnableHypervisor, other], history: Tried(EnableHypervisor, TimeSpan.FromHours(3))));

        // The first elevated rung is used up; the second would be a second prompt the same day.
        Assert.Equal((RepairVerdict.AskUser, other), (decision.Verdict, decision.Step));
    }

    [Fact]
    public void WithTheHelperAlreadyRunningAnElevatedRepairPromptsForNothing()
    {
        // No prompt will appear, so the rules about prompts do not apply; the ones about
        // interrupting still do.
        var connected = RepairPolicy.Decide(Request([EnableHypervisor], helperConnected: true, declined: true));
        var hidden = RepairPolicy.Decide(Request([EnableHypervisor], helperConnected: true, windowVisible: false));

        Assert.Equal((RepairVerdict.Run, EnableHypervisor), (connected.Verdict, connected.Step));
        Assert.Equal(RepairVerdict.Defer, hidden.Verdict);
    }

    [Fact]
    public void ARepairThatLeftAStepForThePlayerEndsTheLadder()
    {
        var decision = RepairPolicy.Decide(Request([StartPlatform, RestartPlatform],
            history: Tried(StartPlatform, Minutes5, RepairOutcome.NeedsUser)));

        Assert.Equal(RepairVerdict.Stop, decision.Verdict);
    }

    [Fact]
    public void SixRepairsAnHourIsTheCeiling()
    {
        Assert.Equal(RepairVerdict.Defer, RepairPolicy.Decide(Request([StartPlatform], lastHour: 6)).Verdict);
        Assert.Equal(RepairVerdict.Run, RepairPolicy.Decide(Request([StartPlatform], lastHour: 5)).Verdict);
    }

    [Fact]
    public void WhatWasTriedYesterdayNoLongerCountsAgainstARung()
    {
        var decision = RepairPolicy.Decide(Request([StartPlatform, RestartPlatform], history:
            [Tried(StartPlatform, TimeSpan.FromHours(30)), Tried(StartPlatform, TimeSpan.FromHours(25))]));

        Assert.Equal(StartPlatform, decision.Step);
    }

    [Fact]
    public void WhatThePlayerRanDoesNotUseUpOptimasRungs()
    {
        var decision = RepairPolicy.Decide(Request([StartPlatform, RestartPlatform], history:
        [
            Tried(StartPlatform, TimeSpan.FromMinutes(8), trigger: RepairTrigger.User),
            Tried(StartPlatform, Minutes5, trigger: RepairTrigger.User),
        ]));

        Assert.Equal(StartPlatform, decision.Step);
    }

    [Fact]
    public void WhenEveryRungWasTriedOptimaStops()
    {
        var decision = RepairPolicy.Decide(Request([StartPlatform, RestartPlatform], history:
        [
            Tried(StartPlatform, TimeSpan.FromMinutes(20)),
            Tried(StartPlatform, TimeSpan.FromMinutes(15)),
            Tried(RestartPlatform, TimeSpan.FromMinutes(12)),
        ]));

        Assert.Equal(RepairVerdict.Stop, decision.Verdict);
    }

    [Fact]
    public void EveryIssueWithARepairPlanIsInTheGuide()
    {
        var missing = RepairCatalog.Codes.Where(code => ErrorCatalog.Find(code) is null).ToList();

        Assert.True(missing.Count == 0, "repair plans for codes without a guide entry: " + string.Join(", ", missing));
    }

    [Fact]
    public void ARepairThatMakesAChoiceForThePlayerIsNeverOnALadder()
    {
        // Restoring a three-week-old backup or keeping the current file is a decision, not a repair.
        var plan = RepairCatalog.For("VDD_RESTORE_PENDING");

        Assert.Empty(plan.Ladder);
        Assert.NotEmpty(plan.ByHandOnly);
    }
}
