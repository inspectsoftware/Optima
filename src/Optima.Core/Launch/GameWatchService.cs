using Optima.Core.Abstractions;
using Optima.Core.Configuration;
using Optima.Core.Models;
using Microsoft.Extensions.Logging;

namespace Optima.Core.Launch;

/// <summary>
/// Watch mode (§5): a background poll that notices the game starting outside Optima and runs
/// the orchestrator's attach path around it (full profile, monitoring, restore on exit).
/// The decision logic lives in <see cref="GameWatchPolicy"/>; the orchestrator's session gate
/// guarantees a watch session and a PLAY session can never double-apply. Frametime capture
/// joins only when the elevated helper is already connected, so watch mode never causes a
/// surprise UAC prompt.
/// </summary>
public sealed class GameWatchService : IAsyncDisposable
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(2);

    private readonly IProcessMonitor _processMonitor;
    private readonly LaunchOrchestrator _orchestrator;
    private readonly SettingsService _settings;
    private readonly ProfileService _profiles;
    private readonly IElevationBroker _elevation;
    private readonly ILogger<GameWatchService> _logger;
    private readonly GameWatchPolicy _policy = new();

    private CancellationTokenSource? _cts;
    private Task? _loop;
    private volatile bool _watchEnabled;

    public GameWatchService(
        IProcessMonitor processMonitor,
        LaunchOrchestrator orchestrator,
        SettingsService settings,
        ProfileService profiles,
        IElevationBroker elevation,
        ILogger<GameWatchService> logger)
    {
        _processMonitor = processMonitor;
        _orchestrator = orchestrator;
        _settings = settings;
        _profiles = profiles;
        _elevation = elevation;
        _logger = logger;
        _settings.SettingsChanged += (_, s) => _watchEnabled = s.EnableWatchMode;
    }

    /// <summary>Raised when a watch session begins applying the profile.</summary>
    public event Action? WatchSessionStarted;

    /// <summary>Raised when a watch session finished (restored) or failed.</summary>
    public event Action<LaunchResult>? WatchSessionEnded;

    public async Task StartAsync(CancellationToken ct = default)
    {
        if (_loop is not null)
        {
            return;
        }
        _watchEnabled = (await _settings.GetSettingsAsync(ct).ConfigureAwait(false)).EnableWatchMode;
        _cts = new CancellationTokenSource();
        _loop = Task.Run(() => RunAsync(_cts.Token), CancellationToken.None);
        _logger.LogInformation("Watch mode service started (enabled: {Enabled})", _watchEnabled);
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
                // Process enumeration only happens while the feature is on.
                if (_watchEnabled)
                {
                    var state = await _processMonitor.GetGameStateAsync(ct).ConfigureAwait(false);
                    var action = _policy.OnPoll(_watchEnabled, _orchestrator.IsSessionActive, state);
                    if (action == WatchAction.Attach)
                    {
                        // Spans the whole game session; polling resumes after restore.
                        await AttachAsync(ct).ConfigureAwait(false);
                    }
                }
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Watch mode tick failed");
            }
            await Task.Delay(PollInterval, ct).ConfigureAwait(false);
        }
    }

    private async Task AttachAsync(CancellationToken ct)
    {
        var tracked = await _processMonitor.GetTrackedProcessesAsync(ct).ConfigureAwait(false);
        var emulator = tracked.FirstOrDefault(p => p.Kind == TrackedProcessKind.Emulator);
        if (emulator is null)
        {
            _logger.LogWarning("Watch mode saw the game running but found no emulator process; skipping");
            return;
        }

        var settings = await _settings.GetSettingsAsync(ct).ConfigureAwait(false);
        var profile = await _profiles.GetProfileAsync(settings.SelectedProfileName, ct).ConfigureAwait(false);

        // Never launch the helper (and its UAC prompt) from the background: capture only
        // joins when the helper is already up from earlier use.
        var captureAllowed = settings.EnableFrametimeCapture && _elevation.IsConnected;

        _logger.LogInformation(
            "Watch mode attaching to {Process} (pid {Pid}) with profile '{Profile}' (capture: {Capture})",
            emulator.Name, emulator.ProcessId, profile.Name, captureAllowed);
        WatchSessionStarted?.Invoke();

        var result = await _orchestrator.AttachToRunningGameAsync(profile, emulator.ProcessId, captureAllowed, ct).ConfigureAwait(false);
        if (result.Success)
        {
            _logger.LogInformation("Watch session complete; settings restored");
        }
        else
        {
            _logger.LogWarning("Watch session ended: {Error}", result.Error?.Title ?? "unknown error");
        }
        WatchSessionEnded?.Invoke(result);
    }

    public async ValueTask DisposeAsync() => await StopAsync().ConfigureAwait(false);
}
