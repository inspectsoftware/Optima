using Optima.Core.Abstractions;
using Optima.Core.Models;
using Optima.Core.Monitoring;
using Microsoft.Extensions.Logging;

namespace Optima.Core.Launch;

/// <summary>
/// What the Wi-Fi pause is doing right now, for the BOOST page. HasWifi is false when this PC has no
/// Wi-Fi at all; Interfaces lists the connected ones Optima holds, with what Windows accepted on each.
/// Failed is true when the last pass could not reach the WLAN service, so an empty list then does not
/// mean the player is off Wi-Fi.
/// </summary>
public sealed record WifiScanStatus(bool Enabled, bool GameOnScreen, bool HasWifi, IReadOnlyList<WifiInterfaceHold> Interfaces, bool Failed = false)
{
    /// <summary>Windows accepted at least one of the two requests on at least one interface.</summary>
    public bool Held => Interfaces.Any(i => i.StreamingMode || i.ScanPaused);
}

/// <summary>
/// While the game is on screen, keeps Windows from scanning for other Wi-Fi networks, which it does
/// about once a minute even while connected and which shows as a regular ping spike in a match. The
/// requests belong to Optima's session with the WLAN service (see <see cref="IWifiScanPause"/>), so
/// closing it when the game leaves, the switch goes off or Optima closes takes them back, and a crash
/// takes them back with the process. That is why there is no ledger here, unlike the other boosts.
/// </summary>
public sealed class WifiScanService : IDisposable
{
    /// <summary>
    /// How often the requests are made again mid-match. A disconnect drops both, and Wi-Fi does drop
    /// and reconnect, so an interface that came back is held again within this long.
    /// </summary>
    public static readonly TimeSpan DefaultInterval = TimeSpan.FromSeconds(30);

    private readonly IWifiScanPause _wifi;
    private readonly GamePresenceService _presence;
    private readonly Func<AppSettings?> _settings;
    private readonly ILogger<WifiScanService> _logger;
    private readonly TimeSpan _interval;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private CancellationTokenSource? _cts;
    private bool _open;
    private volatile bool _disposed;

    public WifiScanService(
        IWifiScanPause wifi,
        GamePresenceService presence,
        Func<AppSettings?> settings,
        ILogger<WifiScanService> logger,
        TimeSpan? interval = null)
    {
        _wifi = wifi;
        _presence = presence;
        _settings = settings;
        _logger = logger;
        _interval = interval ?? DefaultInterval;
    }

    public WifiScanStatus Status { get; private set; } = new(false, false, true, []);

    public event Action<WifiScanStatus>? StatusChanged;

    public void Start()
    {
        if (_cts is not null)
        {
            return;
        }
        _cts = new CancellationTokenSource();
        _presence.PresenceChanged += OnPresenceChanged;
        _ = Task.Run(() => RunAsync(_cts.Token));
    }

    private void OnPresenceChanged(PresenceChange change)
    {
        if ((change.Current == GamePresence.InGame) != (change.Previous == GamePresence.InGame))
        {
            _ = Task.Run(() => SyncAsync());
        }
    }

    private async Task RunAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await SyncAsync(ct).ConfigureAwait(false);
                await Task.Delay(_interval, ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    /// <summary>One pass: hold every connected interface while the game is on screen, let go otherwise.</summary>
    public async Task SyncAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var enabled = _settings()?.BoostWifiScanPaused ?? false;
            var inGame = _presence.Current == GamePresence.InGame;
            var hasWifi = true;
            var failed = false;
            IReadOnlyList<WifiInterfaceHold> held = [];
            // Never after Dispose: a session opened then would have nothing left to close it.
            if (enabled && inGame && !_disposed)
            {
                try
                {
                    _open = true;
                    var interfaces = _wifi.Hold();
                    hasWifi = interfaces is not null;
                    held = interfaces ?? [];
                }
                catch (Exception ex)
                {
                    // Asked again at the next pass.
                    failed = true;
                    _logger.LogWarning(ex, "Could not pause Wi-Fi scanning");
                }
                if (!held.SequenceEqual(Status.Interfaces))
                {
                    _logger.LogInformation("Wi-Fi held for the game: {Interfaces}", held.Count == 0
                        ? "no connected interface"
                        : string.Join("; ", held.Select(i => $"{i.Name} (streaming mode {(i.StreamingMode ? "on" : "refused")}, background scan {(i.ScanPaused ? "off" : "refused")})")));
                }
            }
            else
            {
                Close();
            }
            Status = new WifiScanStatus(enabled, inGame, hasWifi, held, failed);
            StatusChanged?.Invoke(Status);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Wi-Fi scan pass failed");
        }
        finally
        {
            _gate.Release();
        }
    }

    private void Close()
    {
        if (!_open)
        {
            return;
        }
        _wifi.Release();
        _open = false;
        _logger.LogInformation("Wi-Fi let go: Windows scans as usual again");
    }

    public void Dispose()
    {
        _presence.PresenceChanged -= OnPresenceChanged;
        _disposed = true;
        _cts?.Cancel();
        // Bounded, as the other boosts are: a pass stuck in the WLAN service must not hold up the
        // exit, and its session is not closed from under it. It ends with the process anyway.
        if (_gate.Wait(TimeSpan.FromSeconds(2)))
        {
            try
            {
                Close();
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Wi-Fi could not be let go on exit");
            }
            finally
            {
                _gate.Release();
            }
        }
        _cts?.Dispose();
    }
}
