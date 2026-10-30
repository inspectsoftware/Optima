using Optima.Core.Abstractions;
using Optima.Core.Boost;
using Optima.Core.Configuration;
using Optima.Core.Models;
using Optima.Core.Monitoring;
using Microsoft.Extensions.Logging;

namespace Optima.Core.Launch;

/// <summary>What the extras are doing right now, for the BOOST page.</summary>
public sealed record GameExtrasStatus(
    bool CoresEnabled,
    bool CoresHeld,
    bool CoresAlreadyAwake,
    bool GpuEnabled,
    bool GpuApplied,
    string? GamePath,
    bool PlacementEnabled = false,
    CorePlacementPlan? Placement = null,
    bool PlacementApplied = false,
    bool PlacementUnsupported = false,
    int PlacementCorrections = 0,
    int PlacementPinnedThreads = 0,
    int ProgramsOnSpareCores = 0,
    bool GpuPriorityEnabled = false,
    int GpuPriorityRaised = 0,
    bool GpuPriorityAlreadyHigh = false,
    bool GpuPriorityRefused = false,
    bool PowerFloorEnabled = false,
    bool PowerFloorHeld = false,
    bool PowerFloorAlreadySet = false,
    bool GpuAlreadySet = false);

/// <summary>
/// Four settings around the game that are not about its processor priority: keeping every core
/// awake for the length of a match, running the game's emulator on the best cores, asking Windows
/// to run the game on the high-performance graphics adapter, and a higher GPU priority for the
/// emulator. The core placement is a CPU set on the emulator
/// process. It ends with that process, but the emulator outlives the game (Google Play Games keeps
/// it running between games), so the placed processes go in a ledger too, and a start after a
/// crash takes the set off any of them still running.
/// The first is held only while the game is on screen and put back afterwards (also after a crash,
/// from a ledger on disk). The second is a standing Windows preference, read when the game starts,
/// so it is written once the game's executable is known and removed when the switch goes off. It
/// has a ledger of its own: Windows keeps the preference after Optima closes, and Boost starts off
/// at the next launch, so without one nothing would ever take it away again.
/// The fourth raises the emulator's GPU scheduling priority while the game is on screen, so the
/// graphics kernel runs its frames ahead of other programs' work on the graphics card. Like the
/// placement it belongs to the process and outlives the game, so it has a ledger as well.
/// The power floor (minimum processor state at 100% on mains, USB selective suspend off) is held
/// like the core-parking floor: only while the game is on screen, with a ledger of its own.
/// </summary>
public sealed class GameExtrasService : IDisposable
{
    // The adapter choice Windows calls "High performance".
    private const string HighPerformance = "2";

    private readonly IGameExtras _extras;
    private readonly IProcessMonitor _monitor;
    private readonly GamePresenceService _presence;
    private readonly Func<AppSettings?> _settings;
    private readonly Func<string, Task> _rememberGamePath;
    private readonly JsonStore _store;
    private readonly string _ledgerPath;
    private readonly string _gpuLedgerPath;
    private readonly string _placementLedgerPath;
    private readonly SettingsService? _storedSettings;
    private readonly ILogger<GameExtrasService> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private CoreParkingSnapshot? _parking;
    private bool _coresAlreadyAwake;
    private readonly string _powerFloorLedgerPath;
    private PowerFloorSnapshot? _powerFloor;
    private bool _powerFloorAlreadySet;
    private CorePlacementPlan? _plan;
    // Every process Boost placed, the emulator's and the held-back programs', by process id, with
    // what it had before and what Boost gave it (see PlacedProcess). One that could not be put
    // back stays here, and in the ledger, until it is.
    private readonly Dictionary<int, PlacedProcess> _placed = [];
    // Of those, the held-back programs sent to the spare cores.
    private readonly HashSet<int> _onSpare = [];
    private bool _gamePlaced;
    private int _corrections;
    private int _pinned;
    private readonly string _gpuPriorityLedgerPath;
    // The emulator processes whose GPU priority Boost raised, with the class each had before. One
    // that could not be put back stays here, and in the ledger, until it is.
    private readonly Dictionary<int, RaisedGpuPriority> _raised = [];
    // Processes Windows would not let Boost read or raise. They are not asked again this match: the
    // answer will not change, and every refused change is a line in the log.
    private readonly HashSet<int> _gpuRefused = [];
    private bool _gpuAlreadyHigh;
    private readonly CancellationTokenSource _stop = new();

