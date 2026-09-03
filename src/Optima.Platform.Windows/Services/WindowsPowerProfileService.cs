using Optima.Core.Abstractions;
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

    public Task<Guid> ApplyAsync(PowerPlanKind kind, CancellationToken ct = default)
        => Task.Run(() =>
        {
            var previous = PowerNative.GetActiveScheme();
            var target = kind switch
            {
                PowerPlanKind.Balanced => PowerNative.BalancedScheme,
                PowerPlanKind.HighPerformance => ResolveHighPerformance(),
                PowerPlanKind.UltimatePerformance => PowerNative.EnsureUltimatePerformance(),
                _ => previous,
            };

            if (target != previous)
            {
                PowerNative.SetActiveScheme(target);
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

    private static Guid ResolveHighPerformance()
    {
        var schemes = PowerNative.EnumerateSchemes();
        if (schemes.Contains(PowerNative.HighPerformanceScheme))
        {
            return PowerNative.HighPerformanceScheme;
        }
        foreach (var scheme in schemes)
        {
            if (PowerNative.GetFriendlyName(scheme).Contains("high performance", StringComparison.OrdinalIgnoreCase))
            {
                return scheme;
            }
        }
        return PowerNative.HighPerformanceScheme;
    }
}
