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
        public bool IsGpuHighPerformance(string executablePath) => HighPerformance.Contains(executablePath);

        public void SetGpuHighPerformance(string executablePath, bool enabled)
        {
            if (enabled)
            {
                HighPerformance.Add(executablePath);
            }
            else
            {
                HighPerformance.Remove(executablePath);
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

    private GameExtrasService Create()
        => new(_extras, _monitor, _presence, () => _settings,
            path =>
            {
                _settings = _settings with { BoostGamePath = path };
                return Task.CompletedTask;
            },
            _store, Ledger, NullLogger<GameExtrasService>.Instance);

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

    [Theory]
    [InlineData(null, true, "GpuPreference=2;")]
    [InlineData("GpuPreference=1;", true, "GpuPreference=2;")]
    [InlineData("SwapEffectUpgradeEnable=1;GpuPreference=1;", true, "SwapEffectUpgradeEnable=1;GpuPreference=2;")]
    [InlineData("SwapEffectUpgradeEnable=1;GpuPreference=2;", false, "SwapEffectUpgradeEnable=1;")]
    [InlineData("GpuPreference=2;", false, "")]
    public void OnlyTheAdapterEntryOfTheWindowsValueIsTouched(string? existing, bool on, string expected)
        => Assert.Equal(expected, WindowsGameExtras.WithGpuPreference(existing, on));

    [Fact]
    public void TheRealGraphicsPreferenceRoundTrips_AndLeavesNothingBehind()
    {
        var extras = new WindowsGameExtras(NullLogger<WindowsGameExtras>.Instance);
        var path = @"C:\optima-test\" + Guid.NewGuid().ToString("N") + ".exe";

        Assert.False(extras.IsGpuHighPerformance(path));
        extras.SetGpuHighPerformance(path, true);
        try
        {
            Assert.True(extras.IsGpuHighPerformance(path));
        }
        finally
        {
            extras.SetGpuHighPerformance(path, false);
        }
        Assert.False(extras.IsGpuHighPerformance(path));
    }
}
