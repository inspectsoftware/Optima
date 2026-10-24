using Optima.Core.Launch;
using Xunit;

namespace Optima.Tests.Launch;

public sealed class LaunchStepListTests
{
    private const int Launcher = LaunchStepList.Launcher;
    private const int Profile = LaunchStepList.Profile;
    private const int Game = LaunchStepList.Game;
    private const int Display = LaunchStepList.Display;
    private const int Restore = LaunchStepList.Restore;

    /// <summary>Feeds the reports in, checking after each one that no more than one row is running.</summary>
    private static LaunchStepList Run(LaunchStepList steps, params LaunchProgress[] reports)
    {
        foreach (var report in reports)
        {
            steps.Apply(report);
            Assert.True(steps.States.Count(state => state == StepState.Live) <= 1,
                $"two rows running after {report.Phase}: {string.Join(", ", steps.States)}");
        }
        return steps;
    }

    private static LaunchStepList Run(params LaunchProgress[] reports) => Run(new LaunchStepList(), reports);

    private static LaunchProgress P(LaunchPhase phase, string message = "", bool skipped = false) => new(phase, message, skipped);

    private static readonly LaunchProgress[] UpToTheGame =
    [
        P(LaunchPhase.Validating, "Checking Google Play Games and Critical Ops…"),
        P(LaunchPhase.ApplyingPerformanceProfile, "Applying performance profile…"),
        P(LaunchPhase.StartingPlatform, "Launching Critical Ops through Google Play Games…"),
        P(LaunchPhase.WaitingForGame, "Waiting for the game to start…"),
    ];

    [Fact]
    public void BeforeTheGameIsUp_TheDisplayIsStillToCome()
    {
        var steps = Run(UpToTheGame);

        Assert.Equal([StepState.Done, StepState.Done, StepState.Live, StepState.Todo, StepState.Todo], steps.States);
        Assert.Equal("Waiting for the game to start…", steps.Details[Game]);
        Assert.Null(steps.Details[Display]);
    }

    [Fact]
    public void WithTheDisplay_ItComesOnAfterTheGameAndTheGameIsWhatRunsAfterwards()
    {
        var steps = Run(UpToTheGame);

        Run(steps, P(LaunchPhase.ConfiguringDisplay, "Configuring virtual display 1920x1080 @ 240 Hz…"));
        Assert.Equal([StepState.Done, StepState.Done, StepState.Done, StepState.Live, StepState.Todo], steps.States);
        Assert.Equal("running", steps.Details[Game]);
        Assert.Equal("Configuring virtual display 1920x1080 @ 240 Hz…", steps.Details[Display]);

        Run(steps, P(LaunchPhase.Monitoring, "Critical Ops is running."));
        Assert.Equal([StepState.Done, StepState.Done, StepState.Live, StepState.Done, StepState.Todo], steps.States);
        Assert.Equal("running", steps.Details[Game]);
        Assert.Equal("on while the game runs", steps.Details[Display]);

        Run(steps, P(LaunchPhase.Restoring, "Restoring system settings…"), P(LaunchPhase.Completed, "Session complete."));
        Assert.All(steps.States, state => Assert.Equal(StepState.Done, state));
        Assert.Equal("everything restored", steps.Details[Restore]);
    }

    [Fact]
    public void WithoutTheDisplay_ItIsShownAsNotUsedRatherThanDone()
    {
        var steps = Run([.. UpToTheGame, P(LaunchPhase.Monitoring, "Critical Ops is running.")]);

        Assert.Equal([StepState.Done, StepState.Done, StepState.Live, StepState.Skipped, StepState.Todo], steps.States);
        Assert.Equal("off on the Display page", steps.Details[Display]);

        Run(steps, P(LaunchPhase.Restoring), P(LaunchPhase.Completed));
        Assert.Equal([StepState.Done, StepState.Done, StepState.Done, StepState.Skipped, StepState.Done], steps.States);
    }

