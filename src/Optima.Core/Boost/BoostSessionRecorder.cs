using System.Globalization;
using Optima.Core.Launch;
using Optima.Core.Models;

namespace Optima.Core.Boost;

/// <summary>One Boost switch as the session history knows it: a short key that is stored, the name the page shows, and where its switch is in the settings.</summary>
public sealed record BoostFeature(string Key, string Name, Func<AppSettings, bool> IsOn);

/// <summary>Every Boost switch, in the order of the BOOST page's cards.</summary>
public static class BoostFeatures
{
    public const string Priority = "priority";
    public const string Standby = "standby";
    public const string Timer = "timer";
    public const string Demote = "demote";
    public const string Wifi = "wifi";
    public const string Cores = "cores";
    public const string Placement = "placement";
    public const string PowerFloor = "powerFloor";
    public const string Gpu = "gpu";
    public const string GpuPriority = "gpuPriority";

    public static IReadOnlyList<BoostFeature> All { get; } =
    [
        new(Priority, "Priority guard", s => s.BoostPriorityGuardEnabled),
        new(Standby, "Memory cleaner", s => s.BoostStandbyCleanerEnabled),
        new(Timer, "Timer resolution", s => s.BoostTimerResolutionEnabled),
        new(Demote, "Programs held back", s => s.BoostDemoteBackgroundEnabled),
        new(Wifi, "Wi-Fi scans paused", s => s.BoostWifiScanPaused),
        new(Cores, "Core parking off", s => s.BoostKeepCoresAwake),
        new(Placement, "Best cores for the game", s => s.BoostCorePlacement),
        new(PowerFloor, "Power floor", s => s.BoostPowerFloor),
        new(Gpu, "High-performance adapter", s => s.BoostGpuHighPerformance),
        new(GpuPriority, "Higher GPU priority", s => s.BoostGpuPriorityHigh),
    ];
}

/// <summary>
/// What Boost had on during one saved session, and which of those switches actually did something
/// at some point in it. Kept apart because a switch can be on and still do nothing: core parking is
/// already off on a High Performance plan, the graphics card refuses a priority, the helper is not
/// running. Such a session tells nothing about the switch either way, so the comparison leaves it out.
/// </summary>
public sealed record BoostSessionRecord
{
    /// <summary>Who was tracked when the session ended (<see cref="BoostSessionRecorder.PlayerKey"/>), so one player's sessions are compared with each other only.</summary>
    public string Player { get; init; } = string.Empty;

    /// <summary>The <see cref="BoostFeatures"/> keys whose switch was on, with the master switch on as well.</summary>
    public IReadOnlyList<string> On { get; init; } = [];

    /// <summary>Of <see cref="On"/>, the ones whose service reported itself working during the session.</summary>
    public IReadOnlyList<string> Applied { get; init; } = [];

    // By content: the Sessions page compares two reads of a session to see whether anything changed,
    // and every read builds these lists anew.
    public bool Equals(BoostSessionRecord? other)
        => other is not null && Player == other.Player && On.SequenceEqual(other.On) && Applied.SequenceEqual(other.Applied);

    public override int GetHashCode() => HashCode.Combine(Player, On.Count, Applied.Count);
}

/// <summary>
/// Remembers which Boost switches did something during the session the launch is running. The
/// services' statuses at the moment the session ends are no good for this: the game has just left,
/// and every boost has already let go. So each status change is watched as it happens, and a switch
/// counts as applied if its service reported itself working at any point between
/// <see cref="Begin"/> and <see cref="End"/>, or was already working when the session began.
/// </summary>
public sealed class BoostSessionRecorder
{
    private readonly object _gate = new();
    private readonly Dictionary<string, bool> _now = [];
    private readonly HashSet<string> _seen = [];
    private bool _recording;
    private IReadOnlySet<string>? _onAtBegin;
    private int? _purges;

    /// <summary>The tracked player as one string: the account id when there is one, the name otherwise, empty when nobody is tracked.</summary>
    public static string PlayerKey(AppSettings settings)
        => settings.PlayerAccountId is > 0 and var id
            ? id.ToString(CultureInfo.InvariantCulture)
            : settings.PlayerIgn.Trim().ToLowerInvariant();

