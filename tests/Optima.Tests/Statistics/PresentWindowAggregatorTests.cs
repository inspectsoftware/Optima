using Optima.Core.Statistics;
using Xunit;

namespace Optima.Tests.Statistics;

public sealed class PresentWindowAggregatorTests
{
    private static void Present(PresentWindowAggregator aggregator, int pid, double startMs, double deltaMs, int frames)
    {
        for (var i = 0; i <= frames; i++)
        {
            aggregator.RecordPresent(pid, startMs + i * deltaMs);
        }
    }

    [Fact]
    public void EmptyWindowPublishesNothing()
    {
        var aggregator = new PresentWindowAggregator([100]);
        Assert.Null(aggregator.CompleteWindow());
    }

    [Fact]
    public void SingleTimestampIsNotAFrame()
    {
        // A first present only seeds the delta baseline; no frametime exists yet.
        var aggregator = new PresentWindowAggregator([100]);
        aggregator.RecordPresent(100, 5.0);
        Assert.Null(aggregator.CompleteWindow());
    }

    [Fact]
    public void NonCandidatePidsAreIgnored()
    {
        var aggregator = new PresentWindowAggregator([100]);
        Present(aggregator, 999, 0, 10, 50);
        Assert.Null(aggregator.CompleteWindow());
    }

    [Fact]
    public void DominantPidWinsTheWindow()
    {
        var aggregator = new PresentWindowAggregator([100, 200]);
        Present(aggregator, 100, 0, 10, 30);   // 30 frames
        Present(aggregator, 200, 0, 5, 120);   // 120 frames, the real presenter

        var sample = aggregator.CompleteWindow();
        Assert.NotNull(sample);
        Assert.Equal(200, sample.ProcessId);
        Assert.Equal(120, sample.Fps, 3);
        Assert.Equal(5, sample.AverageFrametimeMs, 3);
    }

    [Fact]
    public void PerPidDeltasStayIsolated()
    {
        // Interleaved presents from two pids must not contaminate each other's frametimes.
        var aggregator = new PresentWindowAggregator([1, 2]);
        aggregator.RecordPresent(1, 0);
        aggregator.RecordPresent(2, 3);
        aggregator.RecordPresent(1, 10);
        aggregator.RecordPresent(2, 23);
        aggregator.RecordPresent(1, 20);
        aggregator.RecordPresent(2, 43);

        var sample = aggregator.CompleteWindow();
        Assert.NotNull(sample);
        // pid 1: two 10 ms deltas; pid 2: two 20 ms deltas. Same count, first max wins on count,
        // but the frametime of whichever wins must be its own pure delta.
        Assert.Equal(sample.ProcessId == 1 ? 10 : 20, sample.AverageFrametimeMs, 3);
    }

    [Fact]
    public void IntervalScalesFps()
    {
        var aggregator = new PresentWindowAggregator([100], intervalMs: 500);
        Present(aggregator, 100, 0, 5, 60); // 60 frames in a 500 ms window = 120 fps
        var sample = aggregator.CompleteWindow();
        Assert.NotNull(sample);
        Assert.Equal(120, sample.Fps, 3);
    }

    [Fact]
    public void OutOfRangeDeltasAreDiscarded()
    {
        var aggregator = new PresentWindowAggregator([100]);
        aggregator.RecordPresent(100, 0);
        aggregator.RecordPresent(100, 0.01);   // duplicate timestamp, below 0.05 ms
        aggregator.RecordPresent(100, 2500);   // paused for > 2 s
        Assert.Null(aggregator.CompleteWindow());
    }

    [Fact]
    public void WindowResetsAfterCompletion()
    {
        var aggregator = new PresentWindowAggregator([100]);
        Present(aggregator, 100, 0, 10, 10);
        Assert.NotNull(aggregator.CompleteWindow());
        Assert.Null(aggregator.CompleteWindow());
    }

    [Fact]
    public void CompleteReportsOverallDominantPid()
    {
        var aggregator = new PresentWindowAggregator([1, 2]);
        Present(aggregator, 1, 0, 10, 5);
        Present(aggregator, 2, 0, 5, 100);
        aggregator.CompleteWindow();

        var result = aggregator.Complete();
        Assert.Equal(2, result.DominantProcessId);
        Assert.Equal(100, result.FrametimesMs.Count);
        Assert.All(result.FrametimesMs, d => Assert.Equal(5, d, 3));
        Assert.Single(result.FpsSamples);
    }

    [Fact]
    public void CompleteWithNoPresentsReportsNoDominantPid()
    {
        var aggregator = new PresentWindowAggregator([1, 2]);
        var result = aggregator.Complete();
        Assert.Equal(0, result.DominantProcessId);
        Assert.Empty(result.FrametimesMs);
        Assert.Empty(result.FpsSamples);
    }
}
