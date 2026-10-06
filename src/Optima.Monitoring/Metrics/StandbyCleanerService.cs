using System.Globalization;
using Optima.Core.Abstractions;
using Optima.Core.Boost;
using Optima.Core.Ipc;
using Optima.Core.Models;
using Optima.Core.Monitoring;
using Microsoft.Extensions.Logging;

namespace Optima.Monitoring.Metrics;

public enum StandbyCleanerState
{
    Off,
    WaitingForGame,
    /// <summary>The game runs but the elevated helper does not, and a background start would mean a surprise admin prompt.</summary>
    WaitingForHelper,
    Running,
}

/// <summary>The cleaner's state and the helper's latest readout, for the BOOST page.</summary>
public sealed record StandbyCleanerStatus(
    StandbyCleanerState State,
    long? FreeMb,
    long? StandbyMb,
    int Purges,
    long LastFreedMb,
    DateTimeOffset? LastPurgeAt)
{
    public static StandbyCleanerStatus Initial { get; } = new(StandbyCleanerState.Off, null, null, 0, 0, null);
}

/// <summary>
/// App side of the BOOST memory cleaner. The work is done by the elevated helper; this decides when
/// it runs: only while the game is on screen, and only through a helper that is already there. The
/// one thing that may raise the admin prompt is a deliberate act (switching the cleaner on, PLAY,
/// "purge now"), never a game that happened to start in the background.
/// </summary>
public sealed class StandbyCleanerService : IDisposable
{
    private readonly IElevationBroker _broker;
    private readonly GamePresenceService _presence;
    private readonly Func<AppSettings?> _settings;
    private readonly ILogger<StandbyCleanerService> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private bool _started;
    private bool _running;

    public StandbyCleanerService(
        IElevationBroker broker,
        GamePresenceService presence,
        Func<AppSettings?> settings,
        ILogger<StandbyCleanerService> logger)
    {
        _broker = broker;
        _presence = presence;
        _settings = settings;
        _logger = logger;
    }

    public StandbyCleanerStatus Status { get; private set; } = StandbyCleanerStatus.Initial;

    /// <summary>Raised on a state change and on every readout from the helper, on whatever thread delivered it.</summary>
    public event Action<StandbyCleanerStatus>? StatusChanged;

    public void Start()
    {
        if (_started)
        {
            return;
        }
        _started = true;
        _broker.EventReceived += OnEvent;
        _presence.PresenceChanged += OnPresenceChanged;
        _ = Task.Run(() => SyncAsync(allowPrompt: false));
    }

    private void OnPresenceChanged(PresenceChange change)
    {
        if ((change.Current == GamePresence.InGame) != (change.Previous == GamePresence.InGame))
        {
            _ = Task.Run(() => SyncAsync(allowPrompt: false));
        }
    }

    /// <summary>
    /// Brings the helper's cleaner in line with the settings and the game. Called with
    /// <paramref name="allowPrompt"/> from the page and from PLAY, where an admin prompt is expected.
    /// </summary>
    public async Task SyncAsync(bool allowPrompt, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var settings = _settings();
            var enabled = settings?.BoostStandbyCleanerEnabled ?? false;
            if (!enabled)
            {
                await StopCoreAsync(ct).ConfigureAwait(false);
                Publish(Status with { State = StandbyCleanerState.Off });
                return;
            }

            if (allowPrompt && !_broker.IsConnected)
            {
                await _broker.EnsureStartedAsync(ct).ConfigureAwait(false);
            }

            if (_presence.Current != GamePresence.InGame)
            {
                await StopCoreAsync(ct).ConfigureAwait(false);
                Publish(Status with { State = StandbyCleanerState.WaitingForGame });
                return;
            }

            if (!_broker.IsConnected)
            {
                _running = false;
                Publish(Status with { State = StandbyCleanerState.WaitingForHelper });
                return;
            }

            var response = await _broker.SendAsync(new IpcRequest
            {
                Command = IpcCommand.StartStandbyCleaner,
                Args = new Dictionary<string, string>
                {
                    ["freeBelowMb"] = StandbyCleanerPolicy.ClampThreshold(settings!.BoostStandbyFreeBelowMb).ToString(CultureInfo.InvariantCulture),
                    ["standbyAboveMb"] = StandbyCleanerPolicy.ClampThreshold(settings.BoostStandbyAboveMb).ToString(CultureInfo.InvariantCulture),
                    ["intervalMs"] = StandbyCleanerPolicy.DefaultIntervalMs.ToString(CultureInfo.InvariantCulture),
                },
            }, ct).ConfigureAwait(false);
            _running = response.Success;
            if (!response.Success)
            {
                _logger.LogWarning("Memory cleaner did not start: {Error}", response.Error);
            }
            Publish(Status with { State = response.Success ? StandbyCleanerState.Running : StandbyCleanerState.WaitingForHelper });
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Memory cleaner sync failed");
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>One purge now. Deliberate, so it may raise the admin prompt. Returns the megabytes freed, or null when it could not run.</summary>
    public async Task<long?> PurgeNowAsync(CancellationToken ct = default)
    {
        if (!await _broker.EnsureStartedAsync(ct).ConfigureAwait(false))
        {
            return null;
        }
        var response = await _broker.SendAsync(new IpcRequest { Command = IpcCommand.PurgeStandbyList }, ct).ConfigureAwait(false);
        if (!response.Success)
        {
            _logger.LogWarning("Standby purge failed: {Error}", response.Error);
            return null;
        }
        var freed = ParseLong(response.Data.GetValueOrDefault("freedMb")) ?? 0;
        Publish(Status with
        {
            FreeMb = ParseLong(response.Data.GetValueOrDefault("freeMb")),
            StandbyMb = ParseLong(response.Data.GetValueOrDefault("standbyMb")),
            Purges = Status.Purges + 1,
            LastFreedMb = freed,
            LastPurgeAt = DateTimeOffset.Now,
        });
        return freed;
    }

    private async Task StopCoreAsync(CancellationToken ct)
    {
        if (_running && _broker.IsConnected)
        {
            await _broker.SendAsync(new IpcRequest { Command = IpcCommand.StopStandbyCleaner }, ct).ConfigureAwait(false);
        }
        _running = false;
    }

    private void OnEvent(object? sender, IpcEvent ipcEvent)
    {
        if (ipcEvent.Kind != "memoryStatus")
        {
            return;
        }
        var lastPurge = DateTimeOffset.TryParse(
            ipcEvent.Data.GetValueOrDefault("lastPurgeAt"), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var at)
            ? at
            : (DateTimeOffset?)null;
        Publish(Status with
        {
            FreeMb = ParseLong(ipcEvent.Data.GetValueOrDefault("freeMb")),
            StandbyMb = ParseLong(ipcEvent.Data.GetValueOrDefault("standbyMb")),
            Purges = (int)(ParseLong(ipcEvent.Data.GetValueOrDefault("purges")) ?? Status.Purges),
            LastFreedMb = ParseLong(ipcEvent.Data.GetValueOrDefault("lastFreedMb")) ?? Status.LastFreedMb,
            LastPurgeAt = lastPurge ?? Status.LastPurgeAt,
        });
    }

    private static long? ParseLong(string? text)
        => long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : null;

    private void Publish(StandbyCleanerStatus status)
    {
        Status = status;
        StatusChanged?.Invoke(status);
    }

    public void Dispose()
    {
        if (_started)
        {
            _broker.EventReceived -= OnEvent;
            _presence.PresenceChanged -= OnPresenceChanged;
        }
    }
}
