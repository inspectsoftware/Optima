using Optima.Core.Abstractions;
using Optima.Core.Configuration;
using Optima.Core.Models;
using Optima.Core.Monitoring;
using Microsoft.Extensions.Logging;

namespace Optima.Core.Launch;

/// <summary>
/// Watch mode (§5): notices the game starting outside Optima and runs the orchestrator's attach path around it (full
/// profile, monitoring, restore on exit).
/// </summary>
public sealed class GameWatchService : IAsyncDisposable
{
    private readonly GamePresenceService _presence;
    private readonly IProcessMonitor _processMonitor;
    private readonly LaunchOrchestrator _orchestrator;
    private readonly SettingsService _settings;
    private readonly ProfileService _profiles;
    private readonly IElevationBroker _elevation;
    private readonly ILogger<GameWatchService> _logger;
    private readonly GameWatchPolicy _policy = new();

    private CancellationTokenSource? _cts;
    private bool _subscribed;
    private volatile bool _watchEnabled;

    public GameWatchService(
        GamePresenceService presence,
        IProcessMonitor processMonitor,
        LaunchOrchestrator orchestrator,
        SettingsService settings,
        ProfileService profiles,
        IElevationBroker elevation,
        ILogger<GameWatchService> logger)
    {
        _presence = presence;
        _processMonitor = processMonitor;
        _orchestrator = orchestrator;
        _settings = settings;
        _profiles = profiles;
        _elevation = elevation;
        _logger = logger;
        _settings.SettingsChanged += (_, s) => _watchEnabled = s.EnableWatchMode;
    }

    public event Action? WatchSessionStarted;

    public event Action<LaunchResult>? WatchSessionEnded;

    public async Task StartAsync(CancellationToken ct = default)
    {
        if (_subscribed)
        {
            return;
        }
        _watchEnabled = (await _settings.GetSettingsAsync(ct).ConfigureAwait(false)).EnableWatchMode;
        _cts = new CancellationTokenSource();
        _presence.Ticked += OnPresenceTick;
        _subscribed = true;
        _logger.LogInformation("Watch mode listening to presence ticks (enabled: {Enabled})", _watchEnabled);
    }

    public Task StopAsync()
    {
        if (_subscribed)
        {
            _presence.Ticked -= OnPresenceTick;
            _subscribed = false;
        }
        _cts?.Cancel();
        _cts?.Dispose();
        _cts = null;
        return Task.CompletedTask;
    }

    private void OnPresenceTick(GameRuntimeState state)
    {
        try
        {
            var action = _policy.OnPoll(_watchEnabled, _orchestrator.IsSessionActive, state);
            if (action != WatchAction.Attach)
            {
                return;
            }

            // The attach spans the whole game session; it must never block the presence
            // loop, so it runs on its own task. The policy's attached flag plus the
            // orchestrator gate prevent double entry.
            var ct = _cts?.Token ?? CancellationToken.None;
            _ = Task.Run(() => AttachSafeAsync(ct), CancellationToken.None);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Watch mode tick failed");
        }
    }

    private async Task AttachSafeAsync(CancellationToken ct)
    {
        try
        {
            await AttachAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Watch attach failed");
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