    // D3DKMT_SCHEDULINGPRIORITYCLASS_HIGH. Never realtime, which can starve the desktop's own drawing.
    private const int GpuPriorityHigh = 4;

    /// <summary>How often the placement is checked while the game is on screen, and put right.</summary>
    public static readonly TimeSpan KeeperInterval = TimeSpan.FromSeconds(5);
    private bool _started;
    private volatile bool _disposed;

    public GameExtrasService(
        IGameExtras extras,
        IProcessMonitor monitor,
        GamePresenceService presence,
        Func<AppSettings?> settings,
        Func<string, Task> rememberGamePath,
        JsonStore store,
        string ledgerPath,
        ILogger<GameExtrasService> logger,
        SettingsService? storedSettings = null)
    {
        _extras = extras;
        _monitor = monitor;
        _presence = presence;
        _settings = settings;
        _rememberGamePath = rememberGamePath;
        _store = store;
        _ledgerPath = ledgerPath;
        // The graphics ledger sits beside the core parking one.
        _gpuLedgerPath = Path.Combine(Path.GetDirectoryName(ledgerPath) ?? string.Empty, "boost-gpu.json");
        _placementLedgerPath = Path.Combine(Path.GetDirectoryName(ledgerPath) ?? string.Empty, "boost-placement.json");
        _gpuPriorityLedgerPath = Path.Combine(Path.GetDirectoryName(ledgerPath) ?? string.Empty, "boost-gpu-priority.json");
        _powerFloorLedgerPath = Path.Combine(Path.GetDirectoryName(ledgerPath) ?? string.Empty, "boost-power-floor.json");
        _storedSettings = storedSettings;
        _logger = logger;
    }

    public GameExtrasStatus Status { get; private set; } = new(false, false, false, false, false, null);

    public event Action<GameExtrasStatus>? StatusChanged;

    public void Start()
    {
        if (_started)
        {
            return;
        }
        _started = true;
        _presence.PresenceChanged += OnPresenceChanged;
        _ = Task.Run(async () =>
        {
            await AdoptOlderReleasePreferenceAsync().ConfigureAwait(false);
            await RestoreLeftoverAsync().ConfigureAwait(false);
            await SyncAsync().ConfigureAwait(false);
        });
        _ = Task.Run(KeepPlacementAsync);
    }

    /// <summary>
    /// 0.8.0 and 0.8.1 wrote the graphics preference with no ledger and kept Boost on from one start
    /// to the next, so a player who closed one of them with Boost on still has the preference in
    /// Windows, and nothing here would ever take it away. Once, at the first start of a build with
    /// the ledger, a high-performance preference for the game they last saw, with the graphics switch
    /// still ticked, is taken to be that one and put in the ledger, and Boost starting off removes it.
    /// Only when that release closed with Boost on: with it off, 0.8.x had removed its own already,
    /// and one there is the player's, set in Windows, which 0.8.x left alone as well. One the player
    /// set with Boost on is taken for Boost's, which is how 0.8.x treated it: it removed it at switch-off.
    /// </summary>
    public async Task AdoptOlderReleasePreferenceAsync(CancellationToken ct = default)
    {
        if (_storedSettings is null)
        {
            return;
        }
        try
        {
            // The stored switch, not the effective one: Boost itself starts off.
            var stored = await _storedSettings.GetSettingsAsync(ct).ConfigureAwait(false);
            if (stored.BoostGpuLeftoverChecked)
            {
                return;
            }
            await _gate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                if (stored is { OlderReleaseClosedWithBoostOn: true, BoostGpuHighPerformance: true, BoostGamePath: { Length: > 0 } path }
                    && _store.Load<GpuPreferenceLedger>(_gpuLedgerPath) is null
                    && IsHighPerformance(path))
                {
                    await _store.SaveAsync(_gpuLedgerPath, new GpuPreferenceLedger(path), ct).ConfigureAwait(false);
                    _logger.LogInformation("The graphics preference an older Optima set for {Path} is now Boost's to remove", path);
                }
            }
            finally
            {
                _gate.Release();
            }
            await _storedSettings.UpdateSettingsAsync(
                s => s with { BoostGpuLeftoverChecked = true, OlderReleaseClosedWithBoostOn = false }, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Looked for again at the next start.
            _logger.LogWarning(ex, "Could not look for a graphics preference left by an older Optima");
        }
    }

