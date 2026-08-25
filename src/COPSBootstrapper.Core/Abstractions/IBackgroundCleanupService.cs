namespace COPSBootstrapper.Core.Abstractions;

/// <summary>Closes only the applications the user explicitly listed (§10). Never kills anything else.</summary>
public interface IBackgroundCleanupService
{
    /// <summary>Estimated memory (bytes) currently used by the listed processes, per process name.</summary>
    Task<IReadOnlyDictionary<string, ulong>> EstimateImpactAsync(IReadOnlyList<string> processNames, CancellationToken ct = default);

    /// <summary>Politely closes (then terminates unresponsive) listed processes. Returns names actually closed.</summary>
    Task<IReadOnlyList<string>> CloseAsync(IReadOnlyList<string> processNames, CancellationToken ct = default);
}
