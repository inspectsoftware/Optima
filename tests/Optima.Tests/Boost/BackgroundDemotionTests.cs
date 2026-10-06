using System.Diagnostics;
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

public sealed class BackgroundDemotionTests : IDisposable
{
    private sealed class FakeDemoter : IBackgroundDemoter
    {
        public List<ProcessStateSnapshot> Running { get; } = [];
        public List<IReadOnlyList<string>> AskedFor { get; } = [];

        public Task<IReadOnlyList<ProcessStateSnapshot>> DemoteAsync(
            IReadOnlyList<string> processNames, IReadOnlySet<int> alreadyDemoted, CancellationToken ct = default)
        {
            AskedFor.Add(processNames);
            return Task.FromResult<IReadOnlyList<ProcessStateSnapshot>>(
                Running.Where(p => !alreadyDemoted.Contains(p.ProcessId)).ToList());
        }
    }

    private readonly string _folder = Path.Combine(Path.GetTempPath(), "optima-demotion-" + Guid.NewGuid().ToString("N"));
    private readonly FakeDemoter _demoter = new();
    private readonly FakeProcessOptimizer _optimizer = new();
    private readonly GamePresenceService _presence = new(new FakeProcessMonitor(), NullLogger<GamePresenceService>.Instance);
    private readonly JsonStore _store = new(NullLogger<JsonStore>.Instance);
    private AppSettings _settings = new() { BoostDemoteBackgroundEnabled = true, BoostDemoteProcessNames = ["chrome"] };

    private string Ledger => Path.Combine(_folder, "boost-demoted.json");

    private BackgroundDemotionService Create()
        => new(_demoter, _optimizer, _presence, () => _settings, _store, Ledger, NullLogger<BackgroundDemotionService>.Instance);

    private static ProcessStateSnapshot Chrome(int pid) => new() { ProcessId = pid, ProcessName = "chrome" };

    private void GameOnScreen() => _presence.ApplyState(GameRuntimeState.Running, DateTimeOffset.UtcNow);

    public void Dispose()
    {
        if (Directory.Exists(_folder))
        {
            Directory.Delete(_folder, recursive: true);
        }
    }

    [Fact]
    public async Task NothingIsTouchedWithoutTheGame()
    {
        _demoter.Running.Add(Chrome(1));
        using var service = Create();

        await service.SyncAsync();

        Assert.Empty(_demoter.AskedFor);
        Assert.Equal(0, service.Status.ProcessCount);
    }

    [Fact]
    public async Task ProcessesThatStartMidGameAreDemotedToo_AndEachOnlyOnce()
    {
        _demoter.Running.Add(Chrome(1));
        GameOnScreen();
        using var service = Create();
        await service.SyncAsync();

        _demoter.Running.Add(Chrome(2));
        await service.SyncAsync();

        Assert.Equal(2, service.Status.ProcessCount);
        Assert.Equal(1, service.Status.ProgramCount);
        Assert.True(File.Exists(Ledger));
    }

    [Fact]
    public async Task WhenTheGameLeaves_EverythingIsPutBack_AndTheLedgerIsGone()
    {
        _demoter.Running.AddRange([Chrome(1), Chrome(2)]);
        GameOnScreen();
        using var service = Create();
        await service.SyncAsync();

        _presence.ApplyState(GameRuntimeState.NotRunning, DateTimeOffset.UtcNow);
        // The presence service debounces an exit over several polls.
        for (var i = 0; i < 5 && _presence.Current == GamePresence.InGame; i++)
        {
            _presence.ApplyState(GameRuntimeState.NotRunning, DateTimeOffset.UtcNow);
        }
        await service.SyncAsync();

        Assert.Equal([1, 2], _optimizer.Restored.Order());
        Assert.False(File.Exists(Ledger));
    }

    [Fact]
    public async Task SwitchedOffMidGame_PutsEverythingBack()
    {
        _demoter.Running.Add(Chrome(1));
        GameOnScreen();
        using var service = Create();
        await service.SyncAsync();

        _settings = _settings with { BoostDemoteBackgroundEnabled = false };
        await service.SyncAsync();

        Assert.Equal([1], _optimizer.Restored);
    }

    [Fact]
    public async Task ALedgerLeftByACrashedRun_IsRestoredOnTheNextStart()
    {
        await _store.SaveAsync(Ledger, new List<ProcessStateSnapshot> { Chrome(7), Chrome(8) });
        using var service = Create();

        await service.RestoreLeftoversAsync();

        Assert.Equal([7, 8], _optimizer.Restored);
        Assert.False(File.Exists(Ledger));
    }

    [Fact]
    public async Task ARealProcessIsDemotedAndComesBackExactly()
    {
        // waitfor, not ping: other tests in this suite start ping children at the same time, and this
        // test must only ever demote a process it started itself.
        using var child = Process.Start(new ProcessStartInfo("waitfor.exe", "OptimaDemotionTest /T 30")
        {
            CreateNoWindow = true,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        })!;
        try
        {
            child.PriorityClass = ProcessPriorityClass.AboveNormal;
            var demoter = new WindowsBackgroundDemoter(NullLogger<WindowsBackgroundDemoter>.Instance);
            // Any other instance on the machine is declared "already demoted" so this test only touches its own.
            var others = Process.GetProcessesByName("waitfor").Select(p => p.Id).Where(id => id != child.Id).ToHashSet();

            var snapshot = Assert.Single(await demoter.DemoteAsync(["waitfor.exe"], others));

            child.Refresh();
            Assert.Equal(ProcessPriorityClass.BelowNormal, child.PriorityClass);
            Assert.Equal(ProcessPriorityLevel.AboveNormal, snapshot.OriginalPriority);

            await new WindowsProcessOptimizer(NullLogger<WindowsProcessOptimizer>.Instance).RestoreAsync(snapshot);
            child.Refresh();
            Assert.Equal(ProcessPriorityClass.AboveNormal, child.PriorityClass);
        }
        finally
        {
            child.Kill(entireProcessTree: true);
        }
    }

    [Fact]
    public async Task ProtectedNamesAreNeverDemoted_WhateverTheListSays()
    {
        var demoter = new WindowsBackgroundDemoter(NullLogger<WindowsBackgroundDemoter>.Instance);

        var demoted = await demoter.DemoteAsync(["explorer", "crosvm.exe", "Optima", "svchost"], new HashSet<int>());

        Assert.Empty(demoted);
    }
}
