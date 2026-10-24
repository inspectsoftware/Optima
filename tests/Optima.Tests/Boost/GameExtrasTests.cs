using Microsoft.Extensions.Logging.Abstractions;
using Optima.Core.Abstractions;
using Optima.Core.Configuration;
using Optima.Core.Launch;
using Optima.Core.Models;
using Optima.Core.Monitoring;
using Optima.Platform.Windows.Services;
using Optima.Tests.Launch;
using Xunit;

namespace Optima.Tests.Boost;

public sealed class GameExtrasTests : IDisposable
{
    private sealed class FakeExtras : IGameExtras
    {
        public bool PlanAlreadyAwake { get; set; }
        public int Raised { get; private set; }
        public List<CoreParkingSnapshot> Restored { get; } = [];
        public HashSet<string> HighPerformance { get; } = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>Adapter choices other than high performance, such as "1" for power saving.</summary>
        public Dictionary<string, string> Other { get; } = new(StringComparer.OrdinalIgnoreCase);
        public string? PathOfGame { get; set; } = @"C:\Program Files\Google\Play Games\current\emulator\crosvm.exe";

        public CoreParkingSnapshot? KeepCoresUnparked()
        {
            if (PlanAlreadyAwake)
            {
                return null;
            }
            Raised++;
            return new CoreParkingSnapshot(Guid.Parse("381b4222-f694-41f0-9685-ff5bb260df2e"), 50, 10);
        }

        public void RestoreCoreParking(CoreParkingSnapshot snapshot) => Restored.Add(snapshot);
        public string? GetProcessPath(int processId) => PathOfGame;
        public string? GetGpuPreference(string executablePath)
            => HighPerformance.Contains(executablePath) ? "2" : Other.GetValueOrDefault(executablePath);

        /// <summary>Runs before a preference is written, with the preference about to be written.</summary>
        public Action<string?>? BeforeSet { get; set; }

        public void SetGpuPreference(string executablePath, string? preference)
        {
            BeforeSet?.Invoke(preference);
            HighPerformance.Remove(executablePath);
            Other.Remove(executablePath);
            if (preference == "2")
            {
                HighPerformance.Add(executablePath);
            }
            else if (preference is not null)
            {
                Other[executablePath] = preference;
            }
        }
    }

    private readonly string _folder = Path.Combine(Path.GetTempPath(), "optima-extras-" + Guid.NewGuid().ToString("N"));
    private readonly FakeExtras _extras = new();
    private readonly FakeProcessMonitor _monitor = new();
    private readonly GamePresenceService _presence;
    private readonly JsonStore _store = new(NullLogger<JsonStore>.Instance);
    private AppSettings _settings = new() { BoostKeepCoresAwake = true, BoostGpuHighPerformance = true };

    public GameExtrasTests()
    {
        _presence = new GamePresenceService(_monitor, NullLogger<GamePresenceService>.Instance);
        _monitor.Tracked = [new TrackedProcess { ProcessId = 5, Name = "crosvm", Kind = TrackedProcessKind.Emulator }];
    }

    private string Ledger => Path.Combine(_folder, "boost-coreparking.json");

    private GameExtrasService Create(SettingsService? stored = null)
        => new(_extras, _monitor, _presence, () => _settings,
            path =>
            {
                _settings = _settings with { BoostGamePath = path };
                return Task.CompletedTask;
            },
            _store, Ledger, NullLogger<GameExtrasService>.Instance, stored);

    public void Dispose()
    {
        if (Directory.Exists(_folder))
        {
            Directory.Delete(_folder, recursive: true);
        }
    }

    [Fact]
    public async Task CoresAreKeptAwakeOnlyWhileTheGameIsOnScreen_AndPutBack()
    {
        using var service = Create();
        await service.SyncAsync();
        Assert.Equal(0, _extras.Raised);

        _presence.ApplyState(GameRuntimeState.Running, DateTimeOffset.UtcNow);
        await service.SyncAsync();
        await service.SyncAsync();
        Assert.Equal(1, _extras.Raised);
        Assert.True(service.Status.CoresHeld);
        Assert.True(File.Exists(Ledger));

        _presence.ApplyState(GameRuntimeState.NotRunning, DateTimeOffset.UtcNow);
        await service.SyncAsync();
        var restored = Assert.Single(_extras.Restored);
        Assert.Equal(50u, restored.AcMinCoresPercent);
        Assert.Equal(10u, restored.DcMinCoresPercent);
        Assert.False(File.Exists(Ledger));
    }

