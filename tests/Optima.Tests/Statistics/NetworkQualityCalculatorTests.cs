using Optima.Core.Statistics;
using Xunit;

namespace Optima.Tests.Statistics;

public sealed class NetworkQualityCalculatorTests
{
    [Fact]
    public void EmptyCalculatorReportsZeros()
    {
        var calc = new NetworkQualityCalculator();
        Assert.Equal(0, calc.AveragePingMs);
        Assert.Equal(0, calc.JitterMs);
        Assert.Equal(0, calc.PacketLossPct);
        Assert.False(calc.SessionAggregate.HasData);
    }

    [Fact]
    public void AveragesSuccessfulPings()
    {
        var calc = new NetworkQualityCalculator();
        calc.AddResult(20);
        calc.AddResult(30);
        calc.AddResult(40);
        Assert.Equal(30, calc.AveragePingMs, 3);
    }

    [Fact]
    public void JitterIsMeanAbsoluteSuccessiveDifference()
    {
        var calc = new NetworkQualityCalculator();
        calc.AddResult(20);
        calc.AddResult(30); // +10
        calc.AddResult(24); // -6
        Assert.Equal(8, calc.JitterMs, 3);
    }

    [Fact]
    public void TimeoutsCountAsLoss()
    {
        var calc = new NetworkQualityCalculator();
        calc.AddResult(20);
        calc.AddResult(null);
        calc.AddResult(20);
        calc.AddResult(null);
        Assert.Equal(50, calc.PacketLossPct, 3);
    }

    [Fact]
    public void AllTimeoutsReportFullLossAndZeroPing()
    {
        var calc = new NetworkQualityCalculator();
        calc.AddResult(null);
        calc.AddResult(null);
        Assert.Equal(100, calc.PacketLossPct, 3);
        Assert.Equal(0, calc.AveragePingMs);
        Assert.Equal(0, calc.JitterMs);
    }

    [Fact]
    public void RollingWindowDropsOldResults()
    {
        var calc = new NetworkQualityCalculator(windowSize: 3);
        calc.AddResult(null);
        calc.AddResult(10);
        calc.AddResult(10);
        calc.AddResult(10); // pushes the timeout out of the window
        Assert.Equal(0, calc.PacketLossPct, 3);
        // Whole-session aggregate still remembers the loss.
        Assert.Equal(25, calc.SessionAggregate.PacketLossPct, 3);
    }

    [Fact]
    public void SessionAggregateSpansEverything()
    {
        var calc = new NetworkQualityCalculator(windowSize: 2);
        calc.AddResult(10);
        calc.AddResult(20);
        calc.AddResult(30);
        calc.AddResult(null);

        var aggregate = calc.SessionAggregate;
        Assert.Equal(4, aggregate.SampleCount);
        Assert.Equal(20, aggregate.AveragePingMs, 3);
        Assert.Equal(10, aggregate.JitterMs, 3);
        Assert.Equal(25, aggregate.PacketLossPct, 3);
    }
}
