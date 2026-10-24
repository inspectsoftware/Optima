using Optima.Core.Abstractions;
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
    string? GamePath);

/// <summary>
/// Two settings around the game that are not about its priority: keeping every core awake for the
/// length of a match, and asking Windows to run the game on the high-performance graphics adapter.
/// The first is held only while the game is on screen and put back afterwards (also after a crash,
/// from a ledger on disk). The second is a standing Windows preference, read when the game starts,
/// so it is written once the game's executable is known and removed when the switch goes off. It
/// has a ledger of its own: Windows keeps the preference after Optima closes, and Boost starts off
/// at the next launch, so without one nothing would ever take it away again.
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
    private readonly SettingsService? _storedSettings;
    private readonly ILogger<GameExtrasService> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private CoreParkingSnapshot? _parking;
    private bool _coresAlreadyAwake;
    private bool _started;

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
    }

    /// <summary>
    /// 0.8.0 and 0.8.1 wrote the graphics preference with no ledger and kept Boost on from one start
    /// to the next, so a player who closed one of them with Boost on still has the preference in
    /// Windows, and nothing here would ever take it away. Once, at the first start of a build with
    /// the ledger, a high-performance preference for the game they last saw, with the graphics switch
    /// still ticked, is taken to be that one and put in the ledger, and Boost starting off removes it.
    /// One the player set in Windows with the switch ticked is taken for Boost's too, which is how
    /// 0.8.x treated it as well: it removed it at switch-off.
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
                if (stored is { BoostGpuHighPerformance: true, BoostGamePath: { Length: > 0 } path }
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
            await _storedSettings.UpdateSettingsAsync(s => s with { BoostGpuLeftoverChecked = true }, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Looked for again at the next start.
            _logger.LogWarning(ex, "Could not look for a graphics preference left by an older Optima");
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
    }

    public async Task SyncAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var settings = _settings();
            var coresEnabled = settings?.BoostKeepCoresAwake ?? false;
            var gpuEnabled = settings?.BoostGpuHighPerformance ?? false;
            var inGame = _presence.Current == GamePresence.InGame;

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

            var gamePath = settings?.BoostGamePath;
            if (inGame)
            {
                var tracked = await _monitor.GetTrackedProcessesAsync(ct).ConfigureAwait(false);
                var emulator = tracked.FirstOrDefault(p => p.Kind == TrackedProcessKind.Emulator);
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
                // Nothing else is removed: every preference Boost wrote is in the ledger, so one
                // that is not is the player's own, set in Windows.
                gpuApplied = gpuEnabled;
            }

            Status = new GameExtrasStatus(coresEnabled, _parking is not null, _coresAlreadyAwake, gpuEnabled, gpuApplied, gamePath);
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
    }
}

/// <summary>
/// The game executable Boost gave the high-performance graphics preference to, and the adapter
/// choice Windows had for it before (null when it had none), which is put back afterwards.
/// </summary>
public sealed record GpuPreferenceLedger(string ExecutablePath, string? Previous = null);
