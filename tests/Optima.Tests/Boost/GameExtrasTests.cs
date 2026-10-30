using Microsoft.Extensions.Logging.Abstractions;
using Optima.Core.Abstractions;
using Optima.Core.Boost;
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

        public bool RefuseCoreParking { get; set; }

        public CoreParkingSnapshot? KeepCoresUnparked()
        {
            if (RefuseCoreParking)
            {
                throw new System.ComponentModel.Win32Exception(5);
            }
            if (PlanAlreadyAwake)
            {
                return null;
            }
            Raised++;
            return new CoreParkingSnapshot(Guid.Parse("381b4222-f694-41f0-9685-ff5bb260df2e"), 50, 10);
        }

        public void RestoreCoreParking(CoreParkingSnapshot snapshot) => Restored.Add(snapshot);

        public bool PlanHasPowerFloor { get; set; }
        public bool RefusePowerFloor { get; set; }
        public int PowerFloorsRaised { get; private set; }
        public List<PowerFloorSnapshot> PowerFloorsRestored { get; } = [];

        public PowerFloorSnapshot? RaisePowerFloor()
        {
            if (RefusePowerFloor)
            {
                throw new System.ComponentModel.Win32Exception(5);
            }
            if (PlanHasPowerFloor)
            {
                return null;
            }
            PowerFloorsRaised++;
            return new PowerFloorSnapshot(Guid.Parse("381b4222-f694-41f0-9685-ff5bb260df2e"), 5, 1, null);
        }

        public void RestorePowerFloor(PowerFloorSnapshot snapshot) => PowerFloorsRestored.Add(snapshot);
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

        /// <summary>Eight alike-sized cores with two threads each and distinct ranks, CPU set ids from 256.</summary>
        public IReadOnlyList<LogicalCpu> Processors { get; set; } = [.. Enumerable.Range(0, 16)
            .Select(i => new LogicalCpu((uint)(256 + i), 0, (byte)i, (byte)(i / 2 * 2), 0, 0, (byte)(i / 2), false, 16 << 20))];
        public bool RefusePlacement { get; set; }
        public Dictionary<int, IReadOnlyList<uint>?> Placed { get; } = [];
        public int PlaceCalls { get; private set; }

        public IReadOnlyList<LogicalCpu> ReadProcessors() => Processors;

        /// <summary>What Windows reports as a process's CPU sets; by default what was placed.</summary>
        public Dictionary<int, IReadOnlyList<uint>> Reported { get; } = [];
        public IReadOnlyList<uint>? ReadPlacement(int processId)
            => Reported.TryGetValue(processId, out var r) ? r : Placed.GetValueOrDefault(processId) ?? [];

        /// <summary>The emulator's threads with CPU sets of their own, as Windows has them now.</summary>
        public List<ThreadCpuSets> Threads { get; } = [];
        public int ThreadsPinned { get; set; }
        public IReadOnlyList<ThreadCpuSets> ReadThreadPlacement(int processId) => [.. Threads];

        public bool PlaceThread(int processId, ThreadCpuSets thread, IReadOnlyList<uint> cpuSetIds)
        {
            var at = Threads.FindIndex(t => t.ThreadId == thread.ThreadId && t.CreatedAt == thread.CreatedAt);
            if (at < 0 || RefusePlacement)
            {
                return false;
            }
            Threads[at] = Threads[at] with { CpuSetIds = cpuSetIds };
            return true;
        }

        public int CountPinnedThreads(int processId, IReadOnlyList<int> logicalProcessors) => ThreadsPinned;

        /// <summary>Processes Windows refuses to place, as it does a protected one.</summary>
        public HashSet<int> Refused { get; } = [];

        public List<RunningProcess> Background { get; } = [];
        public IReadOnlyList<RunningProcess> FindProcesses(IReadOnlyList<string> names) => Background;

        /// <summary>Every process runs, started at the epoch, unless a test says otherwise.</summary>
        public Func<int, DateTimeOffset?> StartTimes { get; set; } = _ => DateTimeOffset.UnixEpoch;
        public DateTimeOffset? StartTimeOf(int processId) => StartTimes(processId);

        /// <summary>GPU priority classes by process; a process not in it has not used the graphics card.</summary>
        public Dictionary<int, int> GpuPriority { get; } = new() { [5] = 2 };
        public bool RefuseGpuPriorityRead { get; set; }
        public bool RefuseGpuPrioritySet { get; set; }
        public int GpuPrioritySets { get; private set; }

        public GpuPriorityResult ReadGpuPriority(int processId, out int priorityClass)
        {
            priorityClass = GpuPriority.GetValueOrDefault(processId);
            return RefuseGpuPriorityRead ? GpuPriorityResult.Refused
                : GpuPriority.ContainsKey(processId) ? GpuPriorityResult.Done : GpuPriorityResult.Unavailable;
        }

        public GpuPriorityResult SetGpuPriority(int processId, int priorityClass)
        {
            GpuPrioritySets++;
            if (RefuseGpuPrioritySet)
            {
                return GpuPriorityResult.Refused;
            }
            if (!GpuPriority.ContainsKey(processId))
            {
                return GpuPriorityResult.Unavailable;
            }
            GpuPriority[processId] = priorityClass;
            return GpuPriorityResult.Done;
        }

        public bool PlaceProcess(int processId, IReadOnlyList<uint>? cpuSetIds)
        {
            PlaceCalls++;
            if (RefusePlacement || Refused.Contains(processId))
            {
                return false;
            }
            Placed[processId] = cpuSetIds;
            return true;
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
    public async Task TheEmulatorIsPlacedOnlyWhileTheGameIsOnScreen_AndReleased()
    {
        _settings = _settings with { BoostCorePlacement = true };
        using var service = Create();
        await service.SyncAsync();
        Assert.Empty(_extras.Placed);

        _presence.ApplyState(GameRuntimeState.Running, DateTimeOffset.UtcNow);
        await service.SyncAsync();
        await service.SyncAsync();
        Assert.Equal(1, _extras.PlaceCalls);
        Assert.Equal(12, _extras.Placed[5]!.Count);
        Assert.True(service.Status.PlacementApplied);
        Assert.Equal(6, service.Status.Placement!.Cores);

        _presence.ApplyState(GameRuntimeState.NotRunning, DateTimeOffset.UtcNow);
        await service.SyncAsync();
        Assert.Null(_extras.Placed[5]);
        Assert.False(service.Status.PlacementApplied);
    }

    [Fact]
    public async Task ANewEmulatorProcessMidMatch_IsPlacedToo()
    {
        _settings = _settings with { BoostCorePlacement = true };
        _presence.ApplyState(GameRuntimeState.Running, DateTimeOffset.UtcNow);
        using var service = Create();
        await service.SyncAsync();

        _monitor.Tracked = [.. _monitor.Tracked, new TrackedProcess { ProcessId = 9, Name = "crosvm", Kind = TrackedProcessKind.Emulator }];
        await service.SyncAsync();

        Assert.NotNull(_extras.Placed[9]);
        Assert.Equal(2, _extras.PlaceCalls);
    }

    [Fact]
    public async Task ProcessorsWithNothingToChoose_AreLeftAlone()
    {
        _settings = _settings with { BoostCorePlacement = true };
        _extras.Processors = [.. Enumerable.Range(0, 8).Select(i => new LogicalCpu((uint)(256 + i), 0, (byte)i, (byte)(i / 2), 0, 0, 0, false, 0))];
        _presence.ApplyState(GameRuntimeState.Running, DateTimeOffset.UtcNow);
        using var service = Create();

        await service.SyncAsync();

        Assert.Equal(0, _extras.PlaceCalls);
        Assert.False(service.Status.PlacementApplied);
        Assert.False(service.Status.Placement!.Narrows);
    }

    [Fact]
    public async Task ARefusedPlacement_IsNotReportedAsApplied()
    {
        _settings = _settings with { BoostCorePlacement = true };
        _extras.RefusePlacement = true;
        _presence.ApplyState(GameRuntimeState.Running, DateTimeOffset.UtcNow);
        using var service = Create();

        await service.SyncAsync();

        Assert.False(service.Status.PlacementApplied);
    }

    [Fact]
    public async Task PlacementIsTakenOffWhenOptimaCloses()
    {
        _settings = _settings with { BoostCorePlacement = true };
        _presence.ApplyState(GameRuntimeState.Running, DateTimeOffset.UtcNow);
        var service = Create();
        await service.SyncAsync();

        service.Dispose();

        Assert.Null(_extras.Placed[5]);
    }

    private string PlacementLedgerFile => Path.Combine(_folder, "boost-placement.json");

    [Fact]
    public async Task APlacementLeftByACrashedRun_IsTakenOffTheEmulatorAtTheNextStart()
    {
        _settings = _settings with { BoostCorePlacement = true };
        _presence.ApplyState(GameRuntimeState.Running, DateTimeOffset.UtcNow);
        var crashed = Create();
        await crashed.SyncAsync();
        Assert.True(File.Exists(PlacementLedgerFile));

        // No Dispose: the run died. The emulator is still running, the game is not.
        _presence.ApplyState(GameRuntimeState.NotRunning, DateTimeOffset.UtcNow);
        using var next = Create();
        await next.RestoreLeftoverAsync();

        Assert.Null(_extras.Placed[5]);
        Assert.False(File.Exists(PlacementLedgerFile));
    }

    [Fact]
    public async Task AProcessThatTookAPlacedEmulatorsId_IsLeftAlone()
    {
        var started = DateTimeOffset.UtcNow.AddMinutes(-5);
        _monitor.Tracked = [new TrackedProcess { ProcessId = 5, Name = "crosvm", Kind = TrackedProcessKind.Emulator, StartedAt = started }];
        _settings = _settings with { BoostCorePlacement = true };
        _presence.ApplyState(GameRuntimeState.Running, DateTimeOffset.UtcNow);
        var crashed = Create();
        await crashed.SyncAsync();

        _extras.StartTimes = _ => started.AddMinutes(4);
        using var next = Create();
        await next.RestoreLeftoverAsync();

        Assert.NotNull(_extras.Placed[5]);
        Assert.False(File.Exists(PlacementLedgerFile));
    }

    [Fact]
    public async Task PlacementIsReleasedAndItsLedgerGoes_WhenTheGameLeaves()
    {
        _settings = _settings with { BoostCorePlacement = true };
        _presence.ApplyState(GameRuntimeState.Running, DateTimeOffset.UtcNow);
        using var service = Create();
        await service.SyncAsync();

        _presence.ApplyState(GameRuntimeState.NotRunning, DateTimeOffset.UtcNow);
        await service.SyncAsync();

        Assert.False(File.Exists(PlacementLedgerFile));
    }

    [Fact]
    public async Task ALayoutThatCannotBePlaced_SaysSoInGame()
    {
        _settings = _settings with { BoostCorePlacement = true };
        _extras.Processors = [.. _extras.Processors.Select((c, i) => c with { Group = (ushort)(i % 2) })];
        _presence.ApplyState(GameRuntimeState.Running, DateTimeOffset.UtcNow);
        using var service = Create();

        await service.SyncAsync();

        Assert.True(service.Status.PlacementUnsupported);
        Assert.Equal(0, _extras.PlaceCalls);
    }

    [Fact]
    public async Task APlacementSomethingElseChanged_IsPutBack()
    {
        _settings = _settings with { BoostCorePlacement = true };
        _presence.ApplyState(GameRuntimeState.Running, DateTimeOffset.UtcNow);
        using var service = Create();
        await service.SyncAsync();

        _extras.Reported[5] = [256, 257];
        await service.SyncAsync();

        Assert.Equal(1, service.Status.PlacementCorrections);
        Assert.Equal(2, _extras.PlaceCalls);
        _extras.Reported.Remove(5);
        await service.SyncAsync();
        Assert.Equal(1, service.Status.PlacementCorrections);
    }

    [Fact]
    public async Task ThreadsWithCpuSetsOfTheirOwn_AreCountedAsPutBack_AndPinnedOnesReported()
    {
        _settings = _settings with { BoostCorePlacement = true };
        _extras.Threads.AddRange([new(1, 10, [256u]), new(2, 20, [257u]), new(3, 30, [258u])]);
        _extras.ThreadsPinned = 2;
        _presence.ApplyState(GameRuntimeState.Running, DateTimeOffset.UtcNow);
        using var service = Create();

        await service.SyncAsync();
        await service.SyncAsync();

        Assert.Equal(3, service.Status.PlacementCorrections);
        Assert.Equal(2, service.Status.PlacementPinnedThreads);
        Assert.All(_extras.Threads, t => Assert.Equal(12, t.CpuSetIds.Count));
    }

    [Fact]
    public async Task ThreadSetsBoostReplaced_ComeBackWhenTheGameLeaves()
    {
        _settings = _settings with { BoostCorePlacement = true };
        _extras.Threads.AddRange([new(1, 10, [256u, 257u]), new(2, 20, [300u])]);
        _presence.ApplyState(GameRuntimeState.Running, DateTimeOffset.UtcNow);
        using var service = Create();
        await service.SyncAsync();
        // Something else changes one again mid-match; it keeps what it had before Boost.
        _extras.Threads[1] = _extras.Threads[1] with { CpuSetIds = [301u] };
        await service.SyncAsync();

        _presence.ApplyState(GameRuntimeState.NotRunning, DateTimeOffset.UtcNow);
        await service.SyncAsync();

        Assert.Equal([256u, 257u], _extras.Threads[0].CpuSetIds);
        Assert.Equal([300u], _extras.Threads[1].CpuSetIds);
    }

    [Fact]
    public async Task ThreadSetsBoostReplaced_ComeBackAfterACrash()
    {
        _settings = _settings with { BoostCorePlacement = true };
        _extras.Threads.Add(new(1, 10, [256u, 257u]));
        _presence.ApplyState(GameRuntimeState.Running, DateTimeOffset.UtcNow);
        var crashed = Create();
        await crashed.SyncAsync();

        _presence.ApplyState(GameRuntimeState.NotRunning, DateTimeOffset.UtcNow);
        using var next = Create();
        await next.RestoreLeftoverAsync();

        Assert.Equal([256u, 257u], _extras.Threads[0].CpuSetIds);
        Assert.Null(_extras.Placed[5]);
    }

    [Fact]
    public async Task CpuSetsAProcessHadBefore_ArePutBack()
    {
        _settings = _settings with { BoostCorePlacement = true };
        _extras.Placed[5] = [300u, 301u];
        _presence.ApplyState(GameRuntimeState.Running, DateTimeOffset.UtcNow);
        using var service = Create();
        await service.SyncAsync();
        Assert.Equal(12, _extras.Placed[5]!.Count);

        _presence.ApplyState(GameRuntimeState.NotRunning, DateTimeOffset.UtcNow);
        await service.SyncAsync();

        Assert.Equal([300u, 301u], _extras.Placed[5]);
    }

    [Fact]
    public async Task CpuSetsSomeoneElseChangedSince_AreLeftAsTheyAre()
    {
        _settings = _settings with { BoostCorePlacement = true };
        _presence.ApplyState(GameRuntimeState.Running, DateTimeOffset.UtcNow);
        using var service = Create();
        await service.SyncAsync();
        var calls = _extras.PlaceCalls;

        _extras.Reported[5] = [300u];
        _presence.ApplyState(GameRuntimeState.NotRunning, DateTimeOffset.UtcNow);
        await service.SyncAsync();

        Assert.Equal(calls, _extras.PlaceCalls);
        Assert.False(File.Exists(PlacementLedgerFile));
    }

    [Fact]
    public async Task AReleaseThatFails_KeepsItsLedger_AndIsTriedAgain()
    {
        _settings = _settings with { BoostCorePlacement = true };
        _presence.ApplyState(GameRuntimeState.Running, DateTimeOffset.UtcNow);
        using var service = Create();
        await service.SyncAsync();

        _extras.RefusePlacement = true;
        _presence.ApplyState(GameRuntimeState.NotRunning, DateTimeOffset.UtcNow);
        await service.SyncAsync();
        Assert.NotNull(_extras.Placed[5]);
        Assert.True(File.Exists(PlacementLedgerFile));
        Assert.False(service.Status.PlacementApplied);

        _extras.RefusePlacement = false;
        await service.SyncAsync();
        Assert.Null(_extras.Placed[5]);
        Assert.False(File.Exists(PlacementLedgerFile));
    }

    [Fact]
    public async Task AProcessThatCouldNotBePlaced_IsNotTouchedByACrashRestore()
    {
        _settings = _settings with { BoostCorePlacement = true, BoostDemoteBackgroundEnabled = true, BoostDemoteProcessNames = ["chrome"] };
        _extras.Background.Add(new RunningProcess(40, DateTimeOffset.UnixEpoch));
        _extras.Refused.Add(40);
        _presence.ApplyState(GameRuntimeState.Running, DateTimeOffset.UtcNow);
        var crashed = Create();
        await crashed.SyncAsync();

        // Its own sets, from elsewhere.
        _extras.Refused.Clear();
        _extras.Placed[40] = [7u];
        _presence.ApplyState(GameRuntimeState.NotRunning, DateTimeOffset.UtcNow);
        using var next = Create();
        await next.RestoreLeftoverAsync();

        Assert.Equal([7u], _extras.Placed[40]);
        Assert.Null(_extras.Placed[5]);
    }

    [Fact]
    public async Task AProgramTakenOffTheListMidMatch_GetsItsCoresBack()
    {
        _settings = _settings with { BoostCorePlacement = true, BoostDemoteBackgroundEnabled = true, BoostDemoteProcessNames = ["chrome"] };
        _extras.Background.Add(new RunningProcess(40, DateTimeOffset.UnixEpoch));
        _presence.ApplyState(GameRuntimeState.Running, DateTimeOffset.UtcNow);
        using var service = Create();
        await service.SyncAsync();
        Assert.NotNull(_extras.Placed[40]);

        _settings = _settings with { BoostDemoteProcessNames = [] };
        await service.SyncAsync();

        Assert.Null(_extras.Placed[40]);
        Assert.Equal(0, service.Status.ProgramsOnSpareCores);
        Assert.True(service.Status.PlacementApplied);
    }

    [Fact]
    public async Task HeldBackPrograms_AreLeftAlone_WhileTheirCardIsOff()
    {
        _settings = _settings with { BoostCorePlacement = true, BoostDemoteProcessNames = ["chrome"] };
        _extras.Background.Add(new RunningProcess(40, DateTimeOffset.UnixEpoch));
        _presence.ApplyState(GameRuntimeState.Running, DateTimeOffset.UtcNow);
        using var service = Create();

        await service.SyncAsync();

        Assert.False(_extras.Placed.ContainsKey(40));
        Assert.NotNull(_extras.Placed[5]);
    }

    [Theory]
    [InlineData(0b0001UL, 0b0110UL, true)]
    [InlineData(0b0010UL, 0b0110UL, false)]
    [InlineData(0b1111UL, 0b0110UL, false)]
    [InlineData(0b0001UL, 0UL, false)]
    public void AThreadCountsAsPinnedOff_OnlyWhenItsAffinityHasNoneOfTheChosenCores(ulong affinity, ulong plan, bool pinned)
        => Assert.Equal(pinned, Optima.Platform.Windows.NativeMethods.CpuSetNative.IsPinnedOff(affinity, plan));

    [Fact]
    public void ThisProcesssThreads_ReadAsUnpinnedWithNoCpuSetsOfTheirOwn()
    {
        var extras = new WindowsGameExtras(NullLogger<WindowsGameExtras>.Instance);
        var pid = Environment.ProcessId;

        Assert.Empty(extras.ReadThreadPlacement(pid));
        Assert.Equal(0, extras.CountPinnedThreads(pid, [.. Enumerable.Range(0, Math.Min(64, Environment.ProcessorCount))]));
    }

    [Fact]
    public async Task HeldBackPrograms_GoToTheCoresTheGameDoesNotUse_AndComeBack()
    {
        _settings = _settings with { BoostCorePlacement = true, BoostDemoteBackgroundEnabled = true, BoostDemoteProcessNames = ["Discord"] };
        _extras.Background.Add(new RunningProcess(40, DateTimeOffset.UnixEpoch));
        _presence.ApplyState(GameRuntimeState.Running, DateTimeOffset.UtcNow);
        using var service = Create();

        await service.SyncAsync();

        var game = _extras.Placed[5]!;
        var spare = _extras.Placed[40]!;
        Assert.Empty(game.Intersect(spare));
        Assert.Equal(16, game.Count + spare.Count);
        Assert.Equal(1, service.Status.ProgramsOnSpareCores);
        Assert.True(service.Status.PlacementApplied);

        _presence.ApplyState(GameRuntimeState.NotRunning, DateTimeOffset.UtcNow);
        await service.SyncAsync();
        Assert.Null(_extras.Placed[40]);
    }

    private string GpuPriorityLedgerFile => Path.Combine(_folder, "boost-gpu-priority.json");

    [Fact]
    public async Task TheEmulatorsGpuPriorityIsHighOnlyWhileTheGameIsOnScreen_AndPutBack()
    {
        _settings = _settings with { BoostGpuPriorityHigh = true };
        using var service = Create();
        await service.SyncAsync();
        Assert.Equal(2, _extras.GpuPriority[5]);

        _presence.ApplyState(GameRuntimeState.Running, DateTimeOffset.UtcNow);
        await service.SyncAsync();
        await service.SyncAsync();
        Assert.Equal(4, _extras.GpuPriority[5]);
        Assert.Equal(1, _extras.GpuPrioritySets);
        Assert.Equal(1, service.Status.GpuPriorityRaised);
        Assert.True(File.Exists(GpuPriorityLedgerFile));

        _presence.ApplyState(GameRuntimeState.NotRunning, DateTimeOffset.UtcNow);
        await service.SyncAsync();
        Assert.Equal(2, _extras.GpuPriority[5]);
        Assert.Equal(0, service.Status.GpuPriorityRaised);
        Assert.False(File.Exists(GpuPriorityLedgerFile));
    }

    [Fact]
    public async Task TheGpuPriorityTheEmulatorHadBefore_ComesBackWhenOptimaCloses()
    {
        _extras.GpuPriority[5] = 1;
        _settings = _settings with { BoostGpuPriorityHigh = true };
        _presence.ApplyState(GameRuntimeState.Running, DateTimeOffset.UtcNow);
        var service = Create();
        await service.SyncAsync();

        service.Dispose();

        Assert.Equal(1, _extras.GpuPriority[5]);
        Assert.False(File.Exists(GpuPriorityLedgerFile));
    }

    [Fact]
    public async Task TheGpuPriorityComesBack_WhenTheSwitchGoesOffMidMatch()
    {
        _settings = _settings with { BoostGpuPriorityHigh = true };
        _presence.ApplyState(GameRuntimeState.Running, DateTimeOffset.UtcNow);
        using var service = Create();
        await service.SyncAsync();

        _settings = _settings with { BoostGpuPriorityHigh = false };
        await service.SyncAsync();

        Assert.Equal(2, _extras.GpuPriority[5]);
        Assert.False(service.Status.GpuPriorityEnabled);
    }

    [Fact]
    public async Task AGpuPriorityLeftByACrashedRun_IsPutBackAtTheNextStart()
    {
        _settings = _settings with { BoostGpuPriorityHigh = true };
        _presence.ApplyState(GameRuntimeState.Running, DateTimeOffset.UtcNow);
        var crashed = Create();
        await crashed.SyncAsync();

        // No Dispose: the run died. The emulator is still running.
        _presence.ApplyState(GameRuntimeState.NotRunning, DateTimeOffset.UtcNow);
        using var next = Create();
        await next.RestoreLeftoverAsync();

        Assert.Equal(2, _extras.GpuPriority[5]);
        Assert.False(File.Exists(GpuPriorityLedgerFile));
    }

    [Fact]
    public async Task AProcessThatTookARaisedEmulatorsId_KeepsItsGpuPriority()
    {
        var started = DateTimeOffset.UtcNow.AddMinutes(-5);
        _monitor.Tracked = [new TrackedProcess { ProcessId = 5, Name = "crosvm", Kind = TrackedProcessKind.Emulator, StartedAt = started }];
        _settings = _settings with { BoostGpuPriorityHigh = true };
        _presence.ApplyState(GameRuntimeState.Running, DateTimeOffset.UtcNow);
        var crashed = Create();
        await crashed.SyncAsync();

        _extras.StartTimes = _ => started.AddMinutes(4);
        using var next = Create();
        await next.RestoreLeftoverAsync();

        Assert.Equal(4, _extras.GpuPriority[5]);
        Assert.False(File.Exists(GpuPriorityLedgerFile));
    }

    [Theory]
    [InlineData(4)]
    [InlineData(5)]
    public async Task AnEmulatorAlreadyAtHighGpuPriorityOrAbove_IsLeftAlone(int already)
    {
        _extras.GpuPriority[5] = already;
        _settings = _settings with { BoostGpuPriorityHigh = true };
        _presence.ApplyState(GameRuntimeState.Running, DateTimeOffset.UtcNow);
        using var service = Create();
        await service.SyncAsync();
        Assert.True(service.Status.GpuPriorityAlreadyHigh);

        _presence.ApplyState(GameRuntimeState.NotRunning, DateTimeOffset.UtcNow);
        await service.SyncAsync();

        Assert.Equal(0, _extras.GpuPrioritySets);
        Assert.Equal(already, _extras.GpuPriority[5]);
    }

    [Fact]
    public async Task AGpuPriorityReadWindowsRefuses_IsReported_AndNothingIsChanged()
    {
        _extras.RefuseGpuPriorityRead = true;
        _settings = _settings with { BoostGpuPriorityHigh = true };
        _presence.ApplyState(GameRuntimeState.Running, DateTimeOffset.UtcNow);
        using var service = Create();

        await service.SyncAsync();

        Assert.True(service.Status.GpuPriorityRefused);
        Assert.Equal(0, _extras.GpuPrioritySets);
        Assert.False(File.Exists(GpuPriorityLedgerFile));
    }

    [Fact]
    public async Task AGpuPriorityChangeWindowsRefuses_IsReported_AndNotAskedAgainThisMatch()
    {
        _extras.RefuseGpuPrioritySet = true;
        _settings = _settings with { BoostGpuPriorityHigh = true };
        _presence.ApplyState(GameRuntimeState.Running, DateTimeOffset.UtcNow);
        using var service = Create();

        await service.SyncAsync();
        await service.SyncAsync();

        Assert.True(service.Status.GpuPriorityRefused);
        Assert.Equal(0, service.Status.GpuPriorityRaised);
        Assert.Equal(1, _extras.GpuPrioritySets);
        Assert.Equal(2, _extras.GpuPriority[5]);
        Assert.False(File.Exists(GpuPriorityLedgerFile));

        _presence.ApplyState(GameRuntimeState.NotRunning, DateTimeOffset.UtcNow);
        await service.SyncAsync();
        Assert.False(service.Status.GpuPriorityRefused);
    }

    [Fact]
    public async Task AGpuPrioritySomethingLowered_IsRaisedAgain_AndOneChangedAfterIsLeftAtTheEnd()
    {
        _settings = _settings with { BoostGpuPriorityHigh = true };
        _presence.ApplyState(GameRuntimeState.Running, DateTimeOffset.UtcNow);
        using var service = Create();
        await service.SyncAsync();

        _extras.GpuPriority[5] = 2;
        await service.SyncAsync();
        Assert.Equal(4, _extras.GpuPriority[5]);

        // Changed by someone else after the match: that change stays.
        _settings = _settings with { BoostGpuPriorityHigh = false };
        _extras.GpuPriority[5] = 3;
        await service.SyncAsync();
        Assert.Equal(3, _extras.GpuPriority[5]);
        Assert.False(File.Exists(GpuPriorityLedgerFile));
    }

    [Fact]
    public async Task AGpuPrioritySomethingRaisedAboveHighMidMatch_IsKept_AndStaysAtTheEnd()
    {
        _settings = _settings with { BoostGpuPriorityHigh = true };
        _presence.ApplyState(GameRuntimeState.Running, DateTimeOffset.UtcNow);
        using var service = Create();
        await service.SyncAsync();

        _extras.GpuPriority[5] = 5;
        await service.SyncAsync();
        Assert.Equal(5, _extras.GpuPriority[5]);
        Assert.Equal(1, _extras.GpuPrioritySets);

        _presence.ApplyState(GameRuntimeState.NotRunning, DateTimeOffset.UtcNow);
        await service.SyncAsync();
        Assert.Equal(5, _extras.GpuPriority[5]);
        Assert.False(File.Exists(GpuPriorityLedgerFile));
    }

    [Fact]
    public async Task ARaiseAgainWindowsRefuses_IsReported_AndNotAskedAgainThisMatch()
    {
        _settings = _settings with { BoostGpuPriorityHigh = true };
        _presence.ApplyState(GameRuntimeState.Running, DateTimeOffset.UtcNow);
        using var service = Create();
        await service.SyncAsync();

        _extras.GpuPriority[5] = 2;
        _extras.RefuseGpuPrioritySet = true;
        await service.SyncAsync();
        await service.SyncAsync();

        Assert.Equal(2, _extras.GpuPrioritySets);
        Assert.True(service.Status.GpuPriorityRefused);
        Assert.Equal(0, service.Status.GpuPriorityRaised);
    }

    [Fact]
    public async Task AnEmulatorThatHasNotUsedTheGraphicsCardYet_IsRaisedOnceItHas()
    {
        _extras.GpuPriority.Remove(5);
        _settings = _settings with { BoostGpuPriorityHigh = true };
        _presence.ApplyState(GameRuntimeState.Running, DateTimeOffset.UtcNow);
        using var service = Create();
        await service.SyncAsync();
        Assert.Equal(0, service.Status.GpuPriorityRaised);
        Assert.False(service.Status.GpuPriorityRefused);

        _extras.GpuPriority[5] = 2;
        await service.SyncAsync();

        Assert.Equal(4, _extras.GpuPriority[5]);
    }

    [Fact]
    public void TheGpuPrioritySwitch_IsOffWhileBoostIsOff()
    {
        Assert.False(new AppSettings { BoostGpuPriorityHigh = true }.BoostEffective().BoostGpuPriorityHigh);
        Assert.True(new AppSettings { BoostEnabled = true, BoostGpuPriorityHigh = true }.BoostEffective().BoostGpuPriorityHigh);
    }

    [Theory]
    [InlineData(0, GpuPriorityResult.Done)]
    [InlineData(unchecked((int)0xC0000022), GpuPriorityResult.Refused)]
    [InlineData(unchecked((int)0xC0000061), GpuPriorityResult.Refused)]
    [InlineData(unchecked((int)0xC000000D), GpuPriorityResult.Unavailable)]
    public void AnNtStatusFromTheGpuPriorityCalls_MeansWhatItSays(int status, GpuPriorityResult expected)
        => Assert.Equal(expected, Optima.Platform.Windows.NativeMethods.GpuPriorityNative.ResultOf(status));

    [Theory]
    [InlineData(5, GpuPriorityResult.Refused)]
    [InlineData(87, GpuPriorityResult.Unavailable)]
    public void AProcessThatCannotBeOpened_IsRefusedOnlyWhenAccessWasDenied(int error, GpuPriorityResult expected)
        => Assert.Equal(expected, Optima.Platform.Windows.NativeMethods.GpuPriorityNative.OpenFailure(error));

    [Fact]
    public void ThisProcesssOwnGpuPriority_CanBeReadWithoutAdministratorRights()
    {
        var result = new WindowsGameExtras(NullLogger<WindowsGameExtras>.Instance).ReadGpuPriority(Environment.ProcessId, out var priorityClass);

        // Done, not just "not refused": a wrong signature or access right would come back as
        // Unavailable and leave the feature waiting forever.
        Assert.Equal(GpuPriorityResult.Done, result);
        Assert.InRange(priorityClass, 0, 5);
    }

    [Fact]
    public void TheRealProcessorLayoutCanBeRead()
    {
        var cpus = new WindowsGameExtras(NullLogger<WindowsGameExtras>.Instance).ReadProcessors();

        Assert.Equal(Environment.ProcessorCount, cpus.Count);
        Assert.Equal(cpus.Count, cpus.Select(c => c.Id).Distinct().Count());
        Assert.All(cpus, c => Assert.True(c.L3Bytes > 0));
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

    private string PowerFloorLedger => Path.Combine(_folder, "boost-power-floor.json");

    [Fact]
    public async Task ThePowerFloorIsHeldOnlyWhileTheGameIsOnScreen_AndPutBack()
    {
        _settings = _settings with { BoostPowerFloor = true };
        using var service = Create();
        await service.SyncAsync();
        Assert.Equal(0, _extras.PowerFloorsRaised);

        _presence.ApplyState(GameRuntimeState.Running, DateTimeOffset.UtcNow);
        await service.SyncAsync();
        await service.SyncAsync();
        Assert.Equal(1, _extras.PowerFloorsRaised);
        Assert.True(service.Status.PowerFloorHeld);
        Assert.True(File.Exists(PowerFloorLedger));

        _presence.ApplyState(GameRuntimeState.NotRunning, DateTimeOffset.UtcNow);
        await service.SyncAsync();
        var restored = Assert.Single(_extras.PowerFloorsRestored);
        Assert.Equal(5u, restored.AcMinProcessorPercent);
        Assert.Null(restored.DcUsbSuspend);
        Assert.False(service.Status.PowerFloorHeld);
        Assert.False(File.Exists(PowerFloorLedger));
    }

    [Fact]
    public async Task ThePowerFloorSwitchedOffMidMatch_IsPutBack()
    {
        _settings = _settings with { BoostPowerFloor = true };
        _presence.ApplyState(GameRuntimeState.Running, DateTimeOffset.UtcNow);
        using var service = Create();
        await service.SyncAsync();

        _settings = _settings with { BoostPowerFloor = false };
        await service.SyncAsync();

        Assert.Single(_extras.PowerFloorsRestored);
        Assert.False(service.Status.PowerFloorEnabled);
        Assert.False(File.Exists(PowerFloorLedger));
    }

    [Fact]
    public async Task APlanThatAlreadyHasThePowerFloor_IsLeftAlone()
    {
        _settings = _settings with { BoostPowerFloor = true };
        _extras.PlanHasPowerFloor = true;
        _presence.ApplyState(GameRuntimeState.Running, DateTimeOffset.UtcNow);
        using var service = Create();

        await service.SyncAsync();

        Assert.False(service.Status.PowerFloorHeld);
        Assert.True(service.Status.PowerFloorAlreadySet);
        Assert.False(File.Exists(PowerFloorLedger));
    }

    [Fact]
    public async Task APowerFloorWindowsRefuses_DoesNotStopTheOtherExtras()
    {
        _settings = _settings with { BoostPowerFloor = true };
        _extras.RefusePowerFloor = true;
        _presence.ApplyState(GameRuntimeState.Running, DateTimeOffset.UtcNow);
        using var service = Create();

        await service.SyncAsync();

        Assert.False(service.Status.PowerFloorHeld);
        Assert.Contains(_extras.PathOfGame!, _extras.HighPerformance);
        Assert.True(service.Status.CoresHeld);
    }

    [Fact]
    public async Task CoreParkingWindowsRefuses_DoesNotStopThePowerFloor()
    {
        _settings = _settings with { BoostPowerFloor = true };
        _extras.RefuseCoreParking = true;
        _presence.ApplyState(GameRuntimeState.Running, DateTimeOffset.UtcNow);
        using var service = Create();

        await service.SyncAsync();

        Assert.False(service.Status.CoresHeld);
        Assert.True(service.Status.PowerFloorHeld);
        Assert.Contains(_extras.PathOfGame!, _extras.HighPerformance);
    }

    [Fact]
    public async Task ThePowerFloorIsPutBackWhenOptimaCloses()
    {
        _settings = _settings with { BoostPowerFloor = true };
        _presence.ApplyState(GameRuntimeState.Running, DateTimeOffset.UtcNow);
        var service = Create();
        await service.SyncAsync();

        service.Dispose();

        Assert.Single(_extras.PowerFloorsRestored);
        Assert.False(File.Exists(PowerFloorLedger));
    }

    [Fact]
    public async Task APowerFloorLedgerFromACrashedRun_IsPutBackOnTheNextStart()
    {
        var left = new PowerFloorSnapshot(Guid.NewGuid(), 5, null, 1);
        await _store.SaveAsync(PowerFloorLedger, left);
        using var service = Create();

        await service.RestoreLeftoverAsync();

        Assert.Equal(left, Assert.Single(_extras.PowerFloorsRestored));
        Assert.False(File.Exists(PowerFloorLedger));
    }

    [Theory]
    [InlineData(100u, 0u, 0u, null, null, null, true)]
    [InlineData(5u, 0u, 0u, 5u, null, null, false)]
    [InlineData(100u, 1u, 0u, null, 1u, null, false)]
    [InlineData(100u, 0u, 1u, null, null, 1u, false)]
    [InlineData(99u, 1u, 1u, 99u, 1u, 1u, false)]
    public void ThePowerFloorChangesOnlyTheValuesThePlanLacks(
        uint minAc, uint usbAc, uint usbDc, uint? wantMin, uint? wantUsbAc, uint? wantUsbDc, bool nothing)
    {
        var scheme = Guid.NewGuid();
        var snapshot = PowerFloorSnapshot.Of(scheme, minAc, usbAc, usbDc);
        if (nothing)
        {
            Assert.Null(snapshot);
            return;
        }
        Assert.Equal(new PowerFloorSnapshot(scheme, wantMin, wantUsbAc, wantUsbDc), snapshot);
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
    public async Task APreferenceThePlayerSetInWindows_IsReportedAsAlreadySet_AndOneBoostWroteIsNot()
    {
        _settings = _settings with { BoostGamePath = _extras.PathOfGame };
        _extras.HighPerformance.Add(_extras.PathOfGame!);
        using (var service = Create())
        {
            await service.SyncAsync();
            Assert.True(service.Status.GpuApplied);
            Assert.True(service.Status.GpuAlreadySet);
        }

        _extras.HighPerformance.Clear();
        using var written = Create();
        await written.SyncAsync();
        await written.SyncAsync();
        Assert.True(written.Status.GpuApplied);
        Assert.False(written.Status.GpuAlreadySet);
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
