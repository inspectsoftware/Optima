using System.ComponentModel;
using Optima.Core.Health;
using Xunit;

namespace Optima.Tests.Health;

public sealed class CrashMarkerTests : IDisposable
{
    private static readonly DateTimeOffset Monday = new(2026, 10, 5, 22, 9, 8, TimeSpan.FromHours(-4));
    private static readonly DateTimeOffset Tuesday = Monday.AddDays(1);

    private readonly string _directory = Path.Combine(Path.GetTempPath(), "optima-marker-" + Guid.NewGuid().ToString("N"));

    /// <summary>Every run is a new process, so every run gets a new marker over the same folder.</summary>
    private CrashMarker NewRun() => new(_directory);

    [Fact]
    public void AFirstRunFindsNothingBehindIt()
    {
        var previous = NewRun().Begin("0.7.5", Monday);

        Assert.False(previous.EndedUncleanly);
        Assert.Null(previous.Fatal);
    }

    [Fact]
    public void ARunThatEndedNormallyLeavesNothingForTheNext()
    {
        var first = NewRun();
        first.Begin("0.7.5", Monday);
        first.End();

        var previous = NewRun().Begin("0.7.5", Tuesday);

        Assert.False(previous.EndedUncleanly);
        Assert.Null(previous.Fatal);
    }

    [Fact]
    public void ARunThatNeverReachedItsExitIsFoundByTheNext()
    {
        NewRun().Begin("0.7.4", Monday);

        var previous = NewRun().Begin("0.7.5", Tuesday);

        Assert.True(previous.EndedUncleanly);
        Assert.Equal(Monday, previous.StartedAt);
        Assert.Equal("0.7.4", previous.Version);
        Assert.Null(previous.Fatal);
    }

    [Fact]
    public void AFatalErrorIsThereForTheNextRunToRead()
    {
        var crashed = NewRun();
        crashed.Begin("0.7.5", Monday);
        crashed.RecordFatal("the UI thread", new Win32Exception(5, "PowerSetActiveScheme failed"), Monday.AddMinutes(3));
        // The UI-thread handler still walks the normal exit path afterwards.
        crashed.End();

        var previous = NewRun().Begin("0.7.5", Tuesday);

        Assert.False(previous.EndedUncleanly);
        var fatal = Assert.IsType<FatalRecord>(previous.Fatal);
        Assert.Equal("the UI thread", fatal.Origin);
        Assert.Equal(Monday.AddMinutes(3), fatal.At);
        Assert.Contains("PowerSetActiveScheme failed (Win32 5", fatal.Summary);
        Assert.Contains("Win32Exception", fatal.FullText);
    }

    [Fact]
    public void AHardCrashLeavesBothTheErrorAndTheUnfinishedRun()
    {
        var crashed = NewRun();
        crashed.Begin("0.7.5", Monday);
        crashed.RecordFatal("a background thread", new InvalidOperationException("boom"), Monday.AddMinutes(1));

        var previous = NewRun().Begin("0.7.5", Tuesday);

        Assert.True(previous.EndedUncleanly);
        Assert.Equal("InvalidOperationException: boom", previous.Fatal?.Summary);
    }

    [Fact]
    public void OnlyTheFirstFatalErrorOfARunIsKept()
    {
        var crashed = NewRun();
        crashed.Begin("0.7.5", Monday);
        crashed.RecordFatal("the UI thread", new InvalidOperationException("the cause"), Monday);
        crashed.RecordFatal("a background thread", new InvalidOperationException("the fallout"), Monday);

        Assert.Equal("InvalidOperationException: the cause", NewRun().Begin("0.7.5", Tuesday).Fatal?.Summary);
    }

    [Fact]
    public void AFatalErrorIsReportedOnce()
    {
        var crashed = NewRun();
        crashed.Begin("0.7.5", Monday);
        crashed.RecordFatal("the UI thread", new InvalidOperationException("boom"), Monday);
        crashed.End();

        var second = NewRun();
        Assert.NotNull(second.Begin("0.7.5", Tuesday).Fatal);
        second.End();

        Assert.Null(NewRun().Begin("0.7.5", Tuesday.AddDays(1)).Fatal);
    }

    [Fact]
    public void ADamagedMarkerIsNotAReasonToFailAtStartup()
    {
        Directory.CreateDirectory(_directory);
        File.WriteAllText(Path.Combine(_directory, "run.json"), "{ not json");
        File.WriteAllText(Path.Combine(_directory, "fatal.json"), "also not json");

        var previous = NewRun().Begin("0.7.5", Monday);

        Assert.False(previous.EndedUncleanly);
        Assert.Null(previous.Fatal);
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
