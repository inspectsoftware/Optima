using System.Text.Json;
using Optima.Core.Health;
using Optima.Core.Ipc;
using Optima.Core.Models;
using Xunit;

namespace Optima.Tests.Health;

public sealed class DriverLatencyTests
{
    private const ulong Ndis = 0xFFFF_F800_1000_0000;
    private const ulong Audio = 0xFFFF_F800_2000_0000;

    private static DriverLatencyTally TwoDrivers()
    {
        var tally = new DriverLatencyTally();
        tally.AddModule(Ndis, 0x10000, @"\SystemRoot\System32\drivers\ndis.sys");
        tally.AddModule(Audio, 0x10000, @"C:\Windows\System32\drivers\RTKVHD64.sys");
        return tally;
    }

    [Fact]
    public void RoutinesAreNamedByTheDriverWhoseImageHoldsThem_AndAddedUpPerDriver()
    {
        var tally = TwoDrivers();
        tally.AddDpc(Ndis + 0x100, 40);
        tally.AddDpc(Ndis + 0x200, 120);
        tally.AddIsr(Ndis + 0x100, 15);
        tally.AddDpc(Audio + 0x50, 300);

        var ranked = tally.Rank();

        Assert.Equal(["RTKVHD64.sys", "ndis.sys"], ranked.Select(d => d.Driver));
        Assert.Equal(new DriverLatency("ndis.sys", 120, 15, 175, 3), ranked[1]);
    }

    [Fact]
    public void AnEventBeforeItsDriversImageIsStillNamed_BecauseAddressesAreMatchedAtTheEnd()
    {
        var tally = new DriverLatencyTally();
        tally.AddDpc(Ndis + 4, 10);
        tally.AddModule(Ndis, 0x100, "ndis.sys");

        Assert.Equal("ndis.sys", Assert.Single(tally.Rank()).Driver);
    }

    [Fact]
    public void AnAddressOutsideEveryImageIsUnknown_AndUserModeImagesAreIgnored()
    {
        var tally = TwoDrivers();
        tally.AddModule(0x7FF6_0000_0000, 0x1000_0000, @"C:\Games\game.exe");
        tally.AddDpc(Ndis + 0x10000, 50);
        tally.AddDpc(0x7FF6_0000_0010, 60);
        tally.AddDpc(0xFFFF_F800_0000_0000, 70);

        var ranked = tally.Rank();

        Assert.Equal(new DriverLatency("unknown", 70, 0, 180, 3), Assert.Single(ranked));
    }

    [Fact]
    public void RankingPutsTheLongestSingleCallFirst_TiesBrokenByTotalTime_AndKeepsTheTopOnes()
    {
        var tally = new DriverLatencyTally();
        for (var i = 0; i < 12; i++)
        {
            var image = 0xFFFF_F800_0000_0000 + (ulong)i * 0x10000;
            tally.AddModule(image, 0x10000, $"d{i}.sys");
            tally.AddIsr(image, i == 3 ? 900 : 100);
            tally.AddDpc(image + 1, i);
        }

        var ranked = tally.Rank(top: 5);

        Assert.Equal(5, ranked.Count);
        Assert.Equal("d3.sys", ranked[0].Driver);
        Assert.Equal(["d11.sys", "d10.sys", "d9.sys", "d8.sys"], ranked.Skip(1).Select(d => d.Driver));
    }

    [Fact]
    public void NegativeOrMissingDurationsAreDropped()
    {
        var tally = TwoDrivers();
        tally.AddDpc(Ndis, -5);
        tally.AddIsr(Ndis, double.NaN);

        Assert.Empty(tally.Rank());
    }

    [Fact]
    public void NoEventsIsSkipped_AndRaisesNothing()
    {
        var result = DriverLatencyScan.ToResult([], 10);

        Assert.Equal((DiagnosticStatus.Skipped, null), (result.Status, result.IssueCode));
        Assert.Equal([result], DriverLatencyScan.Reports(result));
    }

