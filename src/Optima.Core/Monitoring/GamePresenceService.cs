using Optima.Core.Abstractions;
using Optima.Core.Models;
using Microsoft.Extensions.Logging;

namespace Optima.Core.Monitoring;

/// <summary>Game presence as consumers care about it, independent of platform plumbing.</summary>
public enum GamePresence
{
    NotRunning,
    Starting,
    InGame,
}

/// <summary>One presence transition.</summary>
public sealed record PresenceChange(GamePresence Previous, GamePresence Current, DateTimeOffset At);

/// <summary>
/// The synthesized end-of-run edge. <paramref name="EmulatorStillAlive"/> is a hint for the
/// crash sentinel: the game window vanished while crosvm stayed up, which is how both a
/// game crash and a menu-quit look from the Windows side; the logcat decides which.
/// </summary>
public sealed record GameExit(DateTimeOffset At, TimeSpan RunDuration, bool EmulatorStillAlive);

/// <summary>
/// The Watchdog's always-on presence loop: one cheap poll (process list + window scan via
/// IProcessMonitor) every couple of seconds, translated into edges everyone else consumes:
/// Discord presence, session tracking, the watch-attach policy, and crash capture. It never
/// takes the orchestrator's session gate and it never blocks on a session; attach work
/// triggered from its ticks must run on its own task.
/// It also synthesizes the exit edge the raw state machine never produced: the platform
/// reports NotRunning/Starting/Running only, so "the game just ended" is derived here from
/// InGame followed by a debounced fall to a lower state.
/// </summary>
public sealed class GamePresenceService : IAsyncDisposable
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(2);

    /// <summary>Polls the window may flicker for (alt-tab, resolution change) before an
    /// InGame -> Starting fall is believed to be a real exit.</summary>
    internal const int ExitDebouncePolls = 3;

    private readonly IProcessMonitor _processMonitor;
    private readonly ILogger<GamePresenceService> _logger;

    private CancellationTokenSource? _cts;
    private Task? _loop;
    private GamePresence _current = GamePresence.NotRunning;
    private DateTimeOffset? _inGameSince;
    private int _fallingPolls;

    public GamePresenceService(IProcessMonitor processMonitor, ILogger<GamePresenceService> logger)
    {
        _processMonitor = processMonitor;
        _logger = logger;
    }

    /// <summary>Every poll's raw state, for consumers that count ticks (the watch policy).</summary>
    public event Action<GameRuntimeState>? Ticked;

    public event Action<PresenceChange>? PresenceChanged;

    public event Action<GameExit>? GameExited;

    public GamePresence Current => _current;

    public DateTimeOffset? InGameSince => _inGameSince;

    public Task StartAsync(CancellationToken ct = default)
    {
        if (_loop is null)
        {
            _cts = new CancellationTokenSource();
            _loop = Task.Run(() => RunAsync(_cts.Token), CancellationToken.None);
            _logger.LogInformation("Watchdog presence loop started");
        }
        return Task.CompletedTask;
    }

    public async Task StopAsync()
    {
        var loop = _loop;
        if (loop is null)
        {
            return;
        }
        _cts?.Cancel();
        try
        {
            await loop.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        _cts?.Dispose();
        _cts = null;
        _loop = null;
    }

    private async Task RunAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var state = await _processMonitor.GetGameStateAsync(ct).ConfigureAwait(false);
                ApplyState(state, DateTimeOffset.Now);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Presence tick failed");
            }
            await Task.Delay(PollInterval, ct).ConfigureAwait(false);
        }
    }

    /// <summary>One tick of the state machine. Public so tests (and diagnostics) can drive
    /// the transitions without the timer; production code must only feed it from the loop.</summary>
    public void ApplyState(GameRuntimeState state, DateTimeOffset now)
    {
        Ticked?.Invoke(state);

        var next = state switch
        {
            GameRuntimeState.Running => GamePresence.InGame,
            GameRuntimeState.Starting => GamePresence.Starting,
            _ => GamePresence.NotRunning,
        };

        if (_current == GamePresence.InGame && next != GamePresence.InGame)
        {
            // The window is gone. Direct NotRunning (whole stack down) ends the run now;
            // a fall to Starting (emulator alive) gets the flicker debounce first.
            var emulatorAlive = next == GamePresence.Starting;
            if (emulatorAlive && ++_fallingPolls < ExitDebouncePolls)
            {
                return; // still InGame as far as consumers know
            }
            EndRun(now, emulatorAlive);
            SetCurrent(GamePresence.NotRunning, now);
            return;
        }

        _fallingPolls = 0;
        if (next == GamePresence.InGame && _inGameSince is null)
        {
            _inGameSince = now;
        }
        if (next != _current)
        {
            SetCurrent(next, now);
        }
    }

    private void EndRun(DateTimeOffset now, bool emulatorStillAlive)
    {
        var duration = _inGameSince is { } since ? now - since : TimeSpan.Zero;
        _inGameSince = null;
        _fallingPolls = 0;
        _logger.LogInformation(
            "Game run ended after {Duration:mm\\:ss} (emulator still alive: {EmulatorAlive})",
            duration, emulatorStillAlive);
        GameExited?.Invoke(new GameExit(now, duration, emulatorStillAlive));
    }

    private void SetCurrent(GamePresence next, DateTimeOffset now)
    {
        var previous = _current;
        _current = next;
        PresenceChanged?.Invoke(new PresenceChange(previous, next, now));
    }

    public async ValueTask DisposeAsync() => await StopAsync().ConfigureAwait(false);
}