    [Fact]
    public async Task APlanThatAlreadyKeepsEveryCoreAwake_IsLeftAlone()
    {
        _extras.PlanAlreadyAwake = true;
        _presence.ApplyState(GameRuntimeState.Running, DateTimeOffset.UtcNow);
        using var service = Create();

        await service.SyncAsync();

        Assert.False(service.Status.CoresHeld);
        Assert.True(service.Status.CoresAlreadyAwake);
        Assert.False(File.Exists(Ledger));
    }

    [Fact]
    public async Task ALedgerFromACrashedRun_IsPutBackOnTheNextStart()
    {
        var left = new CoreParkingSnapshot(Guid.NewGuid(), 25, 5);
        await _store.SaveAsync(Ledger, left);
        using var service = Create();

        await service.RestoreLeftoverAsync();

        Assert.Equal(left, Assert.Single(_extras.Restored));
        Assert.False(File.Exists(Ledger));
    }

    [Fact]
    public async Task TheGraphicsPreferenceIsSetOnceTheGameIsSeen_AndRemovedWhenSwitchedOff()
    {
        using var service = Create();
        await service.SyncAsync();
        Assert.Empty(_extras.HighPerformance);   // the game's path is not known yet

        _presence.ApplyState(GameRuntimeState.Running, DateTimeOffset.UtcNow);
        await service.SyncAsync();
        Assert.Contains(_extras.PathOfGame!, _extras.HighPerformance);
        Assert.Equal(_extras.PathOfGame, _settings.BoostGamePath);

        // Off, with the game closed: the remembered path is what lets it be removed.
        _presence.ApplyState(GameRuntimeState.NotRunning, DateTimeOffset.UtcNow);
        _settings = _settings with { BoostGpuHighPerformance = false };
        await service.SyncAsync();
        Assert.Empty(_extras.HighPerformance);
    }

    [Fact]
    public async Task APreferenceThePlayerSetInWindows_IsLeftAloneWhileTheSwitchStaysOff()
    {
        _settings = _settings with { BoostGpuHighPerformance = false, BoostGamePath = _extras.PathOfGame };
        _extras.HighPerformance.Add(_extras.PathOfGame!);
        using var service = Create();

        await service.SyncAsync();
        await service.SyncAsync();

        Assert.Contains(_extras.PathOfGame!, _extras.HighPerformance);
    }

    [Fact]
    public async Task APreferenceThePlayerSetInWindows_IsLeftAloneWhenTheSwitchGoesOnAndOff()
    {
        _settings = _settings with { BoostGpuHighPerformance = false, BoostGamePath = _extras.PathOfGame };
        _extras.HighPerformance.Add(_extras.PathOfGame!);
        using var service = Create();

        // Already high performance, so Boost writes nothing and keeps no ledger of it.
        _settings = _settings with { BoostGpuHighPerformance = true };
        await service.SyncAsync();
        _settings = _settings with { BoostGpuHighPerformance = false };
        await service.SyncAsync();

        Assert.Contains(_extras.PathOfGame!, _extras.HighPerformance);
    }

    [Fact]
    public async Task APreferenceBoostWrote_IsTakenAwayAtTheNextStart_WhichBeginsWithBoostOff()
    {
        // Run 1 ends with Boost on and without its own clean-up (killed, or a crash).
        var paths = new AppPaths(_folder);
        paths.EnsureCreated();
        var run1 = new SettingsService(paths, _store, NullLogger<SettingsService>.Instance);
        await run1.UpdateSettingsAsync(s => s with { BoostEnabled = true, BoostGpuHighPerformance = true });
        _settings = run1.Current!.BoostEffective();
        _presence.ApplyState(GameRuntimeState.Running, DateTimeOffset.UtcNow);
        await Create().SyncAsync();
        Assert.Contains(_extras.PathOfGame!, _extras.HighPerformance);

        // Run 2 reads the tick back but not the switch, so the feature reads off: what Boost wrote goes.
        _presence.ApplyState(GameRuntimeState.NotRunning, DateTimeOffset.UtcNow);
        var run2 = new SettingsService(paths, _store, NullLogger<SettingsService>.Instance);
        var reloaded = await run2.GetSettingsAsync();
        Assert.False(reloaded.BoostEnabled);
        Assert.True(reloaded.BoostGpuHighPerformance);
        _settings = reloaded.BoostEffective();
        using var service = Create();
        await service.SyncAsync();

        Assert.DoesNotContain(_extras.PathOfGame!, _extras.HighPerformance);
        Assert.False(service.Status.GpuApplied);
        Assert.False(File.Exists(Path.Combine(_folder, "boost-gpu.json")));
    }

