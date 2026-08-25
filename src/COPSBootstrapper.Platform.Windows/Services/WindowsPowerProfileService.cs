using COPSBootstrapper.Core.Abstractions;
using COPSBootstrapper.Core.Models;
using COPSBootstrapper.Platform.Windows.NativeMethods;
using Microsoft.Extensions.Logging;

namespace COPSBootstrapper.Platform.Windows.Services;

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

    /// <summary>High Performance can be hidden on modern-standby machines; fall back to any listed scheme with that name.</summary>
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
        // Nothing matching — the built-in GUID still activates the hidden scheme on most systems.
        return PowerNative.HighPerformanceScheme;
    }
}
