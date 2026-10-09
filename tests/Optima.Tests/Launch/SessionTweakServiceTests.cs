using Microsoft.Extensions.Logging.Abstractions;
using Optima.Core.Abstractions;
using Optima.Core.Launch;
using Optima.Core.Models;
using Optima.Core.Monitoring;
using Xunit;

namespace Optima.Tests.Launch;

public sealed class SessionTweakServiceTests
{
    private const string GameBarOff = "session-gamebar-off";

    /// <summary>Records what was switched, a moment after being asked, as a service that reads a file first does.</summary>
    private sealed class SlowTweaks : ITweakService
    {
        public List<(string Id, bool Enable)> Switched { get; } = [];

        public Task<IReadOnlyList<TweakState>> GetStatesAsync(CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<TweakState>>([]);

        public async Task<TweakState> SetEnabledAsync(string tweakId, bool enable, CancellationToken ct = default)
        {
            await Task.Delay(50, ct).ConfigureAwait(false);
            lock (Switched)
            {
                Switched.Add((tweakId, enable));
            }
            return new TweakState(SessionTweakCatalog.Find(tweakId)!, enable ? TweakStatus.Enabled : TweakStatus.Disabled);
        }
    }

    [Fact]
    public async Task Dispose_PutsTheSessionTweaksBackBeforeItReturns()
    {
        var tweaks = new SlowTweaks();
        var presence = new GamePresenceService(new FakeProcessMonitor(), NullLogger<GamePresenceService>.Instance);
        var service = new SessionTweakService(tweaks, presence, NullLogger<SessionTweakService>.Instance)
        {
            EnabledIds = [GameBarOff],
        };
        var applied = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        service.StatusChanged += _ => applied.TrySetResult();
        service.Start();
        presence.ApplyState(GameRuntimeState.Running, DateTimeOffset.UtcNow);
        await applied.Task.WaitAsync(TimeSpan.FromSeconds(10));

        // Optima exits with the game still on screen. Once this returns the process is on its way
        // out, so a restore that is still running by then never writes anything back.
        service.Dispose();

        Assert.Equal([(GameBarOff, true), (GameBarOff, false)], tweaks.Switched);
    }
}
