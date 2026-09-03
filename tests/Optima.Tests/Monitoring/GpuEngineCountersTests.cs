using Optima.Monitoring;
using Xunit;

namespace Optima.Tests.Monitoring;

public sealed class GpuEngineCountersTests
{
    private static readonly Dictionary<string, (long Raw, long Timestamp)> Empty = new();

    [Fact]
    public void FirstSnapshotReportsZeroAndRemembersEveryInstance()
    {
        var (utilization, next) = GpuEngineCounters.Compute(Empty,
        [
            ("pid_100_engtype_3D", 5_000_000, 10_000_000),
            ("pid_200_engtype_3D", 1_000_000, 10_000_000),
        ]);

        Assert.Equal(0, utilization);
        Assert.Equal(2, next.Count);
        Assert.Equal((5_000_000, 10_000_000), next["pid_100_engtype_3D"]);
    }

    [Fact]
    public void SecondSnapshotSumsBusyTimeOverWallTimeAcrossInstances()
    {
        var previous = new Dictionary<string, (long Raw, long Timestamp)>
        {
            ["pid_100_engtype_3D"] = (0, 0),
            ["pid_200_engtype_3D"] = (0, 0),
        };

        // One second of wall time in 100 ns units; engine 100 was busy 30 %, engine 200 for 25 %.
        var (utilization, _) = GpuEngineCounters.Compute(previous,
        [
            ("pid_100_engtype_3D", 3_000_000, 10_000_000),
            ("pid_200_engtype_3D", 2_500_000, 10_000_000),
        ]);

        Assert.Equal(55, utilization, precision: 6);
    }

    [Fact]
    public void NewInstancesAndStalledTimestampsContributeNothing()
    {
        var previous = new Dictionary<string, (long Raw, long Timestamp)>
        {
            ["pid_100_engtype_3D"] = (1_000_000, 10_000_000),
        };

        var (utilization, next) = GpuEngineCounters.Compute(previous,
        [
            ("pid_100_engtype_3D", 2_000_000, 10_000_000), // no wall time elapsed
            ("pid_300_engtype_3D", 9_000_000, 10_000_000), // first sighting
        ]);

        Assert.Equal(0, utilization);
        Assert.Equal(2, next.Count);
    }

    [Fact]
    public void SumIsClampedToOneHundredPercent()
    {
        var previous = new Dictionary<string, (long Raw, long Timestamp)>
        {
            ["a_engtype_3D"] = (0, 0),
            ["b_engtype_3D"] = (0, 0),
        };

        var (utilization, _) = GpuEngineCounters.Compute(previous,
        [
            ("a_engtype_3D", 10_000_000, 10_000_000),
            ("b_engtype_3D", 10_000_000, 10_000_000),
        ]);

        Assert.Equal(100, utilization);
    }
}
