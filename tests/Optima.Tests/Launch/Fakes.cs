using Optima.Core.Abstractions;
using Optima.Core.Models;

namespace Optima.Tests.Launch;

/// <summary>Hand-written fakes for the orchestrator / recovery pipeline tests.</summary>
internal sealed class FakeDetector : IGameDetector
{
    public GooglePlayGamesInstallation? Platform { get; set; } = new() { InstallDirectory = @"C:\GPG" };
    public InstalledGame? Game { get; set; } = new()
    {
        PackageId = "com.criticalforceentertainment.criticalops",
        LaunchUri = "googleplaygames://launch/?id=com.criticalforceentertainment.criticalops",
    };

    public Task<GooglePlayGamesInstallation?> DetectPlatformAsync(CancellationToken ct = default) => Task.FromResult(Platform);
    public Task<IReadOnlyList<InstalledGame>> DetectInstalledGamesAsync(CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<InstalledGame>>(Game is null ? [] : [Game]);
    public Task<InstalledGame?> DetectTargetGameAsync(CancellationToken ct = default) => Task.FromResult(Game);
}

internal sealed class FakeLauncher : IGameLauncher
{
    public bool CanLaunch { get; set; } = true;
    public bool LaunchSucceeds { get; set; } = true;
    public int LaunchCalls { get; private set; }

    public string Name => "Fake";
    public int Order => 10;
    public Task<bool> CanLaunchAsync(InstalledGame game, CancellationToken ct = default) => Task.FromResult(CanLaunch);
    public Task<bool> LaunchAsync(InstalledGame game, CancellationToken ct = default)
    {
        LaunchCalls++;
        return Task.FromResult(LaunchSucceeds);
    }
}

internal sealed class FakeDisplayService : IDisplayService
{
    public List<string> Log { get; } = [];
    public string? RestoredTopology { get; private set; }