    [Fact]
    public async Task APreferenceLeftByAnOlderReleaseWithoutALedger_IsTakenAwayOnce()
    {
        // As 0.8.1 left it: Boost saved on, the graphics switch ticked, the game seen, and no ledger.
        var paths = new AppPaths(_folder);
        paths.EnsureCreated();
        File.WriteAllText(paths.ConfigFile,
            "{\"boostEnabled\":true,\"boostGpuHighPerformance\":true,\"boostGamePath\":"
            + System.Text.Json.JsonSerializer.Serialize(_extras.PathOfGame) + "}");
        _extras.HighPerformance.Add(_extras.PathOfGame!);
        // Something in this build saves the settings before the look; the old switch survives it.
        await new SettingsService(paths, _store, NullLogger<SettingsService>.Instance)
            .UpdateSettingsAsync(s => s with { Theme = "Light" });
        var stored = new SettingsService(paths, _store, NullLogger<SettingsService>.Instance);
        _settings = (await stored.GetSettingsAsync()).BoostEffective();
        var service = Create(stored);

        await service.AdoptOlderReleasePreferenceAsync();
        await service.SyncAsync();
        service.Dispose();

        Assert.DoesNotContain(_extras.PathOfGame!, _extras.HighPerformance);
        Assert.True(stored.Current!.BoostGpuLeftoverChecked);
        Assert.DoesNotContain("boostEnabled", File.ReadAllText(paths.ConfigFile), StringComparison.OrdinalIgnoreCase);

        // Only once: a preference the player sets in Windows afterwards is theirs.
        _extras.HighPerformance.Add(_extras.PathOfGame!);
        var nextStart = new SettingsService(paths, _store, NullLogger<SettingsService>.Instance);
        var next = Create(nextStart);
        await next.AdoptOlderReleasePreferenceAsync();
        await next.SyncAsync();
        next.Dispose();

        Assert.Contains(_extras.PathOfGame!, _extras.HighPerformance);
    }

    [Fact]
    public async Task AnOlderReleaseClosedWithBoostOff_LeavesTheHighPerformanceThePlayerSetInWindows()
    {
        // 0.8.x removed its own preference when Boost went off, and never touched the player's.
        var paths = new AppPaths(_folder);
        paths.EnsureCreated();
        File.WriteAllText(paths.ConfigFile,
            "{\"boostEnabled\":false,\"boostGpuHighPerformance\":true,\"boostGamePath\":"
            + System.Text.Json.JsonSerializer.Serialize(_extras.PathOfGame) + "}");
        _extras.HighPerformance.Add(_extras.PathOfGame!);
        var stored = new SettingsService(paths, _store, NullLogger<SettingsService>.Instance);
        _settings = (await stored.GetSettingsAsync()).BoostEffective();
        var service = Create(stored);

        await service.AdoptOlderReleasePreferenceAsync();
        await service.RestoreLeftoverAsync();
        await service.SyncAsync();
        service.Dispose();

        Assert.Equal("2", _extras.GetGpuPreference(_extras.PathOfGame!));
        Assert.True(stored.Current!.BoostGpuLeftoverChecked);
    }

    [Fact]
    public async Task APreferenceWrittenForAnEarlierGamePath_IsTakenAwayWhenTheGameMoves()
    {
        const string oldPath = @"C:\Old\Emulator\HD-Player.exe";
        _settings = _settings with { BoostGamePath = oldPath };
        var service = Create();
        await service.SyncAsync();
        Assert.Contains(oldPath, _extras.HighPerformance);

        // The game is seen at its new path: the preference follows it.
        _presence.ApplyState(GameRuntimeState.Running, DateTimeOffset.UtcNow);
        await service.SyncAsync();
        Assert.Contains(_extras.PathOfGame!, _extras.HighPerformance);
        Assert.DoesNotContain(oldPath, _extras.HighPerformance);

        _settings = _settings with { BoostGpuHighPerformance = false };
        await service.SyncAsync();
        service.Dispose();
        Assert.Empty(_extras.HighPerformance);
    }

    [Fact]
    public async Task ClosingOptima_TakesAwayThePreferenceBoostWrote_ButNotThePlayersOwn()
    {
        var service = Create();
        _presence.ApplyState(GameRuntimeState.Running, DateTimeOffset.UtcNow);
        await service.SyncAsync();
        const string playersOwn = @"C:\Games\other.exe";
        _extras.HighPerformance.Add(playersOwn);

        service.Dispose();

        Assert.DoesNotContain(_extras.PathOfGame!, _extras.HighPerformance);
        Assert.Contains(playersOwn, _extras.HighPerformance);
    }

