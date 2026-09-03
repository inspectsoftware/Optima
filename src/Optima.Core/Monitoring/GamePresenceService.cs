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

/// <summary>The synthesized end-of-run edge.</summary>
public sealed record GameExit(DateTimeOffset At, TimeSpan RunDuration, bool EmulatorStillAlive);

/// <summary>
/// The Watchdog's always-on presence loop: one cheap poll (process list + window scan via IProcessMonitor) every couple
/// of seconds, translated into edges everyone else consumes: Discord presence, session tracking, the watch-attach
/// policy, and crash capture.
/// </summary>
public sealed class GamePresenceService : IAsyncDisposable
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(2);

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
            var emulatorAlive = next == GamePresence.Starting;
            if (emulatorAlive && ++_fallingPolls < ExitDebouncePolls)
            {
                return;
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