    /// <summary>
    /// The keeper. Windows, the emulator itself or another tool can change a CPU set or a GPU
    /// priority at any time, and a new emulator process or held-back program can start mid-match;
    /// every few seconds both are read back, put right where they changed, and extended to
    /// processes that are new.
    /// </summary>
    private async Task KeepPlacementAsync()
    {
        try
        {
            while (!_stop.IsCancellationRequested)
            {
                await Task.Delay(KeeperInterval, _stop.Token).ConfigureAwait(false);
                if (_settings() is { } kept && (kept.BoostCorePlacement || kept.BoostGpuPriorityHigh)
                    && _presence.Current == GamePresence.InGame)
                {
                    await SyncAsync(_stop.Token).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private void OnPresenceChanged(PresenceChange change)
    {
        if ((change.Current == GamePresence.InGame) != (change.Previous == GamePresence.InGame))
        {
            _ = Task.Run(() => SyncAsync());
        }
    }

    /// <summary>A core-parking floor a previous run raised and never got to put back.</summary>
    public async Task RestoreLeftoverAsync(CancellationToken ct = default)
    {
        try
        {
            if (await _store.LoadAsync<CoreParkingSnapshot>(_ledgerPath, ct).ConfigureAwait(false) is { } leftover)
            {
                _extras.RestoreCoreParking(leftover);
                _store.Delete(_ledgerPath);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Could not restore the core parking setting left by an earlier run");
        }
        try
        {
            if (await _store.LoadAsync<PowerFloorSnapshot>(_powerFloorLedgerPath, ct).ConfigureAwait(false) is { } leftover)
            {
                _extras.RestorePowerFloor(leftover);
                _store.Delete(_powerFloorLedgerPath);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Could not restore the power floor left by an earlier run");
        }
        try
        {
            if (await _store.LoadAsync<PlacementLedger>(_placementLedgerPath, ct).ConfigureAwait(false) is { } placed)
            {
                await _gate.WaitAsync(ct).ConfigureAwait(false);
                try
                {
                    // One that cannot be put back now is kept, and tried again at the next sync.
                    foreach (var process in placed.Processes)
                    {
                        if (!Release(process))
                        {
                            _placed.TryAdd(process.ProcessId, process);
                        }
                    }
                    if (_placed.Count == 0)
                    {
                        _store.Delete(_placementLedgerPath);
                    }
                }
                finally
                {
                    _gate.Release();
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Could not take off the core placement left by an earlier run");
        }
        try
        {
            if (await _store.LoadAsync<GpuPriorityLedger>(_gpuPriorityLedgerPath, ct).ConfigureAwait(false) is { } raised)
            {
                await _gate.WaitAsync(ct).ConfigureAwait(false);
                try
                {
                    // One that cannot be put back now is kept, and tried again at the next sync.
                    foreach (var process in raised.Processes)
                    {
                        if (!LowerGpuPriority(process))
                        {
                            _raised.TryAdd(process.ProcessId, process);
                        }
                    }
                    if (_raised.Count == 0)
                    {
                        _store.Delete(_gpuPriorityLedgerPath);
                    }
                }
                finally
                {
                    _gate.Release();
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Could not put back the GPU priority left by an earlier run");
        }
    }

    private async Task<List<TrackedProcess>> EmulatorsAsync(CancellationToken ct)
        => [.. (await _monitor.GetTrackedProcessesAsync(ct).ConfigureAwait(false)).Where(p => p.Kind == TrackedProcessKind.Emulator)];

    /// <summary>Whether a process id is still the process that was placed, not a later one given its id.</summary>
    private bool IsStillRunning(int processId, DateTimeOffset? startedAt)
        => _extras.StartTimeOf(processId) is { } now && (startedAt is null || now == startedAt);

    public async Task SyncAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            // Optima is closing and has already put everything back. Written now, a preference
            // would have no ledger and stay in Windows for good.
            if (_disposed)
            {
                return;
            }
            var settings = _settings();
            var coresEnabled = settings?.BoostKeepCoresAwake ?? false;
            var gpuEnabled = settings?.BoostGpuHighPerformance ?? false;
            var inGame = _presence.Current == GamePresence.InGame;

            // Its own try, as the power floor's: a plan that refuses it must not stop the extras after it.
            try
            {
                if (coresEnabled && inGame)
                {
                    if (_parking is null && !_coresAlreadyAwake)
                    {
                        _parking = _extras.KeepCoresUnparked();
                        _coresAlreadyAwake = _parking is null;
                        if (_parking is not null)
                        {
                            await _store.SaveAsync(_ledgerPath, _parking, ct).ConfigureAwait(false);
                        }
                    }
                }
                else
                {
                    if (_parking is not null)
                    {
                        _extras.RestoreCoreParking(_parking);
                        _parking = null;
                        _store.Delete(_ledgerPath);
                    }
                    _coresAlreadyAwake = false;
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Tried again at the next sync; one that could not be put back stays in the ledger.
                _logger.LogWarning(ex, "Core parking could not be changed");
            }

            var powerFloorEnabled = settings?.BoostPowerFloor ?? false;
            // Its own try: a plan that refuses it must not stop the extras after it.
            try
            {
                if (powerFloorEnabled && inGame)
                {
                    if (_powerFloor is null && !_powerFloorAlreadySet)
                    {
                        _powerFloor = _extras.RaisePowerFloor();
                        _powerFloorAlreadySet = _powerFloor is null;
                        if (_powerFloor is not null)
                        {
                            await _store.SaveAsync(_powerFloorLedgerPath, _powerFloor, ct).ConfigureAwait(false);
                        }
                    }
                }
                else
                {
                    if (_powerFloor is not null)
                    {
                        _extras.RestorePowerFloor(_powerFloor);
                        _powerFloor = null;
                        _store.Delete(_powerFloorLedgerPath);
                    }
                    _powerFloorAlreadySet = false;
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Tried again at the next sync; one that could not be put back stays in the ledger.
                _logger.LogWarning(ex, "The power floor could not be changed");
            }

            var gamePath = settings?.BoostGamePath;
            var emulators = inGame ? await EmulatorsAsync(ct).ConfigureAwait(false) : [];
            // Dispose may have taken everything off while the processes were being listed.
            if (_disposed)
            {
                return;
            }
            var placementEnabled = settings?.BoostCorePlacement ?? false;
            if (placementEnabled && inGame)
            {
                // The held-back programs only with their own card on: that card is where the
                // player chose to have them touched.
                await PlaceAsync(emulators,
                    settings?.BoostDemoteBackgroundEnabled == true ? settings.BoostDemoteProcessNames : [], ct).ConfigureAwait(false);
            }
            else
            {
                ReleasePlacement();
            }

            var gpuPriorityEnabled = settings?.BoostGpuPriorityHigh ?? false;
            if (gpuPriorityEnabled && inGame)
            {
                await RaiseGpuPriorityAsync(emulators, ct).ConfigureAwait(false);
            }
            else
            {
                ReleaseGpuPriority();
            }

            if (inGame)
            {
                var emulator = emulators.FirstOrDefault();
                if (emulator is not null && _extras.GetProcessPath(emulator.ProcessId) is { Length: > 0 } found
                    && !string.Equals(found, gamePath, StringComparison.OrdinalIgnoreCase))
                {
                    gamePath = found;
                    await _rememberGamePath(found).ConfigureAwait(false);
                }
            }

            // A preference Boost wrote, in this run or an earlier one, goes as soon as the switch
            // is off. Boost starts off at every launch, so this is also what takes away the one a
            // run left behind when it closed with Boost on.
            if (!gpuEnabled)
            {
                RemoveWrittenGpuPreference();
            }

            var gpuApplied = false;
            var gpuAlreadySet = false;
            if (gamePath is { Length: > 0 })
            {
                if (gpuEnabled && !IsHighPerformance(gamePath))
                {
                    // The ledger holds one path, so the one it holds now (the game at an earlier
                    // path, before an emulator update or reinstall) is taken away before it is
                    // replaced. Otherwise nothing would ever remove that preference.
                    RemoveWrittenGpuPreference();
                    // Into the ledger first: a preference written with no ledger would be taken
                    // for the player's own and left in Windows for good. With it goes the choice
                    // it replaces (power saving, say), which comes back when Boost's goes.
                    await _store.SaveAsync(_gpuLedgerPath,
                        new GpuPreferenceLedger(gamePath, _extras.GetGpuPreference(gamePath)), ct).ConfigureAwait(false);
                    _extras.SetGpuPreference(gamePath, HighPerformance);
                }
                else if (gpuEnabled)
                {
                    // High performance that the ledger does not name is the player's own, which a
                    // session with the switch off has as well, so it is not Boost's doing.
                    gpuAlreadySet = _store.Load<GpuPreferenceLedger>(_gpuLedgerPath) is not { ExecutablePath: var written }
                        || !string.Equals(written, gamePath, StringComparison.OrdinalIgnoreCase);
                }
                // Nothing else is removed: every preference Boost wrote is in the ledger, so one
                // that is not is the player's own, set in Windows.
                gpuApplied = gpuEnabled;
            }

            Status = new GameExtrasStatus(coresEnabled, _parking is not null, _coresAlreadyAwake, gpuEnabled, gpuApplied, gamePath,
                placementEnabled, _plan, _gamePlaced, PlacementUnsupported: placementEnabled && inGame && _plan is null,
                PlacementCorrections: _corrections, PlacementPinnedThreads: _pinned, ProgramsOnSpareCores: _onSpare.Count,
                GpuPriorityEnabled: gpuPriorityEnabled, GpuPriorityRaised: gpuPriorityEnabled && inGame ? _raised.Keys.Count(pid => !_gpuRefused.Contains(pid)) : 0,
                GpuPriorityAlreadyHigh: _gpuAlreadyHigh, GpuPriorityRefused: _gpuRefused.Count > 0,
                PowerFloorEnabled: powerFloorEnabled, PowerFloorHeld: _powerFloor is not null, PowerFloorAlreadySet: _powerFloorAlreadySet,
                GpuAlreadySet: gpuAlreadySet);
            StatusChanged?.Invoke(Status);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Game extras sync failed");
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Puts the emulator's processes on the chosen cores and the listed background programs (the
    /// ones Boost holds back) on the cores left over, then reads the emulator's placement back and
    /// puts it right where something changed it. The cores are chosen once per match, since
    /// Windows' ranking of them can change between matches.
    /// </summary>
    private async Task PlaceAsync(IReadOnlyList<TrackedProcess> emulators, IReadOnlyList<string> backgroundNames, CancellationToken ct)
    {
        try
        {
            _plan ??= CorePlacement.Choose(_extras.ReadProcessors());
        }
        catch (Exception ex)
        {
            // Read again at the next sync; the other extras go on.
            _logger.LogWarning(ex, "Could not read the processor layout for core placement");
            return;
        }
        if (_plan is not { Narrows: true } plan)
        {
            return;
        }

        var game = emulators.Select(e => new RunningProcess(e.ProcessId, e.StartedAt)).ToList();
        var background = plan.SpareCpuSetIds.Count > 0 && backgroundNames.Count > 0
            ? _extras.FindProcesses(backgroundNames).Where(p => game.All(g => g.ProcessId != p.ProcessId)).ToList()
            : [];
        var running = game.Concat(background).Select(p => p.ProcessId).ToHashSet();
        // A placed process missing from this sync's lists may still be running: a program the
        // player took off the list or turned its card off for, or the emulator the monitor missed
        // this once. Its sets are put back, not forgotten; it is placed again if it comes back.
        foreach (var gone in _placed.Values.Where(p => !running.Contains(p.ProcessId)).ToList())
        {
            if (Release(gone))
            {
                _placed.Remove(gone.ProcessId);
                _onSpare.Remove(gone.ProcessId);
            }
        }

        var placedNow = new HashSet<int>();
        foreach (var (process, spare) in game.Select(p => (p, false)).Concat(background.Select(p => (p, true))))
        {
            if (_placed.ContainsKey(process.ProcessId))
            {
                continue;
            }
            var sets = spare ? plan.SpareCpuSetIds : plan.CpuSetIds;
            // With what it had before (none, for most), which is put back afterwards.
            var entry = new PlacedProcess(process.ProcessId, process.StartedAt, _extras.ReadPlacement(process.ProcessId), sets);
            // Into the ledger first, as with the graphics preference: a set placed with no ledger
            // would stay on the emulator, which outlives the game, after a crash.
            await SaveLedgerAsync([.. _placed.Values, entry], ct).ConfigureAwait(false);
            if (_extras.PlaceProcess(process.ProcessId, sets))
            {
                _placed[process.ProcessId] = entry;
                placedNow.Add(process.ProcessId);
                if (spare)
                {
                    _onSpare.Add(process.ProcessId);
                }
            }
        }

        // The emulator's placement, read back and put right.
        var pinned = 0;
        foreach (var pid in game.Select(g => g.ProcessId).Where(_placed.ContainsKey))
        {
            var entry = _placed[pid];
            if (!placedNow.Contains(pid) && _extras.ReadPlacement(pid) is { } now && !SameSets(now, plan.CpuSetIds))
            {
                // A process kept from an earlier match, whose release failed, has that match's
                // cores in the ledger; only the sets the ledger names are taken off again.
                if (entry.Placed is null || !SameSets(entry.Placed, plan.CpuSetIds))
                {
                    _placed[pid] = entry = entry with { Placed = plan.CpuSetIds };
                    await SaveLedgerAsync(_placed.Values, ct).ConfigureAwait(false);
                }
                if (_extras.PlaceProcess(pid, plan.CpuSetIds))
                {
                    _corrections++;
                    _logger.LogInformation("The game's placement on process {Pid} had changed to [{Now}] and was put back", pid, string.Join(',', now));
                }
            }
            // A thread's own CPU sets replace the process default, so those are given the game's
            // too. What each had goes into the ledger first and comes back with the process's; a
            // thread overridden again keeps what it had before Boost first replaced it.
            var threads = _extras.ReadThreadPlacement(pid).Where(t => !SameSets(t.CpuSetIds, plan.CpuSetIds)).ToList();
            if (threads.Count > 0)
            {
                var saved = entry.Threads ?? [];
                _placed[pid] = entry with
                {
                    Threads = [.. saved, .. threads.Where(t => !saved.Any(k => k.ThreadId == t.ThreadId && k.CreatedAt == t.CreatedAt))],
                };
                await SaveLedgerAsync(_placed.Values, ct).ConfigureAwait(false);
                _corrections += threads.Count(t => _extras.PlaceThread(pid, t, plan.CpuSetIds));
            }
            // A hard pin is counted, not undone.
            pinned += _extras.CountPinnedThreads(pid, plan.LogicalProcessors);
        }
        _gamePlaced = game.Any(g => _placed.ContainsKey(g.ProcessId));
        if (pinned > 0 && _pinned == 0)
        {
            _logger.LogWarning("{Count} of the game's threads are pinned to cores outside its placement; Windows follows the pin", pinned);
        }
        _pinned = pinned;
        if (_placed.Count == 0)
        {
            _store.Delete(_placementLedgerPath);
        }
    }

    private static bool SameSets(IReadOnlyList<uint> a, IReadOnlyList<uint> b) => a.Order().SequenceEqual(b.Order());

    private Task SaveLedgerAsync(IEnumerable<PlacedProcess> processes, CancellationToken ct)
        => _store.SaveAsync(_placementLedgerPath, new PlacementLedger([.. processes]), ct);

    /// <summary>
    /// Puts back what a placed process had: the CPU sets of the threads Boost gave the game's, then
    /// its own default sets. Sets changed again since Boost gave them were changed by someone else
    /// (the player, another tool), and stay. True when nothing is left to put back, also when the
    /// process is gone or is a later one given its id.
    /// </summary>
    private bool Release(PlacedProcess process)
    {
        var pid = process.ProcessId;
        if (!IsStillRunning(pid, process.StartedAt))
        {
            return true;
        }
        var released = true;
        if (process.Threads is { Count: > 0 } threads)
        {
            var now = _extras.ReadThreadPlacement(pid);
            foreach (var thread in threads)
            {
                if (now.FirstOrDefault(n => n.ThreadId == thread.ThreadId && n.CreatedAt == thread.CreatedAt) is { } current
                    && (process.Placed is null || SameSets(current.CpuSetIds, process.Placed)))
                {
                    released &= _extras.PlaceThread(pid, thread, thread.CpuSetIds);
                }
            }
        }
        if (process.Placed is not null && _extras.ReadPlacement(pid) is { } sets && !SameSets(sets, process.Placed))
        {
            return released;
        }
        return _extras.PlaceProcess(pid, process.Previous is { Count: > 0 } previous ? previous : null) && released;
    }

    /// <summary>
    /// Puts back every placed process (see <see cref="Release"/>), then the ledger. One that could
    /// not be put back stays, here and in the ledger, and is tried again at the next sync and, after
    /// a crash, at the next start.
    /// </summary>
    private void ReleasePlacement()
    {
        var had = _placed.Count > 0;
        foreach (var process in _placed.Values.ToList())
        {
            if (Release(process))
            {
                _placed.Remove(process.ProcessId);
                _onSpare.Remove(process.ProcessId);
            }
        }
        if (had && _placed.Count == 0)
        {
            _store.Delete(_placementLedgerPath);
        }
        _gamePlaced = false;
        _corrections = 0;
        _pinned = 0;
        _plan = null;
    }

    /// <summary>
    /// Raises each emulator process to high GPU priority, after reading the class it has, which is
    /// put back afterwards; then reads the raised ones back and raises again one something lowered.
    /// A process already at high or above was put there by someone else and is left alone, and so is
    /// one that has not used the graphics card yet: the graphics kernel has no priority for it, and
    /// the next keeper pass asks again.
    /// </summary>
    private async Task RaiseGpuPriorityAsync(IReadOnlyList<TrackedProcess> emulators, CancellationToken ct)
    {
        // One the monitor no longer lists is put back, as with the placement; it is raised again if it comes back.
        foreach (var gone in _raised.Values.Where(r => emulators.All(e => e.ProcessId != r.ProcessId)).ToList())
        {
            if (LowerGpuPriority(gone))
            {
                _raised.Remove(gone.ProcessId);
            }
        }
        var alreadyHigh = false;
        foreach (var emulator in emulators.Where(e => !_gpuRefused.Contains(e.ProcessId)))
        {
            var pid = emulator.ProcessId;
            var read = _extras.ReadGpuPriority(pid, out var now);
            if (read == GpuPriorityResult.Refused)
            {
                _gpuRefused.Add(pid);
                continue;
            }
            if (read != GpuPriorityResult.Done)
            {
                continue;
            }
            if (_raised.ContainsKey(pid))
            {
                if (now > GpuPriorityHigh)
                {
                    // Raised above high by someone else: that choice is theirs and stays, so this
                    // one is no longer Boost's to put back.
                    _raised.Remove(pid);
                    await SaveGpuPriorityLedgerAsync(_raised.Values, ct).ConfigureAwait(false);
                    alreadyHigh = true;
                    continue;
                }
                if (now < GpuPriorityHigh)
                {
                    var again = _extras.SetGpuPriority(pid, GpuPriorityHigh);
                    if (again == GpuPriorityResult.Done)
                    {
                        _logger.LogInformation("The GPU priority of process {Pid} had changed to class {Now} and was put back", pid, now);
                    }
                    else if (again == GpuPriorityResult.Refused)
                    {
                        // Not asked again this match, and no longer counted as raised; it stays in
                        // the ledger so the end still puts it back if it is at high again by then.
                        _gpuRefused.Add(pid);
                    }
                }
                continue;
            }
            if (now >= GpuPriorityHigh)
            {
                alreadyHigh = true;
                continue;
            }
            var entry = new RaisedGpuPriority(pid, emulator.StartedAt, now);
            // Into the ledger first: the emulator outlives the game, and a priority raised with no
            // ledger would stay on it after a crash.
            await SaveGpuPriorityLedgerAsync([.. _raised.Values, entry], ct).ConfigureAwait(false);
            var set = _extras.SetGpuPriority(pid, GpuPriorityHigh);
            if (set == GpuPriorityResult.Done)
            {
                _raised[pid] = entry;
                continue;
            }
            if (set == GpuPriorityResult.Refused)
            {
                _gpuRefused.Add(pid);
            }
            await SaveGpuPriorityLedgerAsync(_raised.Values, ct).ConfigureAwait(false);
        }
        _gpuAlreadyHigh = alreadyHigh;
        if (_raised.Count == 0)
        {
            _store.Delete(_gpuPriorityLedgerPath);
        }
    }

    private Task SaveGpuPriorityLedgerAsync(IEnumerable<RaisedGpuPriority> processes, CancellationToken ct)
        => _store.SaveAsync(_gpuPriorityLedgerPath, new GpuPriorityLedger([.. processes]), ct);

    /// <summary>
    /// Puts back the GPU priority class a raised process had. One no longer at high was changed by
    /// someone else since, and keeps that. True when nothing is left to put back, also when the
    /// process is gone or is a later one given its id.
    /// </summary>
    private bool LowerGpuPriority(RaisedGpuPriority raised)
    {
        var pid = raised.ProcessId;
        if (!IsStillRunning(pid, raised.StartedAt))
        {
            return true;
        }
        return _extras.ReadGpuPriority(pid, out var now) switch
        {
            GpuPriorityResult.Done when now == GpuPriorityHigh => _extras.SetGpuPriority(pid, raised.Previous) != GpuPriorityResult.Refused,
            GpuPriorityResult.Refused => false,
            _ => true,
        };
    }

    /// <summary>Puts back every raised process (see <see cref="LowerGpuPriority"/>), then the ledger.</summary>
    private void ReleaseGpuPriority()
    {
        var had = _raised.Count > 0;
        foreach (var process in _raised.Values.ToList())
        {
            if (LowerGpuPriority(process))
            {
                _raised.Remove(process.ProcessId);
            }
        }
        if (had && _raised.Count == 0)
        {
            _store.Delete(_gpuPriorityLedgerPath);
        }
        _gpuRefused.Clear();
        _gpuAlreadyHigh = false;
    }

    /// <summary>
    /// Takes away the graphics preference the ledger says Boost wrote, putting back the choice it
    /// replaced, then the ledger. A preference that is no longer high performance was changed in
    /// Windows since, and that change is the player's.
    /// </summary>
    private void RemoveWrittenGpuPreference()
    {
        if (_store.Load<GpuPreferenceLedger>(_gpuLedgerPath) is not { ExecutablePath: { Length: > 0 } path } ledger)
        {
            return;
        }
        if (IsHighPerformance(path))
        {
            _extras.SetGpuPreference(path, ledger.Previous);
        }
        _store.Delete(_gpuLedgerPath);
    }

    private bool IsHighPerformance(string path) => _extras.GetGpuPreference(path) == HighPerformance;

    public void Dispose()
    {
        if (_started)
        {
            _presence.PresenceChanged -= OnPresenceChanged;
        }
        // A sync caught between saving the ledger and writing the preference would otherwise have
        // the ledger taken from under it and then write a preference nothing ever removes, and the
        // choice it replaced would be lost. So this waits for that sync, briefly, and no sync
        // after it writes anything.
        _disposed = true;
        _stop.Cancel();
        var held = _gate.Wait(TimeSpan.FromSeconds(2));
        try
        {
            // Boost does not outlive Optima, and neither does what it asked of Windows.
            try
            {
                RemoveWrittenGpuPreference();
            }
            catch (Exception ex)
            {
                // Stays in the ledger; the next start takes it away.
                _logger.LogDebug(ex, "The graphics preference could not be removed on exit");
            }
            // Only with the gate: a sync still running would change the placed processes under
            // it, and could place more afterwards. All it placed is in the ledger already, so the
            // next start takes it off.
            if (held)
            {
                try
                {
                    ReleasePlacement();
                }
                catch (Exception ex)
                {
                    // Stays in the ledger; the next start takes it off.
                    _logger.LogDebug(ex, "Core placement could not be taken off on exit");
                }
                try
                {
                    ReleaseGpuPriority();
                }
                catch (Exception ex)
                {
                    // Stays in the ledger; the next start puts it back.
                    _logger.LogDebug(ex, "The GPU priority could not be put back on exit");
                }
            }
            if (_parking is not null)
            {
                try
                {
                    _extras.RestoreCoreParking(_parking);
                    _store.Delete(_ledgerPath);
                }
                catch (Exception ex)
                {
                    // Stays in the ledger; the next start puts it back.
                    _logger.LogDebug(ex, "Core parking could not be restored on exit");
                }
                _parking = null;
            }
            if (_powerFloor is not null)
            {
                try
                {
                    _extras.RestorePowerFloor(_powerFloor);
                    _store.Delete(_powerFloorLedgerPath);
                }
                catch (Exception ex)
                {
                    // Stays in the ledger; the next start puts it back.
                    _logger.LogDebug(ex, "The power floor could not be restored on exit");
                }
                _powerFloor = null;
            }
        }
        finally
        {
            if (held)
            {
                _gate.Release();
            }
        }
    }
}

/// <summary>
/// The game executable Boost gave the high-performance graphics preference to, and the adapter
/// choice Windows had for it before (null when it had none), which is put back afterwards.
/// </summary>
public sealed record GpuPreferenceLedger(string ExecutablePath, string? Previous = null);

/// <summary>The processes Boost gave a CPU set to, which a start after a crash takes it off.</summary>
public sealed record PlacementLedger(IReadOnlyList<PlacedProcess> Processes);

/// <summary>The emulator processes Boost raised the GPU priority of, which a start after a crash puts back.</summary>
public sealed record GpuPriorityLedger(IReadOnlyList<RaisedGpuPriority> Processes);

/// <summary>
/// A process Boost gave high GPU priority to: the start time that tells it from a later process
/// given its id, and the priority class it had before, which is put back afterwards.
/// </summary>
public sealed record RaisedGpuPriority(int ProcessId, DateTimeOffset? StartedAt, int Previous);

/// <summary>
/// A process Boost gave CPU sets to: the start time that tells it from a later process given its
/// id, the default CPU sets it had before (empty or null for none), the ones Boost gave it, and
/// the threads whose own CPU sets Boost replaced, each with the sets it had.
/// </summary>
public sealed record PlacedProcess(
    int ProcessId,
    DateTimeOffset? StartedAt,
    IReadOnlyList<uint>? Previous = null,
    IReadOnlyList<uint>? Placed = null,
    IReadOnlyList<ThreadCpuSets>? Threads = null);
