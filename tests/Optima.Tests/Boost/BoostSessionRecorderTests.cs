using Optima.Core.Abstractions;
using Optima.Core.Boost;
using Optima.Core.Launch;
using Optima.Core.Models;
using Xunit;

namespace Optima.Tests.Boost;

public sealed class BoostSessionRecorderTests
{
    private static readonly AppSettings AllOn = new()
    {
        BoostEnabled = true,
        BoostPriorityGuardEnabled = true,
        BoostStandbyCleanerEnabled = true,
        BoostTimerResolutionEnabled = true,
        BoostDemoteBackgroundEnabled = true,
        BoostWifiScanPaused = true,
        BoostKeepCoresAwake = true,
        BoostCorePlacement = true,
        BoostPowerFloor = true,
        BoostGpuHighPerformance = true,
        BoostGpuPriorityHigh = true,
        PlayerIgn = " Someone ",
    };

    [Fact]
    public void ASwitchThatWorkedOnceDuringTheSession_CountsAsApplied_EvenAfterItLetGo()
    {
        var recorder = new BoostSessionRecorder();
        recorder.Begin();
        recorder.Observe(new TimerResolutionStatus(true, true, 1, 1, 1, true));
        recorder.Observe(new TimerResolutionStatus(true, false, 1, null, 15.6, true));

        var record = recorder.End(AllOn)!;

        Assert.Contains(BoostFeatures.Timer, record.Applied);
        Assert.DoesNotContain(BoostFeatures.Gpu, record.Applied);
        Assert.Equal(BoostFeatures.All.Count, record.On.Count);
    }

    [Fact]
    public void WhatWasAlreadyWorkingWhenTheSessionBegan_IsCounted_AndWhatWorkedBeforeOnlyIsNot()
    {
        var recorder = new BoostSessionRecorder();
        recorder.Observe(new GameExtrasStatus(false, false, false, true, true, "game.exe"));
        recorder.Observe(BoostFeatures.Standby, true);
        recorder.Observe(BoostFeatures.Standby, false);

        recorder.Begin();
        var record = recorder.End(AllOn)!;

        Assert.Equal([BoostFeatures.Gpu], record.Applied);
    }

    [Fact]
    public void ChangesOutsideASession_AreNotRecorded()
    {
        var recorder = new BoostSessionRecorder();
        recorder.Begin();
        recorder.End(AllOn);
        recorder.Observe(BoostFeatures.Standby, true);
        recorder.Observe(BoostFeatures.Standby, false);

        recorder.Begin();
        Assert.Empty(recorder.End(AllOn)!.Applied);
    }

    [Fact]
    public void WithTheMasterSwitchOff_NothingIsOn_AndNothingCountsAsApplied()
    {
        var recorder = new BoostSessionRecorder();
        recorder.Begin();
        recorder.Observe(new BackgroundDemotionStatus(true, true, 4, 2));

        var record = recorder.End(AllOn with { BoostEnabled = false })!;

        Assert.Empty(record.On);
        Assert.Empty(record.Applied);
    }

    [Fact]
    public void WithoutSettings_TheSessionHasNoRecord()
    {
        var recorder = new BoostSessionRecorder();
        recorder.Begin();
        Assert.Null(recorder.End(null));
    }

    [Fact]
    public void EachStatusMapsToItsSwitch_OnlyWhenItReallyDidSomething()
    {
        var recorder = new BoostSessionRecorder();
        recorder.Begin();
        // The guard standing down for the session, cores already awake and a floor the plan
        // already had do nothing a session with the switch off would not have had too.
        recorder.Observe(new PriorityGuardStatus(true, ProcessPriorityLevel.High, 1, true, 0, null, null));
        recorder.Observe(new GameExtrasStatus(true, false, true, false, false, null,
            PowerFloorEnabled: true, PowerFloorAlreadySet: true));
        Assert.Empty(recorder.End(AllOn)!.Applied);

        recorder.Begin();
        recorder.Observe(new PriorityGuardStatus(true, ProcessPriorityLevel.High, 1, false, 0, null, null));
        recorder.Observe(new BackgroundDemotionStatus(true, true, 3, 1));
        recorder.Observe(new WifiScanStatus(true, true, true, [new WifiInterfaceHold("Wi-Fi", true, false)]));
        recorder.Observe(new GameExtrasStatus(true, true, false, true, true, "game.exe",
            PlacementEnabled: true, PlacementApplied: true, GpuPriorityEnabled: true, GpuPriorityRaised: 1,
            PowerFloorEnabled: true, PowerFloorHeld: true));
        var record = recorder.End(AllOn)!;

        Assert.Equal(
            [BoostFeatures.Priority, BoostFeatures.Demote, BoostFeatures.Wifi, BoostFeatures.Cores,
             BoostFeatures.Placement, BoostFeatures.PowerFloor, BoostFeatures.Gpu, BoostFeatures.GpuPriority],
            record.Applied);
    }

