using System.ComponentModel;
using Optima.Core.Abstractions;
using Optima.Core.Launch;
using Optima.Core.Models;
using Optima.Platform.Windows.NativeMethods;
using Microsoft.Extensions.Logging;

namespace Optima.Platform.Windows.Services;

/// <summary>Active power scheme management via powrprof.dll (no elevation required).</summary>
public sealed class WindowsPowerProfileService : IPowerProfileService
{
    private readonly ILogger<WindowsPowerProfileService> _logger;

    public WindowsPowerProfileService(ILogger<WindowsPowerProfileService> logger)
    {
        _logger = logger;
    }

    public Task<Guid> GetActiveSchemeAsync(CancellationToken ct = default)
        => Task.Run(PowerNative.GetActiveScheme, ct);

    public Task<string> GetSchemeNameAsync(Guid scheme, CancellationToken ct = default)
        => Task.Run(() => PowerNative.GetFriendlyName(scheme), ct);

    public Task<IReadOnlyList<PowerScheme>> ListSchemesAsync(CancellationToken ct = default)
        => Task.Run(PowerNative.ListSchemes, ct);

    public Task<Guid> ApplyAsync(PowerPlanKind kind, CancellationToken ct = default)
        => Task.Run(() =>
        {
            var previous = PowerNative.GetActiveScheme();
            if (kind == PowerPlanKind.Unchanged)
            {
                return previous;
            }

            var target = ResolveListed(kind);
            if (target == previous)
            {
                return previous;
            }

            try
            {
                PowerNative.SetActiveScheme(target);
            }
            catch (Win32Exception ex)
            {
                throw OptimaException.From(
                    "POWER_PLAN_REFUSED",
                    "Windows refused the power plan change",
                    $"Windows lists the {PowerNative.GetFriendlyName(target)} plan on this PC but would not make it active. "
                        + "The plan that was active before is still active.",
                    ex,
                    "Check whether a company policy or a vendor power tool manages the power plan on this PC",
                    "Switch to the plan once in Windows' own power settings to see whether Windows allows it at all",
                    "To stop the notice, use a profile whose power plan is Unchanged; the Performance page can save a copy of this one");
            }

            // Not thrown: the switch was asked for, so the caller must still get the previous plan
            // to put back, whatever Windows made of the request.
            var active = PowerNative.GetActiveScheme();
            if (active != target)
            {
                _logger.LogWarning("Windows accepted the switch to {Plan} ({Guid}) and kept {Active} active",
                    PowerNative.GetFriendlyName(target), target, PowerNative.GetFriendlyName(active));
            }
            else
            {
                _logger.LogInformation("Power plan switched to {Plan} ({Guid}); previous was {Previous}",
                    PowerNative.GetFriendlyName(target), target, previous);
            }
            return previous;
        }, ct);

    public Task RestoreAsync(Guid previousScheme, CancellationToken ct = default)
        => Task.Run(() =>
        {
            PowerNative.SetActiveScheme(previousScheme);
            _logger.LogInformation("Power plan restored to {Plan}", PowerNative.GetFriendlyName(previousScheme));
        }, ct);

    /// <summary>
    /// The plan to activate for the choice, taken from what Windows lists. A choice this PC does not
    /// offer is a typed error with the listed plans in it, never a guess at a well-known GUID.
    /// </summary>
    private Guid ResolveListed(PowerPlanKind kind)
    {
        var listed = PowerNative.ListSchemes();
        if (PowerPlanPolicy.Resolve(kind, listed) is { } exact)
        {
            return exact;
        }

        if (kind == PowerPlanKind.UltimatePerformance)
        {
            if (PowerNative.TryCreateUltimatePerformance())
            {
                return PowerPlanPolicy.OptimaUltimatePerformance;
            }
            if (PowerPlanPolicy.ResolveNearest(kind, listed) is { } nearest)
            {
                _logger.LogInformation("Ultimate Performance is not offered on this PC; using {Plan} instead",
                    PowerNative.GetFriendlyName(nearest));
                return nearest;
            }
        }

        var names = listed.Count == 0 ? "none" : string.Join(", ", listed.Select(s => s.Name));
        throw OptimaException.From(
            "POWER_PLAN_UNAVAILABLE",
            "This PC does not offer that power plan",
            $"The profile asks for the {Describe(kind)} power plan and Windows does not list it on this PC. "
                + $"The plans it lists are: {names}. The plan that was active before is still active.",
            null,
            "Nothing needs repairing: the session runs on the plan that is already active",
            "To stop the notice, use a profile whose power plan is Unchanged; the Performance page can save a copy of this one",
            "A PC with Modern Standby only offers Balanced and its vendor's own plans; that is Windows, not a fault");
    }

    private static string Describe(PowerPlanKind kind) => kind switch
    {
        PowerPlanKind.HighPerformance => "High performance",
        PowerPlanKind.UltimatePerformance => "Ultimate Performance",
        _ => kind.ToString(),
    };
}
