using COPSBootstrapper.Core.Abstractions;
using COPSBootstrapper.Core.Models;

namespace COPSBootstrapper.Tests.Launch;

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

    public Task<Guid> GetActiveSchemeAsync(CancellationToken ct = default) => Task.FromResult(Active);
    public Task<string> GetSchemeNameAsync(Guid scheme, CancellationToken ct = default) => Task.FromResult("Fake Plan");
    public Task<Guid> ApplyAsync(PowerPlanKind kind, CancellationToken ct = default)
    {
        Log.Add($"apply:{kind}");
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

    public Task<IReadOnlyList<TrackedProcess>> GetTrackedProcessesAsync(CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<TrackedProcess>>([]);
    public Task<GameRuntimeState> GetGameStateAsync(CancellationToken ct = default)
        => Task.FromResult(GameRuntimeState.NotRunning);
    public Task<int?> WaitForGameStartAsync(TimeSpan timeout, CancellationToken ct = default)
        => Task.FromResult(GameStartPid);
    public async Task WaitForGameExitAsync(CancellationToken ct = default)
    {
        if (ExitAfter > TimeSpan.Zero)
        {
            await Task.Delay(ExitAfter, ct);
        }
    }
}

internal sealed class FakeProcessOptimizer : IProcessOptimizer
{
    public List<int> Applied { get; } = [];
    public List<int> Restored { get; } = [];
    public bool ReturnSnapshot { get; set; } = true;

    public Task<ProcessStateSnapshot?> ApplyAsync(int processId, PerformanceProfile profile, CancellationToken ct = default)
    {
        Applied.Add(processId);
        return Task.FromResult<ProcessStateSnapshot?>(ReturnSnapshot
            ? new ProcessStateSnapshot { ProcessId = processId, ProcessName = "crosvm" }
            : null);
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
    public Task StartAsync(int processId, CancellationToken ct = default)
    {
        Started = true;
        return Task.CompletedTask;
    }
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
    public Task InitializeAsync(CancellationToken ct = default) => Task.CompletedTask;
    public Task<long> SaveSessionAsync(SessionRecord record, CancellationToken ct = default)
    {
        Saved.Add(record);
        return Task.FromResult((long)Saved.Count);
    }
    public Task<IReadOnlyList<SessionRecord>> GetSessionsAsync(int limit = 50, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<SessionRecord>>(Saved);
    public Task<IReadOnlyList<SessionRecord>> GetSessionsByProfileAsync(string profileName, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<SessionRecord>>(Saved.Where(s => s.ProfileName == profileName).ToList());
}

internal sealed class FakeVirtualDisplay : IVirtualDisplayProvider
{
    public bool Active { get; set; }
    public List<string> Log { get; } = [];

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
