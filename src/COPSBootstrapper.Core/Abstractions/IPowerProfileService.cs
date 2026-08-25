using COPSBootstrapper.Core.Models;

namespace COPSBootstrapper.Core.Abstractions;

public interface IPowerProfileService
{
    Task<Guid> GetActiveSchemeAsync(CancellationToken ct = default);

    Task<string> GetSchemeNameAsync(Guid scheme, CancellationToken ct = default);

    /// <summary>Activates the scheme for the given kind, creating Ultimate Performance if needed. Returns the previous scheme.</summary>
    Task<Guid> ApplyAsync(PowerPlanKind kind, CancellationToken ct = default);

    Task RestoreAsync(Guid previousScheme, CancellationToken ct = default);
}
