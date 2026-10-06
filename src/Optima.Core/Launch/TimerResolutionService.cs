using Optima.Core.Abstractions;
using Optima.Core.Boost;
using Optima.Core.Models;
using Optima.Core.Monitoring;
using Microsoft.Extensions.Logging;

namespace Optima.Core.Launch;

/// <summary>What the timer is doing right now, for the BOOST page.</summary>
public sealed record TimerResolutionStatus(
    bool Enabled,
    bool Holding,
    double RequestedMs,
    double? AppliedMs,
    double CurrentMs,
    bool ReachesGame);

/// <summary>
/// Holds the chosen timer resolution for exactly as long as the game is on screen and lets go when
/// it leaves: a fine timer costs battery and a little throughput, so it is not kept a second longer.
/// Whether the hold reaches the game depends on the Windows version; <see cref="TimerResolutionPolicy"/>
/// has the rules and the page reports them rather than assuming.
/// </summary>
public sealed class TimerResolutionService : IDisposable
{
    private readonly ITimerResolution _timer;
    private readonly IProcessMonitor _monitor;
    private readonly GamePresenceService _presence;
    private readonly Func<AppSettings?> _settings;
    private readonly ILogger<TimerResolutionService> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private bool _started;
    private double? _appliedMs;
    private uint _held;

    public TimerResolutionService(
        ITimerResolution timer,
        IProcessMonitor monitor,
        GamePresenceService presence,
        Func<AppSettings?> settings,
        ILogger<TimerResolutionService> logger)
    {
        _timer = timer;
        _monitor = monitor;
        _presence = presence;
        _settings = settings;
        _logger = logger;
        Status = new TimerResolutionStatus(false, false, 1.0, null, 0, false);
    }

    public TimerResolutionStatus Status { get; private set; }

    public event Action<TimerResolutionStatus>? StatusChanged;

    public void Start()
    {
        if (_started)
        {
            return;
        }
        _started = true;
        _presence.PresenceChanged += OnPresenceChanged;
        _ = Task.Run(() => SyncAsync());
    }

    private void OnPresenceChanged(PresenceChange change)
    {
        if ((change.Current == GamePresence.InGame) != (change.Previous == GamePresence.InGame))
        {
            _ = Task.Run(() => SyncAsync());
        }
    }

    /// <summary>Brings the hold in line with the settings and with whether the game is on screen.</summary>
    public async Task SyncAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var settings = _settings();
            var enabled = settings?.BoostTimerResolutionEnabled ?? false;
            var wanted = TimerResolutionPolicy.ToHundredNanoseconds(settings?.BoostTimerResolution);
            var inGame = _presence.Current == GamePresence.InGame;

            if (enabled && inGame)
            {
                if (_held != wanted)
                {
                    _appliedMs = _timer.Hold(wanted);
                    _held = _appliedMs is null ? 0 : wanted;
                }
                var tracked = await _monitor.GetTrackedProcessesAsync(ct).ConfigureAwait(false);
                foreach (var process in tracked.Where(p => p.Kind == TrackedProcessKind.Emulator))
                {
                    _timer.HonorRequestsOf(process.ProcessId);
                }
            }
            else if (_held != 0)
            {
                _timer.Release();
                _held = 0;
                _appliedMs = null;
            }

            Status = new TimerResolutionStatus(
                enabled,
                _held != 0,
                wanted / 10_000.0,
                _appliedMs,
                _timer.CurrentMs,
                TimerResolutionPolicy.HoldReachesGame(_timer.WindowsBuild, _timer.GlobalRequestsEnabled));
            StatusChanged?.Invoke(Status);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Timer resolution sync failed");
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Dispose()
    {
        if (_started)
        {
            _presence.PresenceChanged -= OnPresenceChanged;
        }
        if (_held != 0)
        {
            _timer.Release();
            _held = 0;
        }
    }
}
