namespace Optima.Core.Abstractions;

/// <summary>The Windows system timer, as far as BOOST needs it.</summary>
public interface ITimerResolution
{
    int WindowsBuild { get; }

    /// <summary>Whether GlobalTimerResolutionRequests is set in the registry (it takes effect after a restart).</summary>
    bool GlobalRequestsEnabled { get; }

    /// <summary>
    /// Windows' clock right now, in milliseconds: the finest resolution any process asks for. Since
    /// Windows 10 2004 that is not the tick of a process that did not ask for it itself.
    /// </summary>
    double CurrentMs { get; }

    /// <summary>Asks Windows for a resolution and keeps asking until released. Returns what Windows applied, or null when it refused.</summary>
    double? Hold(uint hundredNanoseconds);

    void Release();

    /// <summary>
    /// Windows 11 may ignore the timer request of a process whose window is hidden. This tells it to
    /// always honour the requests of the given process. A no-op where Windows has no such rule.
    /// </summary>
    void HonorRequestsOf(int processId);
}