    [Fact]
    public void EverythingUnder500UsPasses_NamesTheLongestAndTheBusiest_AndClosesBothIssues()
    {
        var result = DriverLatencyScan.ToResult(
            [new DriverLatency("ndis.sys", 480, 20, 9000, 100), new DriverLatency("dxgkrnl.sys", 100, 0, 20000, 900)], 10);

        Assert.Equal(DiagnosticStatus.Pass, result.Status);
        Assert.Contains("ndis.sys, 480 us", result.Reason);
        Assert.Contains("Most time in all: dxgkrnl.sys, 20000 us", result.Reason);
        Assert.Contains("ndis.sys: longest DPC 480 us, longest ISR 20 us, 9000 us in all over 100 calls", result.Details);
        Assert.Equal(
            [(DiagnosticStatus.Pass, DriverLatencyScan.HighCode), (DiagnosticStatus.Pass, DriverLatencyScan.ElevatedCode)],
            DriverLatencyScan.Reports(result).Select(r => (r.Status, r.IssueCode)));
    }

    [Fact]
    public void Over500UsIsANote_ThatClosesTheHighIssue()
    {
        var result = DriverLatencyScan.ToResult([new DriverLatency("RTKVHD64.sys", 650, 0, 3000, 10)], 10);

        Assert.Equal((DiagnosticStatus.Warning, DriverLatencyScan.ElevatedCode), (result.Status, result.IssueCode));
        Assert.Contains("RTKVHD64.sys ran a single DPC of 650 us", result.Reason);
        Assert.Contains("Update RTKVHD64.sys", result.RecommendedFix);
        Assert.Equal(IssueSeverity.Note, LogRuleTable.SeverityFor(DriverLatencyScan.ElevatedCode, IssueSeverity.Warning));
        Assert.Equal(
            [(DiagnosticStatus.Warning, DriverLatencyScan.ElevatedCode), (DiagnosticStatus.Pass, DriverLatencyScan.HighCode)],
            DriverLatencyScan.Reports(result).Select(r => (r.Status, r.IssueCode)));
    }

    [Fact]
    public void Over1000UsIsTheIssue_NamingEveryDriverPastIt_AndWhatUsuallyHelps()
    {
        var result = DriverLatencyScan.ToResult(
            [
                new DriverLatency("Netwtw10.sys", 400, 2400, 8000, 50),
                new DriverLatency("storport.sys", 1100, 0, 5000, 40),
                new DriverLatency("ndis.sys", 700, 0, 100, 4),
            ], 10);

        Assert.Equal((DiagnosticStatus.Warning, DriverLatencyScan.HighCode), (result.Status, result.IssueCode));
        Assert.Contains("Netwtw10.sys ran a single ISR of 2400 us", result.Reason);
        Assert.Contains("stutter", result.Reason);
        Assert.Contains("Update Netwtw10.sys, storport.sys from", result.RecommendedFix);
        Assert.DoesNotContain("ndis.sys", result.RecommendedFix);
        Assert.Contains("Wi-Fi, audio, RGB and storage drivers", result.RecommendedFix);
        Assert.Equal(IssueSeverity.Warning, LogRuleTable.SeverityFor(DriverLatencyScan.HighCode, IssueSeverity.Warning));
        Assert.Equal(DriverLatencyScan.ElevatedCode, DriverLatencyScan.Reports(result)[1].IssueCode);
    }

    [Fact]
    public void AnUnknownDriverPastTheLimitIsNeverNamedAsAFile_AndTheFixSaysItCouldNotTell()
    {
        var alone = DriverLatencyScan.ToResult([new DriverLatency(DriverLatencyTally.UnknownDriver, 1500, 0, 3000, 20)], 10);
        var beside = DriverLatencyScan.ToResult(
            [
                new DriverLatency(DriverLatencyTally.UnknownDriver, 1500, 0, 3000, 20),
                new DriverLatency("storport.sys", 1100, 0, 5000, 40),
            ], 10);

        Assert.Equal(DriverLatencyScan.HighCode, alone.IssueCode);
        Assert.StartsWith("A driver the scan could not name ran a single DPC of 1500 us", alone.Reason);
        Assert.StartsWith("The scan could not tell which driver it was.", alone.RecommendedFix);
        Assert.DoesNotContain("Update unknown", alone.RecommendedFix);
        Assert.StartsWith("Update storport.sys from", beside.RecommendedFix);
    }

    [Fact]
    public void TheRankingCrossesThePipeIntact()
    {
        IReadOnlyList<DriverLatency> sent = [new DriverLatency("ndis.sys", 120.5, 15, 175, 3)];

        var json = JsonSerializer.Serialize(sent, IpcJson.Options);

        Assert.Equal(sent, JsonSerializer.Deserialize<List<DriverLatency>>(json, IpcJson.Options));
    }
}
