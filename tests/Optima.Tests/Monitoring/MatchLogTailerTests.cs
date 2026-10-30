using Optima.Core.Monitoring;
using Xunit;

namespace Optima.Tests.Monitoring;

/// <summary>The match line on the Discord card comes from GPG's logcat file; these pin the parse and the tailing.</summary>
public sealed class MatchLogTailerTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 10, 27, 20, 0, 0, TimeSpan.Zero);

    private const string MatchStart =
        "10-27 20:00:00.000  1234  1260 I Unity   : [LoadTiming] MENU -> GAMEPLAY (Raid): total 8.71 s\n";

    private const string MatchEnd =
        "10-27 20:09:00.000  1234  1260 I Unity   : [LoadTiming] GAMEPLAY -> MENU (Menu): total 2.10 s\n";

    private readonly string _dir = Directory.CreateTempSubdirectory("optima-tailer-").FullName;

    private string Log => Path.Combine(_dir, "AndroidSerial.log");

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Theory]
    [InlineData("I Unity   : [LoadTiming] MENU -> GAMEPLAY (Raid): total 8.71 s", MatchPhase.InMatch, "Raid")]
    [InlineData("I Unity   : [LoadTiming] MENU -> GAMEPLAY (Village): total 9 s", MatchPhase.InMatch, "Village")]
    [InlineData("I Unity   : [LoadTiming] GAMEPLAY -> MENU (Menu): total 2 s", MatchPhase.InMenu, "")]
    [InlineData("I Unity   : [LoadTiming] LANDINGPAGE -> MENU (Menu): total 1 s", MatchPhase.InMenu, "")]
    [InlineData("I Unity   : [LoadTiming] START -> LANDINGPAGE (Menu): total 4 s", MatchPhase.Unknown, "")]
    [InlineData("I Unity   : HSMWaitForMatchResult: OnStateEnter", MatchPhase.Unknown, "")]
    public void LoadLinesAreParsed(string line, MatchPhase phase, string map)
    {
        var tailer = new MatchLogTailer(Log);

        tailer.Apply(line, Now);

        Assert.Equal(phase, tailer.State.Phase);
        Assert.Equal(map, tailer.State.Map);
        Assert.Equal(MatchMode.Unknown, tailer.State.Mode);
    }

    [Fact]
    public void TheFirstReadSkipsHistoryThenFollowsNewLines()
    {
        File.WriteAllText(Log, "boot noise\n" + MatchStart + MatchEnd + MatchStart);
        var tailer = new MatchLogTailer(Log);

        Assert.False(tailer.Poll(Now));
        Assert.Equal(MatchPhase.Unknown, tailer.State.Phase);

        File.AppendAllText(Log, MatchStart.Replace("Raid", "Heat", StringComparison.Ordinal));
        Assert.True(tailer.Poll(Now));
        Assert.Equal(new MatchState(MatchPhase.InMatch, "Heat", MatchMode.Casual, Now), tailer.State);

        File.AppendAllText(Log, MatchEnd);
        Assert.True(tailer.Poll(Now.AddMinutes(9)));
        Assert.Equal(MatchPhase.InMenu, tailer.State.Phase);

        Assert.False(tailer.Poll(Now.AddMinutes(10)));
    }

    [Fact]
    public void AHalfWrittenLineWaitsForItsEnd()
    {
        File.WriteAllText(Log, "start\n");
        var tailer = new MatchLogTailer(Log);
        tailer.Poll(Now);

        File.AppendAllText(Log, MatchStart[..40]);
        Assert.False(tailer.Poll(Now));

        File.AppendAllText(Log, MatchStart[40..]);
        Assert.True(tailer.Poll(Now));
        Assert.Equal("Raid", tailer.State.Map);
    }

    [Fact]
    public void ARotatedFileIsReadFromItsStart()
    {
        File.WriteAllText(Log, new string('x', 4000) + "\n");
        var tailer = new MatchLogTailer(Log);
        tailer.Poll(Now);

        // GPG renames the full file away and starts a fresh, shorter one.
        File.Move(Log, Path.Combine(_dir, "AndroidSerial-bkup-20261027-0001.log"));
        File.WriteAllText(Log, MatchStart);

        Assert.True(tailer.Poll(Now));
        Assert.Equal("Raid", tailer.State.Map);
    }

    [Fact]
    public void ARotatedFileThatOutgrewTheOldOffsetIsStillNoticed()
    {
        File.WriteAllText(Log, "old head line\n");
        var tailer = new MatchLogTailer(Log);
        tailer.Poll(Now);

        File.Delete(Log);
        File.WriteAllText(Log, "new head line, longer than before\n" + MatchStart);

        Assert.True(tailer.Poll(Now));
        Assert.Equal(MatchPhase.InMatch, tailer.State.Phase);
    }

    [Fact]
    public void ARotatedFileWithTheSameBootBannerIsStillNoticed()
    {
        // Every real AndroidSerial.log starts with the same U-Boot banner, so the head cannot tell files apart.
        const string Banner = "\n\n\n\nU-Boot 2023.04-23704-g981ae56d1b-ab10287685 (Jun 08 2023 - 00:00:00)\n";
        File.WriteAllText(Log, Banner + "short old session\n");
        var tailer = new MatchLogTailer(Log);
        tailer.Poll(Now);

        File.Delete(Log);
        File.WriteAllText(Log, Banner + "a new boot, already longer than the old file was\n" + MatchStart);

        Assert.True(tailer.Poll(Now));
        Assert.Equal("Raid", tailer.State.Map);
    }

    [Fact]
    public void APollReadsAtMostItsCapAndOnlyWholeLines()
    {
        File.WriteAllText(Log, "start\n");
        var tailer = new MatchLogTailer(Log, maxBytesPerPoll: 200);
        tailer.Poll(Now);

        // An old match far behind the cap is skipped; the newest line inside it is read.
        File.AppendAllText(Log, MatchStart + new string('y', 1000) + "\n" + MatchEnd);

        Assert.True(tailer.Poll(Now));
        Assert.Equal(MatchPhase.InMenu, tailer.State.Phase);
    }

    [Fact]
    public void ResetForgetsTheStateAndStartsFromTheEndAgain()
    {
        File.WriteAllText(Log, "start\n");
        var tailer = new MatchLogTailer(Log);
        tailer.Poll(Now);
        File.AppendAllText(Log, MatchStart);
        tailer.Poll(Now);

        tailer.Reset();
        File.AppendAllText(Log, MatchStart);

        Assert.Equal(MatchState.None, tailer.State);
        Assert.False(tailer.Poll(Now));
        Assert.Equal(MatchPhase.Unknown, tailer.State.Phase);
    }

    [Fact]
    public void AMissingFileIsQuiet()
    {
        var tailer = new MatchLogTailer(Log);

        Assert.False(tailer.Poll(Now));
        Assert.False(tailer.Poll(Now));
    }

    // Shapes and timings from the owner's logs: the game's analytics event lands as a ranked match
    // starts loading; the load line follows "total" seconds later.
    private const string Submit = "10-26 22:17:06.780  2289  2400 I Singular: ApiManager [api] - api = com.singular.sdk.internal.ApiSubmitEvent";
    private const string RankedLoad = "10-26 22:17:21.017  2289  2350 I Unity   : [LoadTiming] MENU -> GAMEPLAY (Village): total 14.05 s, 845 frames";
    private const string Menu = "10-26 22:06:35.100  2289  2350 I Unity   : [LoadTiming] GAMEPLAY -> MENU (Menu): total 1.91 s, 127 frames";

    [Fact]
    public void AnAnalyticsEventAsTheLoadBeginsMeansRanked()
    {
        var tailer = new MatchLogTailer(Log);
        tailer.Apply(Menu, Now);
        tailer.Apply(Submit, Now);
        tailer.Apply(RankedLoad, Now);

        Assert.Equal(MatchMode.Ranked, tailer.State.Mode);
        Assert.Equal("Village", tailer.State.Map);
    }

    [Fact]
    public void NoEventNearTheLoadIsCasual_AndOtherProcessesDoNotCount()
    {
        var tailer = new MatchLogTailer(Log);
        tailer.Apply(Menu, Now);
        // Same time, but another app's process.
        tailer.Apply(Submit.Replace("  2289  2400", "  1515  2400", StringComparison.Ordinal), Now);
        tailer.Apply(RankedLoad, Now);

        Assert.Equal(MatchMode.Casual, tailer.State.Mode);
    }

    [Fact]
    public void TheEventAFinishedMatchSendsBeforeTheMenuIsNotRanked()
    {
        // The previous match's end event falls inside the 15 s window of the next load, but before
        // the return to the menu, so it belongs to the old match.
        var tailer = new MatchLogTailer(Log);
        tailer.Apply(Menu, Now);
        tailer.Apply("10-26 22:06:25.000  2289  2400 I Singular: ApiManager [api] - api = com.singular.sdk.internal.ApiSubmitEvent", Now);
        tailer.Apply("10-26 22:06:35.200  2289  2350 I Unity   : [LoadTiming] GAMEPLAY -> MENU (Menu): total 1.91 s", Now);
        tailer.Apply("10-26 22:06:54.000  2289  2350 I Unity   : [LoadTiming] MENU -> GAMEPLAY (Heat): total 13.33 s", Now);

        Assert.Equal(MatchMode.Casual, tailer.State.Mode);
    }

    [Fact]
    public void AWatchStartedMidSessionLearnsTheGameFromAnyOfItsLines()
    {
        var tailer = new MatchLogTailer(Log);
        tailer.Apply("10-26 22:16:50.000  2289  2350 W Unity   : InternalEmblemDefinition.Load: Already loading emblem_022_15, ignoring", Now);
        tailer.Apply(Submit, Now);
        tailer.Apply(RankedLoad, Now);

        Assert.Equal(MatchMode.Ranked, tailer.State.Mode);
    }
}
