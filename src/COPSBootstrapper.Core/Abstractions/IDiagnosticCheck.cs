using COPSBootstrapper.Core.Models;

namespace COPSBootstrapper.Core.Abstractions;

/// <summary>One row on the diagnostics page (§15). Checks are registered in DI and run in order.</summary>
public interface IDiagnosticCheck
{
    string Name { get; }

    int Order { get; }

    Task<DiagnosticResult> RunAsync(CancellationToken ct = default);
}