    [Fact]
    public void ASwitchThatIsOffDoesNotCountAsApplied_WhateverItsServiceSays()
    {
        var recorder = new BoostSessionRecorder();
        recorder.Begin();
        recorder.Observe(BoostFeatures.Gpu, true);

        var record = recorder.End(AllOn with { BoostGpuHighPerformance = false })!;

        Assert.DoesNotContain(BoostFeatures.Gpu, record.On);
        Assert.DoesNotContain(BoostFeatures.Gpu, record.Applied);
    }

    [Fact]
    public void ThePlayerIsTheAccountIdWhenKnown_AndTheTrimmedLowerCaseNameOtherwise()
    {
        Assert.Equal("someone", BoostSessionRecorder.PlayerKey(AllOn));
        Assert.Equal("123", BoostSessionRecorder.PlayerKey(AllOn with { PlayerAccountId = 123 }));
        Assert.Equal("someone", BoostSessionRecorder.PlayerKey(AllOn with { PlayerAccountId = 0 }));
    }

    [Fact]
    public void ASwitchTurnedOffBeforeTheSessionEnded_CountsAsNeitherOnNorOff()
    {
        var recorder = new BoostSessionRecorder();
        recorder.Begin(AllOn);
        recorder.Observe(new TimerResolutionStatus(true, true, 1, 1, 1, true));

        var record = recorder.End(AllOn with { BoostEnabled = false })!;

        Assert.Contains(BoostFeatures.Timer, record.On);
        Assert.Equal(BoostFeatures.All.Count, record.On.Count);
        Assert.Empty(record.Applied);
    }

    [Fact]
    public void ASwitchTurnedOnDuringTheSession_CountsAsNeitherOnNorOff_EvenIfItWorked()
    {
        var recorder = new BoostSessionRecorder();
        recorder.Begin(AllOn with { BoostTimerResolutionEnabled = false });
        recorder.Observe(new TimerResolutionStatus(true, true, 1, 1, 1, true));
        recorder.Observe(new BackgroundDemotionStatus(true, true, 4, 2));

        var record = recorder.End(AllOn)!;

        Assert.Contains(BoostFeatures.Timer, record.On);
        Assert.Equal([BoostFeatures.Demote], record.Applied);
    }

    [Fact]
    public void AHighPerformancePreferenceThePlayerSetThemselves_DoesNotCountAsApplied()
    {
        var recorder = new BoostSessionRecorder();
        recorder.Begin();
        recorder.Observe(new GameExtrasStatus(false, false, false, true, true, "game.exe", GpuAlreadySet: true));

        Assert.DoesNotContain(BoostFeatures.Gpu, recorder.End(AllOn)!.Applied);
    }

    [Fact]
    public void TheMemoryCleanerCountsOnlyWhenItPurgedWhileRunning_NotForWatchingAlone()
    {
        var recorder = new BoostSessionRecorder();
        recorder.ObserveCleaner(true, 4);   // purges from an earlier match
        recorder.Begin();
        recorder.ObserveCleaner(true, 4);
        Assert.Empty(recorder.End(AllOn)!.Applied);

        recorder.Begin();
        recorder.ObserveCleaner(true, 5);
        recorder.ObserveCleaner(true, 5);
        Assert.Equal([BoostFeatures.Standby], recorder.End(AllOn)!.Applied);

        recorder.Begin();
        recorder.ObserveCleaner(false, 6);
        Assert.Empty(recorder.End(AllOn)!.Applied);
    }

    [Fact]
    public void ThePlayersKeys_AreTheIdAndTheName_OrJustOneWhenTheyAreTheSame()
    {
        Assert.Equal(["123", "someone"], BoostSessionRecorder.PlayerKeys(AllOn with { PlayerAccountId = 123 }));
        Assert.Equal(["someone"], BoostSessionRecorder.PlayerKeys(AllOn));
        Assert.Equal(["123"], BoostSessionRecorder.PlayerKeys(AllOn with { PlayerAccountId = 123, PlayerIgn = " " }));
    }

    [Fact]
    public void TwoRecordsWithTheSameContent_AreEqual()
    {
        var a = new BoostSessionRecord { Player = "p", On = ["gpu"], Applied = ["gpu"] };
        var b = new BoostSessionRecord { Player = "p", On = new List<string> { "gpu" }, Applied = new List<string> { "gpu" } };

        Assert.Equal(a, b);
        Assert.Equal(a.GetHashCode(), b.GetHashCode());
        Assert.NotEqual(a, b with { Applied = [] });
    }
}
