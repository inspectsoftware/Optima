using Optima.Core.Models;

namespace Optima.Core.Abstractions;

/// <summary>
/// Lowers named background programs out of the game's way without closing them: below-normal
/// priority and Windows' efficiency mode. Each returned snapshot is what
/// <see cref="IProcessOptimizer.RestoreAsync"/> needs to put that process back exactly.
/// </summary>
public interface IBackgroundDemoter
{
    /// <summary>
    /// Demotes every running process with one of the given names that is not in
    /// <paramref name="alreadyDemoted"/>. System processes, the game, Google Play Games and Optima
    /// itself are never touched, whatever the list says.
    /// </summary>
    Task<IReadOnlyList<ProcessStateSnapshot>> DemoteAsync(
        IReadOnlyList<string> processNames, IReadOnlySet<int> alreadyDemoted, CancellationToken ct = default);
}
