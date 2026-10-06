using Optima.Core.Models;

namespace Optima.Core.Abstractions;

/// <summary>When a check may run without anyone having asked for it.</summary>
[Flags]
public enum CheckScope
{
    /// <summary>Only when the checks are run from the Debug page.</summary>
    OnDemand = 0,

    /// <summary>Also once, in the background, shortly after Optima starts.</summary>
    Startup = 1,

    /// <summary>Also at the start of every launch, before anything on the system is changed.</summary>
    Preflight = 2,
}

/// <summary>One row of the checks on the Debug page (§15).</summary>
public interface IDiagnosticCheck
{
    string Name { get; }

    int Order { get; }

    /// <summary>
    /// A check that runs unasked has to be quick, quiet and read-only: it runs behind whatever
    /// the user is doing. The default is to wait for the user to ask.
    /// </summary>
    CheckScope Scope => CheckScope.OnDemand;

    Task<DiagnosticResult> RunAsync(CancellationToken ct = default);
}
