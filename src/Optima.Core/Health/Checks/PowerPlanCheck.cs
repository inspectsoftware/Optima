using Optima.Core.Abstractions;
using Optima.Core.Configuration;
using Optima.Core.Launch;
using Optima.Core.Models;

namespace Optima.Core.Health.Checks;

/// <summary>
/// Whether this PC offers the power plan the selected profile asks for. A launch finds this out
/// too, and carries on without the plan; the check says it before the launch, so the notice after
/// a session is not the first the player hears of it.
/// </summary>
public sealed class PowerPlanCheck : IDiagnosticCheck
{
    private readonly ProfileService _profiles;
    private readonly SettingsService _settings;
    private readonly IPowerProfileService _power;

    public PowerPlanCheck(ProfileService profiles, SettingsService settings, IPowerProfileService power)
    {
        _profiles = profiles;
        _settings = settings;
        _power = power;
    }

    public string Name => "Power Plan";
    public int Order => 45;
    public CheckScope Scope => CheckScope.Startup | CheckScope.Preflight;

    public async Task<DiagnosticResult> RunAsync(CancellationToken ct = default)
    {
        var settings = await _settings.GetSettingsAsync(ct).ConfigureAwait(false);
        var profile = await _profiles.GetProfileAsync(settings.SelectedProfileName, ct).ConfigureAwait(false);
        var kind = profile.Performance.PowerPlan;
        if (kind == PowerPlanKind.Unchanged)
        {
            return Result(DiagnosticStatus.Pass, $"The profile {profile.Name} leaves the power plan as it is.");
        }

        var listed = await _power.ListSchemesAsync(ct).ConfigureAwait(false);
        if (PowerPlanPolicy.ResolveNearest(kind, listed) is { } plan)
        {
            var name = listed.First(s => s.Id == plan).Name;
            return Result(DiagnosticStatus.Pass, $"The profile {profile.Name} asks for {Describe(kind)}; this PC offers {name}.");
        }

        if (kind == PowerPlanKind.UltimatePerformance)
        {
            // Only trying tells whether Windows will accept and list a copy of the plan, and trying
            // changes the power settings. That belongs to a launch, not to a background check.
            return new DiagnosticResult
            {
                CheckName = Name,
                Status = DiagnosticStatus.Pass,
                Reason = "Ultimate Performance is not listed yet. Optima adds its own copy at launch where Windows allows one.",
            };
        }

        var names = listed.Count == 0 ? "none" : string.Join(", ", listed.Select(s => s.Name));
        return new DiagnosticResult
        {
            CheckName = Name,
            Status = DiagnosticStatus.Warning,
            Reason = $"The profile {profile.Name} asks for the {Describe(kind)} power plan, which this PC does not offer. It lists: {names}.",
            RecommendedFix = "Nothing needs repairing: the game starts on the plan that is already active. A profile whose power plan is Unchanged stops the notice.",
            IssueCode = "POWER_PLAN_UNAVAILABLE",
        };
    }

    private DiagnosticResult Result(DiagnosticStatus status, string reason) => new()
    {
        CheckName = Name,
        Status = status,
        Reason = reason,
        IssueCode = "POWER_PLAN_UNAVAILABLE",
    };

    private static string Describe(PowerPlanKind kind) => kind switch
    {
        PowerPlanKind.HighPerformance => "High performance",
        PowerPlanKind.UltimatePerformance => "Ultimate Performance",
        _ => kind.ToString(),
    };
}
