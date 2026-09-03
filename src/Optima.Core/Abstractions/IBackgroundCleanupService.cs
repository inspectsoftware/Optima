namespace Optima.Core.Abstractions;

/// <summary>Closes only the applications the user explicitly listed (§10).</summary>
public interface IBackgroundCleanupService
{
    Task<IReadOnlyDictionary<string, ulong>> EstimateImpactAsync(IReadOnlyList<string> processNames, CancellationToken ct = default);

    Task<IReadOnlyList<string>> CloseAsync(IReadOnlyList<string> processNames, CancellationToken ct = default);
}
