namespace Optima.Core.Launch;

/// <summary>
/// What a launch can lean on when a step goes wrong. Optional: a launch without it behaves as it
/// always did, and nothing here may ever make a launch fail that would otherwise have worked.
/// </summary>
public interface ILaunchSupport
{
    /// <summary>
    /// Runs before anything on the system is changed: the quick checks, so that a problem this
    /// session is about to run into is on the issue list before the session finds it the hard way.
    /// Bounded; a slow check is left behind rather than waited for.
    /// </summary>
    Task PreflightAsync(CancellationToken ct = default);

    /// <summary>
    /// Tries the one repair that can make a failed step worth running again, for the error code the
    /// step failed with. True when a repair ran and the step should be given its second try.
    /// </summary>
    Task<bool> TryRepairAsync(string code, CancellationToken ct = default);
}
