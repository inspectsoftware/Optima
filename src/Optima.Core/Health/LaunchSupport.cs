using Microsoft.Extensions.Logging;
using Optima.Core.Abstractions;
using Optima.Core.Launch;
using Optima.Core.Models;

namespace Optima.Core.Health;

/// <summary>
/// The launch's side of the debugging center: the quick checks before a session, and one
/// repair-and-retry for a step that failed with a fault a repair is known for.
/// </summary>
public sealed class LaunchSupport : ILaunchSupport
{
    public const string ReloadDriverRepair = "reload-display-driver";

    private readonly IssueEngine _issues;
    private readonly IVirtualDisplayMaintenance _display;
    private readonly ILogger<LaunchSupport> _logger;
    private readonly TimeSpan _preflightBudget;
    private readonly Func<DateTimeOffset> _clock;

    /// <param name="preflightBudget">How long the checks before a launch may take in all. The launch does not wait longer.</param>
    public LaunchSupport(
        IssueEngine issues,
        IVirtualDisplayMaintenance display,
        ILogger<LaunchSupport> logger,
        TimeSpan? preflightBudget = null,
        Func<DateTimeOffset>? clock = null)
    {
        _issues = issues;
        _display = display;
        _logger = logger;
        _preflightBudget = preflightBudget ?? TimeSpan.FromSeconds(3);
        _clock = clock ?? (() => DateTimeOffset.Now);
    }

    public async Task PreflightAsync(CancellationToken ct = default)
    {
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(ct);
        budget.CancelAfter(_preflightBudget);
        try
        {
            await _issues.ScanAsync(CheckScope.Preflight, budget.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // The player pressed PLAY; the list being a check short is better than the game being late.
            _logger.LogDebug("The checks before the launch ran out of time and were left behind");
        }
    }

    public async Task<bool> TryRepairAsync(string code, CancellationToken ct = default)
    {
        if (!string.Equals(code, "VDD_NO_DISPLAY", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        RepairResult result;
        try
        {
            await _display.ReloadDriverAsync(ct).ConfigureAwait(false);
            result = new RepairResult(RepairOutcome.Fixed,
                "The virtual display did not appear, so the driver was told to reload its settings; the step is being tried again.");
        }
        catch (OptimaException ex)
        {
            // The reload has faults of its own (the pipe refused, the prompt declined). Then the
            // step keeps the error it failed with, which is the one that names the real problem.
            _logger.LogWarning(ex, "Reloading the virtual display driver failed ({Code}); the launch step is not retried", ex.Error.Code);
            result = new RepairResult(RepairOutcome.Failed, ex.Error.Title);
        }

        // Written down like any other repair, so it shows in the history: the launch that worked
        // on its second try should not look, afterwards, like a launch where nothing happened.
        await _issues.RecordAttemptAsync(new RepairAttempt(
            code, ReloadDriverRepair, RepairTier.Elevated, RepairTrigger.Launch, _clock(), result.Outcome, result.Summary))
            .ConfigureAwait(false);
        _logger.LogInformation("Repair {Repair} for {Code} (launch): {Outcome}. {Summary}",
            ReloadDriverRepair, code, result.Outcome.ToString(), result.Summary);
        return result.Outcome == RepairOutcome.Fixed;
    }
}
