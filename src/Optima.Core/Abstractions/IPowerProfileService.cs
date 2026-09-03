using Optima.Core.Models;

namespace Optima.Core.Abstractions;

public interface IPowerProfileService
{
    Task<Guid> GetActiveSchemeAsync(CancellationToken ct = default);

    Task<string> GetSchemeNameAsync(Guid scheme, CancellationToken ct = default);

    Task<Guid> ApplyAsync(PowerPlanKind kind, CancellationToken ct = default);

    Task RestoreAsync(Guid previousScheme, CancellationToken ct = default);
}
