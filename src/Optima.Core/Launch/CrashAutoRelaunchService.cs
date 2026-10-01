using Optima.Core.Configuration;
using Optima.Core.Models;
using Optima.Core.Monitoring;
using Microsoft.Extensions.Logging;

namespace Optima.Core.Launch;

/// <summary>Outcome of one crash-relaunch evaluation.</summary>
public enum CrashRelaunchDecision
{
    /// <summary>The emulator died early and the profile should be relaunched.</summary>
    Relaunch,

    /// <summary>The user closed the game normally, or the emulator is still alive.</summary>
    NotACrash,

    /// <summary>A crash, but relaunching is disabled or the per-outage relaunch was used.</summary>
    Skipped,
}

/// <summary>
/// Relaunches the last profile once when the game dies within five minutes of launch and the
/// emulator did not survive (the user choosing to quit is not a crash). Opt-in in Settings.
/// </summary>
public sealed class CrashAutoRelaunchService
{
    /// <summary>How early a death counts as a crash rather than a normal short session.</summary>
    public static readonly TimeSpan CrashWindow = TimeSpan.FromMinutes(5);

    private const int MaxRelaunchesPerOutage = 1;

    private readonly SettingsService _settings;
    private readonly Func<LaunchProfile?, Task> _relaunch;
    private readonly ILogger<CrashAutoRelaunchService> _logger;

    private LaunchProfile? _lastProfile;
    private int _relaunchesUsed;
    private bool _subscribed;

    public CrashAutoRelaunchService(
        SettingsService settings,
        Func<LaunchProfile?, Task> relaunch,
        ILogger<CrashAutoRelaunchService> logger)
    {
        _settings = settings;
        _relaunch = relaunch;
        _logger = logger;
    }

    public event Action<string>? Relaunching;

    /// <summary>Call when a session starts so the service knows what to relaunch.</summary>
    public void NoteSessionStart(LaunchProfile profile)
    {
        _lastProfile = profile;
        _relaunchesUsed = 0;
    }

    public void Start(GamePresenceService presence)
    {
        if (_subscribed)
        {
            return;
        }
        presence.GameExited += OnGameExited;
        _subscribed = true;
    }

    /// <summary>Pure decision core, so the crash window and guardrails stay testable.</summary>
    public static CrashRelaunchDecision Decide(
        GameExit exit, bool enabled, int relaunchesUsed, int maxRelaunches = MaxRelaunchesPerOutage)
        => (enabled, exit.EmulatorStillAlive, exit.RunDuration > CrashWindow) switch
        {
            (false, _, _) => CrashRelaunchDecision.Skipped,
            (_, true, _) => CrashRelaunchDecision.NotACrash,
            (_, _, true) => CrashRelaunchDecision.NotACrash,
            (_, _, _) when relaunchesUsed >= maxRelaunches => CrashRelaunchDecision.Skipped,
            _ => CrashRelaunchDecision.Relaunch,
        };

    private void OnGameExited(GameExit exit)
        => _ = Task.Run(() => HandleExitAsync(exit));

    private async Task HandleExitAsync(GameExit exit)
    {
        try
        {
            var settings = await _settings.GetSettingsAsync().ConfigureAwait(false);
            var decision = Decide(exit, settings.AutoRelaunchOnCrash, _relaunchesUsed);
            if (decision != CrashRelaunchDecision.Relaunch)
            {
                _logger.LogDebug("Crash relaunch not needed ({Decision}, run {Duration:mm\\:ss})", decision, exit.RunDuration);
                return;
            }

            var profile = _lastProfile;
            if (profile is null)
            {
                return;
            }

            _relaunchesUsed++;
            _logger.LogWarning(
                "Game died after {Duration:mm\\:ss}; relaunching '{Profile}' (attempt {Used} of {Max})",
                exit.RunDuration, profile.Name, _relaunchesUsed, MaxRelaunchesPerOutage);
            Relaunching?.Invoke(profile.Name);
            await _relaunch(profile).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Crash auto-relaunch failed");
        }
    }
}