    [Fact]
    public async Task APowerSavingChoiceThePlayerMadeInWindows_ComesBackWhenBoostGoesOff()
    {
        _extras.Other[_extras.PathOfGame!] = "1";
        var service = Create();

        _presence.ApplyState(GameRuntimeState.Running, DateTimeOffset.UtcNow);
        await service.SyncAsync();
        Assert.Contains(_extras.PathOfGame!, _extras.HighPerformance);

        _settings = _settings with { BoostGpuHighPerformance = false };
        await service.SyncAsync();
        Assert.Equal("1", _extras.GetGpuPreference(_extras.PathOfGame!));

        // And the same when Optima closes with Boost still on.
        _settings = _settings with { BoostGpuHighPerformance = true };
        await service.SyncAsync();
        Assert.Contains(_extras.PathOfGame!, _extras.HighPerformance);
        service.Dispose();
        Assert.Equal("1", _extras.GetGpuPreference(_extras.PathOfGame!));
    }

    [Fact]
    public async Task ClosingOptimaWhileASyncWritesThePreference_StillPutsBackThePlayersChoice()
    {
        _extras.Other[_extras.PathOfGame!] = "1";
        var service = Create();
        _presence.ApplyState(GameRuntimeState.Running, DateTimeOffset.UtcNow);
        using var writing = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        _extras.BeforeSet = preference =>
        {
            if (preference == "2")
            {
                writing.Set();
                release.Wait(TimeSpan.FromSeconds(10));
            }
        };

        // The sync has saved the ledger and is about to write high performance when Optima closes.
        var sync = Task.Run(() => service.SyncAsync());
        Assert.True(writing.Wait(TimeSpan.FromSeconds(10)));
        var exit = Task.Run(service.Dispose);
        await Task.WhenAny(exit, Task.Delay(300));
        release.Set();
        await sync;
        await exit;

        Assert.Equal("1", _extras.GetGpuPreference(_extras.PathOfGame!));
        Assert.False(File.Exists(Path.Combine(_folder, "boost-gpu.json")));
    }

    [Fact]
    public async Task AChoiceThePlayerMadeInWindowsWhileBoostWasOn_IsLeftAsTheyMadeIt()
    {
        var service = Create();
        _presence.ApplyState(GameRuntimeState.Running, DateTimeOffset.UtcNow);
        await service.SyncAsync();

        _extras.SetGpuPreference(_extras.PathOfGame!, "0");
        service.Dispose();

        Assert.Equal("0", _extras.GetGpuPreference(_extras.PathOfGame!));
    }

    [Theory]
    [InlineData(null, "2", "GpuPreference=2;")]
    [InlineData("GpuPreference=1;", "2", "GpuPreference=2;")]
    [InlineData("SwapEffectUpgradeEnable=1;GpuPreference=1;", "2", "SwapEffectUpgradeEnable=1;GpuPreference=2;")]
    [InlineData("SwapEffectUpgradeEnable=1;GpuPreference=2;", null, "SwapEffectUpgradeEnable=1;")]
    [InlineData("SwapEffectUpgradeEnable=1;GpuPreference=2;", "1", "SwapEffectUpgradeEnable=1;GpuPreference=1;")]
    [InlineData("GpuPreference=2;", null, "")]
    public void OnlyTheAdapterEntryOfTheWindowsValueIsTouched(string? existing, string? preference, string expected)
        => Assert.Equal(expected, WindowsGameExtras.WithGpuPreference(existing, preference));

    [Theory]
    [InlineData(null, null)]
    [InlineData("SwapEffectUpgradeEnable=1;", null)]
    [InlineData("GpuPreference=1;", "1")]
    [InlineData("SwapEffectUpgradeEnable=1;GpuPreference=2;", "2")]
    public void TheAdapterChoiceIsReadFromTheWindowsValue(string? data, string? expected)
        => Assert.Equal(expected, WindowsGameExtras.GpuPreferenceOf(data));

    [Fact]
    public void TheRealGraphicsPreferenceRoundTrips_AndLeavesNothingBehind()
    {
        var extras = new WindowsGameExtras(NullLogger<WindowsGameExtras>.Instance);
        var path = @"C:\optima-test\" + Guid.NewGuid().ToString("N") + ".exe";

        Assert.Null(extras.GetGpuPreference(path));
        extras.SetGpuPreference(path, "2");
        try
        {
            Assert.Equal("2", extras.GetGpuPreference(path));
        }
        finally
        {
            extras.SetGpuPreference(path, null);
        }
        Assert.Null(extras.GetGpuPreference(path));
    }
}