    [Fact]
    public void ADisplayThatWasGivenUp_StaysSkippedWhileTheGamePlaysOn()
    {
        var steps = Run(
        [
            .. UpToTheGame,
            P(LaunchPhase.ConfiguringDisplay, "Configuring virtual display 1920x1080 @ 240 Hz…"),
            P(LaunchPhase.ConfiguringDisplay, "The virtual display was not switched on.", skipped: true),
        ]);

        // Nothing is running for the moment the display was given up; the game row takes over next.
        Assert.Equal(StepState.Skipped, steps.States[Display]);
        Assert.Equal("not switched on", steps.Details[Display]);

        Run(steps, P(LaunchPhase.Monitoring, "Critical Ops is running."));
        Assert.Equal([StepState.Done, StepState.Done, StepState.Live, StepState.Skipped, StepState.Todo], steps.States);
        Assert.Equal("not switched on", steps.Details[Display]);

        Run(steps, P(LaunchPhase.Restoring), P(LaunchPhase.Completed));
        Assert.Equal(StepState.Skipped, steps.States[Display]);
    }

    [Theory]
    [InlineData(LaunchPhase.StartingPlatform, Game)]
    [InlineData(LaunchPhase.WaitingForGame, Game)]
    [InlineData(LaunchPhase.Monitoring, Game)]
    [InlineData(LaunchPhase.ApplyingPerformanceProfile, Profile)]
    public void AFailure_MarksTheRowThatWasRunning(LaunchPhase lastPhase, int failed)
    {
        var steps = Run(P(LaunchPhase.Validating), P(LaunchPhase.ApplyingPerformanceProfile), P(lastPhase));

        Run(steps, P(LaunchPhase.Failed, "The game did not start within three minutes."));
        // The result reports the same failure a second time.
        steps.Fail();

        Assert.Equal(StepState.Failed, steps.States[failed]);
        Assert.Single(steps.States, state => state == StepState.Failed);
        Assert.DoesNotContain(StepState.Live, steps.States);
    }

    [Fact]
    public void ACancelBeforeTheGameIsUp_DoesNotTickOffTheDisplay()
    {
        // The cancel path reports Completed straight after the phase it stopped in.
        var steps = Run([.. UpToTheGame, P(LaunchPhase.Completed, "Cancelled. Settings restored.")]);

        Assert.Equal([StepState.Done, StepState.Done, StepState.Done, StepState.Todo, StepState.Done], steps.States);
        Assert.Null(steps.Details[Display]);
        Assert.Equal("everything restored", steps.Details[Restore]);
    }

    [Fact]
    public void AFailureBeforeAnyReport_BlamesTheFirstRow()
    {
        var steps = new LaunchStepList();

        steps.Fail();

        Assert.Equal(StepState.Failed, steps.States[Launcher]);
    }

    [Fact]
    public void AWatchSession_StartsFromAClearListAndHasTheGameRunningFromTheStart()
    {
        // The last session ended and left every row done.
        var steps = Run([.. UpToTheGame, P(LaunchPhase.Monitoring), P(LaunchPhase.Completed)]);

        // Watch mode attaches to a game that is already up: no launch and no wait for it.
        Run(steps, P(LaunchPhase.Validating, "Game detected. Applying the selected profile…"));
        Assert.Equal([StepState.Live, StepState.Todo, StepState.Todo, StepState.Todo, StepState.Todo], steps.States);
        Assert.Null(steps.Details[Restore]);

        Run(steps,
            P(LaunchPhase.ApplyingPerformanceProfile),
            P(LaunchPhase.ConfiguringDisplay, "Configuring virtual display 1920x1080 @ 240 Hz…"),
            P(LaunchPhase.Monitoring));
        Assert.Equal([StepState.Done, StepState.Done, StepState.Live, StepState.Done, StepState.Todo], steps.States);
    }
}
