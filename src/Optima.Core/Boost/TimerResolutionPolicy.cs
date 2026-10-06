namespace Optima.Core.Boost;

/// <summary>How far a timer resolution request made by one process reaches on a given Windows.</summary>
public enum TimerResolutionReach
{
    /// <summary>Before Windows 10 2004: the finest request of any process applies to every process.</summary>
    SystemWide,

    /// <summary>Windows 10 2004 to 22H2: a request applies to the process that made it, and nothing switches that off.</summary>
    OwnProcessOnly,

    /// <summary>
    /// Windows 11: per process as well, and a hidden window-owning process can have its request
    /// ignored; but GlobalTimerResolutionRequests in the registry brings the system-wide rule back.
    /// </summary>
    OwnProcessUnlessGlobalSwitch,
}

/// <summary>
/// The Windows-version rules for timer resolution, from Microsoft's documentation of timeBeginPeriod
/// and SetProcessInformation. Kept apart from the code that calls Windows so the rules can be tested
/// against build numbers this machine does not run.
/// </summary>
public static class TimerResolutionPolicy
{
    public const int PerProcessBuild = 19041;
    public const int Windows11Build = 22000;

    public const string OneMillisecond = "1.0";
    public const string HalfMillisecond = "0.5";

    public static IReadOnlyList<string> Choices { get; } = [OneMillisecond, HalfMillisecond];

    public static TimerResolutionReach ReachFor(int windowsBuild) => windowsBuild switch
    {
        < PerProcessBuild => TimerResolutionReach.SystemWide,
        < Windows11Build => TimerResolutionReach.OwnProcessOnly,
        _ => TimerResolutionReach.OwnProcessUnlessGlobalSwitch,
    };

    /// <summary>Whether a resolution held by Optima also applies to the game's process.</summary>
    public static bool HoldReachesGame(int windowsBuild, bool globalSwitchOn) => ReachFor(windowsBuild) switch
    {
        TimerResolutionReach.SystemWide => true,
        TimerResolutionReach.OwnProcessUnlessGlobalSwitch => globalSwitchOn,
        _ => false,
    };

    /// <summary>The choice as Windows counts it: units of 100 nanoseconds.</summary>
    public static uint ToHundredNanoseconds(string? choice)
        => string.Equals(choice, HalfMillisecond, StringComparison.Ordinal) ? 5_000u : 10_000u;

    /// <summary>What this Windows does with the request, in the words the BOOST page shows.</summary>
    public static string Describe(int windowsBuild, bool globalSwitchOn) => ReachFor(windowsBuild) switch
    {
        TimerResolutionReach.SystemWide =>
            "This Windows applies the finest timer any program asks for to every program, so what Optima holds reaches the game.",
        TimerResolutionReach.OwnProcessOnly =>
            "Windows 10 from version 2004 keeps a timer request inside the program that made it, and has no switch to change that. "
            + "What Optima holds here cannot reach the game; the game's own request stays in effect. Leave this off unless you want to try it.",
        _ => globalSwitchOn
            ? "Windows 11 keeps a timer request inside the program that made it, but the system-wide switch below is on, "
                + "so what Optima holds reaches the game (after the restart that switch needs)."
            : "Windows 11 keeps a timer request inside the program that made it. Optima still makes sure the game's own request "
                + "is honoured even when Windows would ignore it. For Optima's hold to reach the game, turn on the system-wide switch below.",
    };
}
