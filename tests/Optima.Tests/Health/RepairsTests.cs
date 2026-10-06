using Microsoft.Extensions.Logging.Abstractions;
using Optima.Core.Abstractions;
using Optima.Core.Configuration;
using Optima.Core.Health;
using Optima.Core.Health.Repairs;
using Optima.Core.Models;
using Optima.Core.Recovery;
using Optima.Tests.Launch;
using Xunit;

namespace Optima.Tests.Health;

public sealed class RepairsTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "optima-repair-" + Guid.NewGuid().ToString("N"));
    private readonly AppPaths _paths;
    private readonly JsonStore _store = new(NullLogger<JsonStore>.Instance);
    private readonly FakePlatform _platform = new();
    private readonly FakeDisplayService _display = new();

    private static readonly Issue AnIssue = new()
    {
        Key = "LAUNCH_FAILED",
        Code = "LAUNCH_FAILED",
        Severity = IssueSeverity.Error,
        Title = "Could not start Critical Ops",
        FirstSeen = DateTimeOffset.Now,
        LastSeen = DateTimeOffset.Now,
    };

    public RepairsTests()
    {
        _paths = new AppPaths(_root);
        _paths.EnsureCreated();
    }

    private sealed class FakePlatform : IPlatformControl
    {
        public int Running { get; set; }
        public bool Installed { get; set; } = true;
        public List<string> Calls { get; } = [];

        public Task<int> CountRunningAsync(CancellationToken ct = default) => Task.FromResult(Running);

        public Task<bool> StartAsync(CancellationToken ct = default)
        {
            Calls.Add("start");
            if (Installed)
            {
                Running = 4;
            }
            return Task.FromResult(Installed);
        }

        public Task<int> StopAsync(CancellationToken ct = default)
        {
            Calls.Add("stop");
            var stopped = Running;
            Running = 0;
            return Task.FromResult(stopped);
        }
    }

    private RecoveryService Recovery() => new(
        _paths, _store, _display, new FakePowerService(), new FakeVirtualDisplay(), new FakeProcessOptimizer(),
        NullLogger<RecoveryService>.Instance, retryDelay: TimeSpan.Zero);

    [Fact]
    public async Task APlatformThatIsNotRunningIsStarted()
    {
        var result = await new StartPlatformRepair(_platform).RunAsync(AnIssue);

        Assert.Equal(RepairOutcome.Fixed, result.Outcome);
        Assert.Equal(["start"], _platform.Calls);
    }

    [Fact]
    public async Task APlatformThatIsRunningIsLeftExactlyAsItIs()
    {
        _platform.Running = 5;

        var result = await new StartPlatformRepair(_platform).RunAsync(AnIssue);

        // Safe means safe: with the platform up, and maybe a game in it, it touches nothing.
        Assert.Equal(RepairOutcome.NotNeeded, result.Outcome);
        Assert.Empty(_platform.Calls);
    }

    [Fact]
    public async Task APlatformThatIsNotInstalledCannotBeStarted()
    {
        _platform.Installed = false;

        Assert.Equal(RepairOutcome.Failed, (await new StartPlatformRepair(_platform).RunAsync(AnIssue)).Outcome);
    }

    [Fact]
    public async Task ARestartStopsFirstAndSaysHowMuchItStopped()
    {
        _platform.Running = 7;

        var result = await new RestartPlatformRepair(_platform, settle: TimeSpan.Zero).RunAsync(AnIssue);

        Assert.Equal(RepairOutcome.Fixed, result.Outcome);
        Assert.Equal(["stop", "start"], _platform.Calls);
        Assert.Contains("Stopped 7 process(es)", result.Summary);
    }

    [Fact]
    public void WhatARepairAsksOfThePlayerIsSaidOnItsFace()
    {
        IRepairAction start = new StartPlatformRepair(_platform);
        IRepairAction restart = new RestartPlatformRepair(_platform);
        IRepairAction retry = new RetryRestoreRepair(Recovery());

        Assert.Equal(RepairTier.Safe, start.Tier);
        Assert.Equal(RepairTier.Disruptive, restart.Tier);
        // It can switch the virtual display off through the elevated helper.
        Assert.Equal(RepairTier.Elevated, retry.Tier);
        Assert.Contains("A running game is closed", restart.Changes);
    }

    [Fact]
    public async Task WithNothingPendingThereIsNoRestoreToTryAgain()
        => Assert.Equal(RepairOutcome.NotNeeded, (await new RetryRestoreRepair(Recovery()).RunAsync(AnIssue)).Outcome);

    [Fact]
    public async Task ARestoreThatWasLeftUnfinishedIsFinished()
    {
        var recovery = Recovery();
        await recovery.SavePendingAsync(new SystemStateSnapshot { DisplayTopology = "v1:xyz" });

        var result = await new RetryRestoreRepair(recovery).RunAsync(AnIssue);

        Assert.Equal(RepairOutcome.Fixed, result.Outcome);
        Assert.Equal("v1:xyz", _display.RestoredTopology);
        Assert.Null(await recovery.GetPendingAsync());
    }

    [Fact]
    public async Task ARestoreThatStillFailsSaysWhatIsStillOwed()
    {
        var recovery = Recovery();
        await recovery.SavePendingAsync(new SystemStateSnapshot { DisplayTopology = "v1:xyz" });
        _display.RestoreTopologyErrors.Enqueue(new InvalidOperationException("refused"));
        _display.RestoreTopologyErrors.Enqueue(new InvalidOperationException("refused"));

        var result = await new RetryRestoreRepair(recovery).RunAsync(AnIssue);

        Assert.Equal(RepairOutcome.Failed, result.Outcome);
        Assert.Contains("display topology", result.Summary);
        Assert.NotNull(await recovery.GetPendingAsync());
    }

    [Fact]
    public async Task DiscardingAPendingDisplayRestoreKeepsTheBackup()
    {
        var backup = Path.Combine(_paths.BackupsDirectory, "vdd_settings-1789554030.xml");
        File.WriteAllText(backup, "<vdd_settings />");
        File.WriteAllLines(_paths.VddRestoreMarkerFile, [backup, @"C:\VirtualDisplayDriver\vdd_settings.xml"]);
        var repair = new DiscardDisplayRestoreRepair(_paths);

        var result = await repair.RunAsync(AnIssue);

        Assert.Equal(RepairOutcome.Fixed, result.Outcome);
        Assert.False(File.Exists(_paths.VddRestoreMarkerFile));
        // Only the instruction to restore is dropped. The thing that could be restored stays.
        Assert.True(File.Exists(backup));
        Assert.Equal(RepairOutcome.NotNeeded, (await repair.RunAsync(AnIssue)).Outcome);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}
