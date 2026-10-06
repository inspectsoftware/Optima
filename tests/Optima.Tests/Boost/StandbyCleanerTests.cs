using Microsoft.Extensions.Logging.Abstractions;
using Optima.Core.Abstractions;
using Optima.Core.Boost;
using Optima.Core.Ipc;
using Optima.Core.Models;
using Optima.Core.Monitoring;
using Optima.Monitoring.Metrics;
using Optima.Tests.Launch;
using Xunit;

namespace Optima.Tests.Boost;

public sealed class StandbyCleanerTests
{
    [Theory]
    [InlineData(500, 4000, true)]   // short on memory and the standby list holds it
    [InlineData(500, 800, false)]   // short on memory, but nothing worth purging
    [InlineData(6000, 9000, false)] // a big cache with plenty free is a healthy cache
    [InlineData(1024, 4000, false)] // at the threshold is not below it
    public void PurgesOnlyWhenMemoryIsShortAndStandbyHoldsIt(long freeMb, long standbyMb, bool expected)
        => Assert.Equal(expected, StandbyCleanerPolicy.ShouldPurge(freeMb, standbyMb, 1024, 1024));

    [Theory]
    [InlineData(1024, 1024, 5000, true)]
    [InlineData(64, 1024, 5000, false)]
    [InlineData(1024, 100000, 5000, false)]
    [InlineData(1024, 1024, 100, false)]
    public void ThresholdsOutsideTheLimitsAreRefused(int freeBelow, int standbyAbove, int intervalMs, bool expected)
        => Assert.Equal(expected, StandbyCleanerPolicy.IsValid(freeBelow, standbyAbove, intervalMs));

    private sealed class FakeBroker : IElevationBroker
    {
        public bool IsConnected { get; set; }
        public bool CurrentProcessIsElevated => false;
        public int Prompts { get; private set; }
        public List<IpcRequest> Sent { get; } = [];
        public event EventHandler<IpcEvent>? EventReceived;

        public Task<bool> EnsureStartedAsync(CancellationToken ct = default)
        {
            if (!IsConnected)
            {
                Prompts++;
                IsConnected = true;
            }
            return Task.FromResult(true);
        }

        public Task<IpcResponse> SendAsync(IpcRequest request, CancellationToken ct = default)
        {
            Sent.Add(request);
            return Task.FromResult(new IpcResponse { Success = true });
        }

        public void Raise(IpcEvent ipcEvent) => EventReceived?.Invoke(this, ipcEvent);

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private readonly FakeBroker _broker = new();
    private readonly GamePresenceService _presence = new(new FakeProcessMonitor(), NullLogger<GamePresenceService>.Instance);
    private AppSettings _settings = new() { BoostStandbyCleanerEnabled = true };

    private StandbyCleanerService Create()
        => new(_broker, _presence, () => _settings, NullLogger<StandbyCleanerService>.Instance);

    private void GameOnScreen() => _presence.ApplyState(GameRuntimeState.Running, DateTimeOffset.UtcNow);

    [Fact]
    public async Task AGameInTheBackground_NeverRaisesTheAdminPrompt()
    {
        GameOnScreen();
        using var cleaner = Create();

        await cleaner.SyncAsync(allowPrompt: false);

        Assert.Equal(0, _broker.Prompts);
        Assert.Empty(_broker.Sent);
        Assert.Equal(StandbyCleanerState.WaitingForHelper, cleaner.Status.State);
    }

    [Fact]
    public async Task WithTheHelperThere_TheCleanerStartsWithTheSavedThresholds()
    {
        _broker.IsConnected = true;
        _settings = _settings with { BoostStandbyFreeBelowMb = 2048, BoostStandbyAboveMb = 512 };
        GameOnScreen();
        using var cleaner = Create();

        await cleaner.SyncAsync(allowPrompt: false);

        var request = Assert.Single(_broker.Sent);
        Assert.Equal(IpcCommand.StartStandbyCleaner, request.Command);
        Assert.Equal("2048", request.Args["freeBelowMb"]);
        Assert.Equal("512", request.Args["standbyAboveMb"]);
        Assert.Equal(StandbyCleanerState.Running, cleaner.Status.State);
    }

    [Fact]
    public async Task WithoutTheGame_ItWaits_AndADeliberateSyncMayStartTheHelper()
    {
        using var cleaner = Create();

        await cleaner.SyncAsync(allowPrompt: true);

        Assert.Equal(1, _broker.Prompts);
        Assert.Empty(_broker.Sent);
        Assert.Equal(StandbyCleanerState.WaitingForGame, cleaner.Status.State);
    }

    [Fact]
    public async Task SwitchedOff_StopsARunningCleaner()
    {
        _broker.IsConnected = true;
        GameOnScreen();
        using var cleaner = Create();
        await cleaner.SyncAsync(allowPrompt: false);

        _settings = _settings with { BoostStandbyCleanerEnabled = false };
        await cleaner.SyncAsync(allowPrompt: false);

        Assert.Equal(IpcCommand.StopStandbyCleaner, _broker.Sent[^1].Command);
        Assert.Equal(StandbyCleanerState.Off, cleaner.Status.State);
    }

    [Fact]
    public void TheHelpersReadoutReachesTheStatus()
    {
        using var cleaner = Create();
        cleaner.Start();

        _broker.Raise(new IpcEvent
        {
            Kind = "memoryStatus",
            Data = new Dictionary<string, string>
            {
                ["freeMb"] = "731", ["standbyMb"] = "5120", ["purges"] = "2", ["lastFreedMb"] = "4096", ["lastPurgeAt"] = string.Empty,
            },
        });

        Assert.Equal(731, cleaner.Status.FreeMb);
        Assert.Equal(5120, cleaner.Status.StandbyMb);
        Assert.Equal(2, cleaner.Status.Purges);
        Assert.Equal(4096, cleaner.Status.LastFreedMb);
    }
}
