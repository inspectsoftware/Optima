using Microsoft.Extensions.Logging.Abstractions;
using Optima.Core.Abstractions;
using Optima.Core.Launch;
using Optima.Core.Models;
using Xunit;

namespace Optima.Tests.Launch;

public sealed class PriorityGuardServiceTests
{
    private readonly FakeProcessMonitor _monitor = new();
    private readonly FakeProcessOptimizer _optimizer = new();
    private AppSettings _settings = new() { GamePriority = "High", BoostPriorityGuardEnabled = true };
    private bool _sessionActive;

    private PriorityGuardService Create()
    {
        // The fake holds a re-assert until released; these tests do not need that.
        _optimizer.ReleaseGate();
        return new PriorityGuardService(
            _monitor, _optimizer, () => _settings, () => _sessionActive, NullLogger<PriorityGuardService>.Instance);
    }

    private static TrackedProcess Emulator(int pid)
        => new() { ProcessId = pid, Name = "crosvm", Kind = TrackedProcessKind.Emulator };

    [Fact]
    public async Task EveryEmulatorProcessIsRaised_AndOnlyEmulators()
    {
        _monitor.Tracked =
        [
            Emulator(10), Emulator(11),
            new TrackedProcess { ProcessId = 12, Name = "Service", Kind = TrackedProcessKind.Platform },
        ];
        using var guard = Create();

        await guard.CheckNowAsync();

        Assert.Equal([10, 11], _optimizer.Applied);
        Assert.All(_optimizer.AppliedProfiles, p => Assert.Equal(ProcessPriorityLevel.High, p.Priority));
        Assert.Equal(2, guard.Status.ProcessCount);
    }

    [Fact]
    public async Task AKnownProcessIsReasserted_AndARestartedOneIsRaisedAgain()
    {
        _monitor.Tracked = [Emulator(10)];
        using var guard = Create();
        await guard.CheckNowAsync();

        await guard.CheckNowAsync();
        Assert.Equal([10], _optimizer.Applied);
        Assert.Equal([10], _optimizer.Reasserted);

        // The emulator restarted under a new pid.
        _monitor.Tracked = [Emulator(20)];
        await guard.CheckNowAsync();

        Assert.Equal([10, 20], _optimizer.Applied);
        Assert.Equal(1, guard.Status.ProcessCount);
    }

    [Fact]
    public async Task AnActiveSessionOwnsThePriority_TheGuardWritesNothing()
    {
        _monitor.Tracked = [Emulator(10)];
        _sessionActive = true;
        using var guard = Create();

        await guard.CheckNowAsync();

        Assert.Empty(_optimizer.Applied);
        Assert.Empty(_optimizer.Restored);
        Assert.True(guard.Status.SessionOwned);
    }

    [Fact]
    public async Task SwitchingTheGuardOff_PutsTheOriginalBack()
    {
        _monitor.Tracked = [Emulator(10)];
        using var guard = Create();
        await guard.CheckNowAsync();

        _settings = _settings with { BoostPriorityGuardEnabled = false };
        await guard.CheckNowAsync();

        Assert.Equal([10], _optimizer.Restored);
        Assert.Equal(0, guard.Status.ProcessCount);
    }

    [Fact]
    public async Task UnchangedPriority_MeansNothingToKeep()
    {
        _monitor.Tracked = [Emulator(10)];
        _settings = _settings with { GamePriority = "Unchanged" };
        using var guard = Create();

        await guard.CheckNowAsync();

        Assert.Empty(_optimizer.Applied);
    }

    [Fact]
    public async Task AChangedPriorityIsPickedUpOnTheNextPass()
    {
        _monitor.Tracked = [Emulator(10)];
        using var guard = Create();
        await guard.CheckNowAsync();

        _settings = _settings with { GamePriority = "AboveNormal" };
        await guard.CheckNowAsync();

        Assert.Equal(ProcessPriorityLevel.AboveNormal, _optimizer.ReassertedProfiles[^1].Priority);
    }

    [Fact]
    public async Task Dispose_RestoresWhatItRaised()
    {
        _monitor.Tracked = [Emulator(10)];
        var guard = Create();
        await guard.CheckNowAsync();

        guard.Dispose();

        Assert.Equal([10], _optimizer.Restored);
    }
}
