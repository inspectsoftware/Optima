using Optima.Core.Models;

namespace Optima.Core.Abstractions;

/// <summary>One row on the diagnostics page (§15).</summary>
public interface IDiagnosticCheck
{
    string Name { get; }

    int Order { get; }

    Task<DiagnosticResult> RunAsync(CancellationToken ct = default);
}
