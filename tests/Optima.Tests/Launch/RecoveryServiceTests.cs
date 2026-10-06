using Optima.Core.Configuration;
using Optima.Core.Models;
using Optima.Core.Recovery;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Optima.Tests.Launch;

public sealed class RecoveryServiceTests : IDisposable
{
    private readonly string _tempRoot = Path.Combine(Path.GetTempPath(), "optima-rec-" + Guid.NewGuid().ToString("N"));
    private readonly AppPaths _paths;
    private readonly JsonStore _store = new(NullLogger<JsonStore>.Instance);
    private readonly FakeDisplayService _displayService = new();
    private readonly FakePowerService _power = new();
    private readonly FakeVirtualDisplay _virtualDisplay = new();
    private readonly FakeProcessOptimizer _processOptimizer = new();

    public RecoveryServiceTests()
    {
        _paths = new AppPaths(_tempRoot);
        _paths.EnsureCreated();
    }

    private RecoveryService CreateService() => new(
        _paths, _store, _displayService, _power, _virtualDisplay, _processOptimizer,
        NullLogger<RecoveryService>.Instance, retryDelay: TimeSpan.Zero);

    private static Exception Refused() => new InvalidOperationException("SetDisplayConfig was refused");

    [Fact]
    public async Task Pending_RoundTripsThroughDisk()
    {
        var service = CreateService();
        var snapshot = new SystemStateSnapshot
        {
            ProfileName = "Competitive",
            PreviousPowerScheme = Guid.Parse("33333333-3333-3333-3333-333333333333"),
            DisplayTopology = "v1:abc",
        };

        await service.SavePendingAsync(snapshot);
        var loaded = await CreateService().GetPendingAsync(); // fresh instance = fresh process simulation

        Assert.NotNull(loaded);
        Assert.Equal("Competitive", loaded.ProfileName);
        Assert.Equal(snapshot.PreviousPowerScheme, loaded.PreviousPowerScheme);
        Assert.Equal("v1:abc", loaded.DisplayTopology);
    }

    [Fact]
    public async Task Restore_RunsEveryRecordedStep_AndClearsPending()
    {
        var service = CreateService();
        var snapshot = new SystemStateSnapshot
        {
            ProfileName = "Test",
            PreviousPowerScheme = Guid.NewGuid(),
            DisplayTopology = "v1:xyz",
            VirtualDisplayEnabledByUs = true,
            ProcessStates = [new ProcessStateSnapshot { ProcessId = 99, ProcessName = "crosvm" }],
        };
        await service.SavePendingAsync(snapshot);

        await service.RestoreAsync(snapshot);

        Assert.Equal(snapshot.PreviousPowerScheme, _power.Restored);
        Assert.Equal("v1:xyz", _displayService.RestoredTopology);
        Assert.Contains("restoreOriginal", _virtualDisplay.Log);
        Assert.Equal([99], _processOptimizer.Restored);
        Assert.Null(await service.GetPendingAsync());
    }

    [Fact]
    public async Task Restore_EmptySnapshot_DoesNothingButClears()
    {
        var service = CreateService();
        var snapshot = new SystemStateSnapshot { ProfileName = "Nothing" };
        await service.SavePendingAsync(snapshot);

        await service.RestoreAsync(snapshot);

        Assert.Null(_power.Restored);
        Assert.Empty(_displayService.Log);
        Assert.Empty(_virtualDisplay.Log);
        Assert.Null(await service.GetPendingAsync());
    }

    [Fact]
    public async Task Restore_AStepThatFailsOnce_IsTriedAgainAndCountsAsRestored()
    {
        var service = CreateService();
        var snapshot = new SystemStateSnapshot { DisplayTopology = "v1:xyz" };
        await service.SavePendingAsync(snapshot);
        _displayService.RestoreTopologyErrors.Enqueue(Refused());

        var report = await service.RestoreAsync(snapshot);

        // Windows refuses a display change while another settles; a moment later it goes through.
        Assert.True(report.AllRestored);
        Assert.Equal("v1:xyz", _displayService.RestoredTopology);
        Assert.Equal(2, _displayService.Log.Count(entry => entry == "restoreTopology"));
        Assert.Null(await service.GetPendingAsync());
    }

    [Fact]
    public async Task Restore_AFailedStep_StaysPendingAndOnlyThatStep()
    {
        var service = CreateService();
        var snapshot = new SystemStateSnapshot
        {
            ProfileName = "Test",
            PreviousPowerScheme = Guid.NewGuid(),
            DisplayTopology = "v1:xyz",
            ProcessStates = [new ProcessStateSnapshot { ProcessId = 99, ProcessName = "crosvm" }],
        };
        await service.SavePendingAsync(snapshot);
        _displayService.RestoreTopologyErrors.Enqueue(Refused());
        _displayService.RestoreTopologyErrors.Enqueue(Refused());

        var report = await service.RestoreAsync(snapshot);

        Assert.Equal(["display topology"], report.Failed);
        // The power plan did go back, and is not owed any more; the layout still is.
        Assert.Equal(snapshot.PreviousPowerScheme, _power.Restored);
        var left = await service.GetPendingAsync();
        Assert.NotNull(left);
        Assert.Equal("v1:xyz", left.DisplayTopology);
        Assert.Null(left.PreviousPowerScheme);
        Assert.Empty(left.ProcessStates);
    }

    [Fact]
    public async Task Restore_FinishedLater_PutsBackOnlyWhatWasLeft()
    {
        var service = CreateService();
        var snapshot = new SystemStateSnapshot { PreviousPowerScheme = Guid.NewGuid(), DisplayTopology = "v1:xyz" };
        await service.SavePendingAsync(snapshot);
        _displayService.RestoreTopologyErrors.Enqueue(Refused());
        _displayService.RestoreTopologyErrors.Enqueue(Refused());
        await service.RestoreAsync(snapshot);

        var report = await service.RestoreAsync((await service.GetPendingAsync())!);

        Assert.True(report.AllRestored);
        Assert.Equal("v1:xyz", _displayService.RestoredTopology);
        // Once: switching the power plan a second time would undo whatever was chosen since.
        Assert.Single(_power.Log, entry => entry == "restore");
        Assert.Null(await service.GetPendingAsync());
    }

    [Fact]
    public async Task Restore_AProcessThatIsGone_NeverKeepsTheRestorePending()
    {
        var service = CreateService();
        var snapshot = new SystemStateSnapshot { ProcessStates = [new ProcessStateSnapshot { ProcessId = 99, ProcessName = "crosvm" }] };
        await service.SavePendingAsync(snapshot);
        _processOptimizer.RestoreError = new InvalidOperationException("the process has exited");

        var report = await service.RestoreAsync(snapshot);

        Assert.True(report.AllRestored);
        Assert.Null(await service.GetPendingAsync());
    }

    [Fact]
    public async Task GetPending_NoFile_ReturnsNull()
    {
        Assert.Null(await CreateService().GetPendingAsync());
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_tempRoot, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}