    /// <summary>
    /// The keys a tracked player's sessions can carry: the account id and the name both. A player
    /// tracked while the stats lookup was down is stored under the name until the id is found, and
    /// the sessions from before that are still theirs.
    /// </summary>
    public static string[] PlayerKeys(AppSettings settings)
        => PlayerKey(settings) is var key && settings.PlayerIgn.Trim().ToLowerInvariant() is { Length: > 0 } name && name != key
            ? [key, name]
            : [key];

    /// <summary>
    /// Starts a session's record, with what is already working counted in. The switches on now are
    /// kept, so one turned on or off while the game ran is known at <see cref="End"/>.
    /// </summary>
    public void Begin(AppSettings? settings = null)
    {
        lock (_gate)
        {
            _onAtBegin = settings is null ? null : OnIn(settings).ToHashSet();
            _seen.Clear();
            _seen.UnionWith(_now.Where(pair => pair.Value).Select(pair => pair.Key));
            _recording = true;
        }
    }

    /// <summary>Whether one switch's service is doing something right now.</summary>
    public void Observe(string feature, bool applied)
    {
        lock (_gate)
        {
            _now[feature] = applied;
            if (applied && _recording)
            {
                _seen.Add(feature);
            }
        }
    }

    // The same tests the BOOST page's pills use for "working". The guard stands down while an Optima
    // session keeps the priority itself, and every saved session is one, so it only counts when it held.
    public void Observe(PriorityGuardStatus status)
        => Observe(BoostFeatures.Priority, status is { Enabled: true, SessionOwned: false, ProcessCount: > 0 });

    public void Observe(TimerResolutionStatus status) => Observe(BoostFeatures.Timer, status.Enabled && status.Holding);

    public void Observe(BackgroundDemotionStatus status) => Observe(BoostFeatures.Demote, status.Enabled && status.ProcessCount > 0);

    public void Observe(WifiScanStatus status) => Observe(BoostFeatures.Wifi, status.Enabled && status.Held);

    // Cores already awake (a High Performance plan) and a power floor the plan already has are left
    // out on purpose: the sessions with the switch off had the same, so they are not "with" it.
    public void Observe(GameExtrasStatus status)
    {
        Observe(BoostFeatures.Cores, status.CoresEnabled && status.CoresHeld);
        Observe(BoostFeatures.Placement, status.PlacementEnabled && status.PlacementApplied);
        Observe(BoostFeatures.PowerFloor, status.PowerFloorEnabled && status.PowerFloorHeld);
        Observe(BoostFeatures.Gpu, status.GpuEnabled && status.GpuApplied && !status.GpuAlreadySet);
        Observe(BoostFeatures.GpuPriority, status.GpuPriorityEnabled && status.GpuPriorityRaised > 0);
    }

    /// <summary>
    /// The memory cleaner, from its state and the helper's count of purges. Running is not enough:
    /// with plenty of free memory it watches a whole match and never purges, which is what a session
    /// with it off has too. So it counts only when the count goes up while it runs.
    /// </summary>
    public void ObserveCleaner(bool running, int purges)
    {
        lock (_gate)
        {
            var purged = _purges is { } before && purges > before;
            _purges = purges;
            Observe(BoostFeatures.Standby, running && purged);
        }
    }

    /// <summary>
    /// Ends the record and returns it, from the stored settings as the session ends. Null without
    /// settings: a session whose switches are not known is left out, not counted as all off. A switch
    /// that was on at one end of the session and off at the other is put in <see cref="BoostSessionRecord.On"/>
    /// but never in Applied, so it counts as neither: the session was partly with it and partly without.
    /// </summary>
    public BoostSessionRecord? End(AppSettings? settings)
    {
        lock (_gate)
        {
            _recording = false;
            if (settings is null)
            {
                return null;
            }
            var atEnd = OnIn(settings).ToList();
            var atBegin = _onAtBegin ?? atEnd.ToHashSet();
            return new BoostSessionRecord
            {
                Player = PlayerKey(settings),
                On = [.. BoostFeatures.All.Select(f => f.Key).Where(key => atBegin.Contains(key) || atEnd.Contains(key))],
                Applied = [.. atEnd.Where(key => atBegin.Contains(key) && _seen.Contains(key))],
            };
        }
    }

    private static IEnumerable<string> OnIn(AppSettings settings)
    {
        var effective = settings.BoostEffective();
        return BoostFeatures.All.Where(f => f.IsOn(effective)).Select(f => f.Key);
    }
}