    public Task<IReadOnlyList<DisplayInfo>> GetDisplaysAsync(CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<DisplayInfo>>([]);
    public Task<IReadOnlyList<DisplayMode>> GetSupportedModesAsync(string deviceName, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<DisplayMode>>([]);
    public Task ApplyModeAsync(string deviceName, DisplayMode mode, CancellationToken ct = default)
    {
        Log.Add($"apply:{deviceName}:{mode}");
        return Task.CompletedTask;
    }
    public Task MakePrimaryAsync(string deviceName, CancellationToken ct = default)
    {
        Log.Add($"primary:{deviceName}");
        return Task.CompletedTask;
    }
    public Task<string> CaptureTopologyAsync(CancellationToken ct = default)
    {
        Log.Add("capture");
        return Task.FromResult("v1:topology");
    }
    public Task RestoreTopologyAsync(string topology, CancellationToken ct = default)
    {
        Log.Add("restoreTopology");
        RestoredTopology = topology;
        return Task.CompletedTask;
    }
}

internal sealed class FakePowerService : IPowerProfileService
{
    public Guid Active { get; set; } = Guid.Parse("11111111-1111-1111-1111-111111111111");
    public Guid? Restored { get; private set; }
    public List<string> Log { get; } = [];

    /// <summary>Thrown by the apply instead of switching the plan, as a PC without that plan does.</summary>
    public Exception? ApplyError { get; set; }

    public Task<Guid> GetActiveSchemeAsync(CancellationToken ct = default) => Task.FromResult(Active);
    public Task<string> GetSchemeNameAsync(Guid scheme, CancellationToken ct = default) => Task.FromResult("Fake Plan");

    /// <summary>What the PC lists; Balanced alone unless a test says otherwise.</summary>
    public List<Optima.Core.Launch.PowerScheme> Listed { get; } =
        [new(Optima.Core.Launch.PowerPlanPolicy.Balanced, "Balanced")];

    public Task<IReadOnlyList<Optima.Core.Launch.PowerScheme>> ListSchemesAsync(CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<Optima.Core.Launch.PowerScheme>>(Listed);
    public Task<Guid> ApplyAsync(PowerPlanKind kind, CancellationToken ct = default)
    {
        Log.Add($"apply:{kind}");
        if (ApplyError is not null)
        {
            return Task.FromException<Guid>(ApplyError);
        }
        var previous = Active;
        Active = Guid.Parse("22222222-2222-2222-2222-222222222222");
        return Task.FromResult(previous);
    }
    public Task RestoreAsync(Guid previousScheme, CancellationToken ct = default)
    {
        Log.Add("restore");
        Restored = previousScheme;
        Active = previousScheme;
        return Task.CompletedTask;
    }
}

internal sealed class FakeProcessMonitor : IProcessMonitor
{
    public int? GameStartPid { get; set; } = 4242;
    public TimeSpan ExitAfter { get; set; } = TimeSpan.Zero;

    /// <summary>Ends the wait before ExitAfter elapses, so a test does not have to wait it out.</summary>
    public TaskCompletionSource ExitNow { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public void EndGameNow() => ExitNow.TrySetResult();

    public IReadOnlyList<TrackedProcess> Tracked { get; set; } = [];

    public Task<IReadOnlyList<TrackedProcess>> GetTrackedProcessesAsync(CancellationToken ct = default)
        => Task.FromResult(Tracked);
    public Task<GameRuntimeState> GetGameStateAsync(CancellationToken ct = default)
        => Task.FromResult(GameRuntimeState.NotRunning);
    public Task<int?> WaitForGameStartAsync(TimeSpan timeout, CancellationToken ct = default)
        => Task.FromResult(GameStartPid);
    public async Task WaitForGameExitAsync(CancellationToken ct = default)
    {
        if (ExitAfter <= TimeSpan.Zero)
        {
            return;
        }

        var timer = Task.Delay(ExitAfter, ct);
        var finished = await Task.WhenAny(timer, ExitNow.Task).ConfigureAwait(false);
        if (finished == timer)
        {
            await timer.ConfigureAwait(false);
        }
    }
}

internal sealed class FakeProcessOptimizer : IProcessOptimizer
{
    public List<int> Applied { get; } = [];
    public List<PerformanceProfile> AppliedProfiles { get; } = [];
    public List<int> Reasserted { get; } = [];
    public List<PerformanceProfile> ReassertedProfiles { get; } = [];
    public List<int> Restored { get; } = [];
    public bool ReturnSnapshot { get; set; } = true;
    public Exception? ApplyError { get; set; }

    /// <summary>
    /// Holds each re-assert pass until the test releases it (or the session is cancelled), so a
    /// test can wait for a pass that has already been recorded instead of racing the clock.
    /// </summary>
    public TaskCompletionSource Gate { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public void ReleaseGate() => Gate.TrySetResult();

    public Task<ProcessStateSnapshot?> ApplyAsync(int processId, PerformanceProfile profile, CancellationToken ct = default)
    {
        Applied.Add(processId);
        AppliedProfiles.Add(profile);
        if (ApplyError is not null)
        {
            return Task.FromException<ProcessStateSnapshot?>(ApplyError);
        }
        return Task.FromResult<ProcessStateSnapshot?>(ReturnSnapshot
            ? new ProcessStateSnapshot { ProcessId = processId, ProcessName = "crosvm" }
            : null);
    }
    public async Task<bool> ReassertAsync(ProcessStateSnapshot baseline, PerformanceProfile profile, CancellationToken ct = default)
    {
        Reasserted.Add(baseline.ProcessId);
        ReassertedProfiles.Add(profile);
        await Gate.Task.WaitAsync(ct).ConfigureAwait(false);
        return false;
    }
    public Task RestoreAsync(ProcessStateSnapshot snapshot, CancellationToken ct = default)
    {
        Restored.Add(snapshot.ProcessId);
        return Task.CompletedTask;
    }
}

internal sealed class FakeCleanup : IBackgroundCleanupService
{
    public List<string> Closed { get; } = [];
    public Task<IReadOnlyDictionary<string, ulong>> EstimateImpactAsync(IReadOnlyList<string> processNames, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyDictionary<string, ulong>>(new Dictionary<string, ulong>());
    public Task<IReadOnlyList<string>> CloseAsync(IReadOnlyList<string> processNames, CancellationToken ct = default)
    {
        Closed.AddRange(processNames);
        return Task.FromResult<IReadOnlyList<string>>(processNames);
    }
}

internal sealed class FakeMetrics : IPerformanceMetricsProvider
{
    public bool Available { get; set; }
    public bool Started { get; private set; }
    public bool Stopped { get; private set; }

    public string Name => "FakeMetrics";
    public event EventHandler<(double Fps, double FrametimeMs)>? SampleArrived { add { } remove { } }

    public Task<bool> IsAvailableAsync(CancellationToken ct = default) => Task.FromResult(Available);
    public Task StartAsync(IReadOnlyList<int> processIds, CancellationToken ct = default)
    {
        Started = true;
        StartedPids = processIds;
        return Task.CompletedTask;
    }
    public IReadOnlyList<int> StartedPids { get; private set; } = [];
    public Task StopAsync()
    {
        Stopped = true;
        return Task.CompletedTask;
    }
    public SessionStats GetSessionStats() => new() { AverageFps = 200, SampleCount = 100 };
    public IReadOnlyList<double> GetFpsSamples() => [200, 201];
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

internal sealed class FakeSessionStore : ISessionStore
{
    public List<SessionRecord> Saved { get; } = [];
    public List<MatchRecord> Matches { get; } = [];
    public List<(Optima.Core.Stats.CopsProfileDelta? Delta, Optima.Core.Stats.CopsSeasonStats? Baseline, DateTimeOffset WindowStart)> AttachedDeltas { get; } = [];
    public List<(long SessionId, Optima.Core.Stats.CopsProfileDelta Delta)> UpdatedDeltas { get; } = [];
    public List<MatchRecord> UpdatedMatches { get; } = [];
    public long? AttachTargetId { get; set; } = 1;
    public bool UpdateResult { get; set; } = true;
    public Exception? SaveError { get; set; }

    public Task InitializeAsync(CancellationToken ct = default) => Task.CompletedTask;
    public Task<long> SaveSessionAsync(SessionRecord record, CancellationToken ct = default)
    {
        if (SaveError is not null)
        {
            return Task.FromException<long>(SaveError);
        }
        Saved.Add(record);
        return Task.FromResult((long)Saved.Count);
    }
    public Task<IReadOnlyList<SessionRecord>> GetSessionsAsync(int limit = 50, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<SessionRecord>>(Saved);
    public Task<IReadOnlyList<SessionRecord>> GetSessionsByProfileAsync(string profileName, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<SessionRecord>>(Saved.Where(s => s.ProfileName == profileName).ToList());
    public Task<IReadOnlyList<SessionRecord>> GetSessionsByIdsAsync(IReadOnlyList<long> ids, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<SessionRecord>>(Saved.Where(s => ids.Contains(s.Id)).ToList());
    public Task<long?> AttachStatsAsync(Optima.Core.Stats.CopsProfileDelta? delta,
        Optima.Core.Stats.CopsSeasonStats? baseline, DateTimeOffset windowStart, CancellationToken ct = default)
    {
        AttachedDeltas.Add((delta, baseline, windowStart));
        return Task.FromResult(AttachTargetId);
    }
    public Task<bool> UpdateStatsDeltaAsync(long sessionId, Optima.Core.Stats.CopsProfileDelta delta, CancellationToken ct = default)
    {
        UpdatedDeltas.Add((sessionId, delta));
        return Task.FromResult(UpdateResult);
    }
    public Optima.Core.Abstractions.SessionEndBoundary EndBoundary { get; set; } = new(false, null);
    public Task<Optima.Core.Abstractions.SessionEndBoundary> GetSessionEndBoundaryAsync(long sessionId, CancellationToken ct = default)
        => Task.FromResult(EndBoundary);
    public Task<long> SaveMatchAsync(MatchRecord match, CancellationToken ct = default)
    {
        Matches.Add(match);
        return Task.FromResult((long)Matches.Count);
    }
    public Task UpdateMatchAsync(MatchRecord match, CancellationToken ct = default)
    {
        UpdatedMatches.Add(match);
        return Task.CompletedTask;
    }
    public Task DeleteMatchAsync(long matchId, CancellationToken ct = default) => Task.CompletedTask;
    public Task<IReadOnlyList<MatchRecord>> GetMatchesAsync(int limit = 100, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<MatchRecord>>(Matches);
}

internal sealed class FakeNetworkMonitor : INetworkQualityMonitor
{
    public bool Started { get; private set; }
    public bool Stopped { get; private set; }
    public NetworkQualityStats? StatsToReturn { get; set; }

    public NetworkQualitySample? Latest => null;
    public event EventHandler<NetworkQualitySample>? SampleArrived { add { } remove { } }

    public Task StartAsync(IReadOnlyList<int> processIds, CancellationToken ct = default)
    {
        Started = true;
        return Task.CompletedTask;
    }
    public Task<NetworkQualityStats?> StopAsync()
    {
        Stopped = true;
        return Task.FromResult(StatsToReturn);
    }
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

internal sealed class FakeTweakService : ITweakService
{
    public List<TweakState> States { get; } = [];
    public Task<IReadOnlyList<TweakState>> GetStatesAsync(CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<TweakState>>(States);
    public Task<TweakState> SetEnabledAsync(string tweakId, bool enable, CancellationToken ct = default)
        => throw new NotSupportedException();
}

internal sealed class FakeVirtualDisplay : IVirtualDisplayProvider
{
    public bool Active { get; set; }
    public List<string> Log { get; } = [];
    public Exception? EnableError { get; set; }

    public string Name => "FakeVdd";
    public Task<bool> IsAvailableAsync(CancellationToken ct = default) => Task.FromResult(true);
    public Task<DriverCapabilities> GetCapabilitiesAsync(CancellationToken ct = default) => Task.FromResult(new DriverCapabilities());
    public Task InitializeAsync(CancellationToken ct = default)
    {
        Log.Add("init");
        return Task.CompletedTask;
    }
    public Task CreateDisplayAsync(CancellationToken ct = default) => EnableDisplayAsync(ct);
    public Task EnableDisplayAsync(CancellationToken ct = default)
    {
        Log.Add("enable");
        if (EnableError is not null)
        {
            return Task.FromException(EnableError);
        }
        Active = true;
        return Task.CompletedTask;
    }
    public Task DisableDisplayAsync(CancellationToken ct = default)
    {
        Log.Add("disable");
        Active = false;
        return Task.CompletedTask;
    }
    public Task SetResolutionAsync(int width, int height, CancellationToken ct = default) => Task.CompletedTask;
    public Task SetRefreshRateAsync(int refreshRate, CancellationToken ct = default) => Task.CompletedTask;
    public Task SetModeAsync(DisplayMode mode, CancellationToken ct = default)
    {
        Log.Add($"mode:{mode}");
        return Task.CompletedTask;
    }
    public Task<IReadOnlyList<DisplayMode>> GetSupportedModesAsync(CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<DisplayMode>>([]);
    public Task<DisplayMode?> GetCurrentModeAsync(CancellationToken ct = default) => Task.FromResult<DisplayMode?>(null);
    public Task<bool> IsDisplayActiveAsync(CancellationToken ct = default) => Task.FromResult(Active);
    public Task<DisplayInfo?> GetDisplayInfoAsync(CancellationToken ct = default) => Task.FromResult<DisplayInfo?>(null);
    public Task RestoreOriginalStateAsync(CancellationToken ct = default)
    {
        Log.Add("restoreOriginal");
        return Task.CompletedTask;
    }
}
