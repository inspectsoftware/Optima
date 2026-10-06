using Optima.Core.Health;

namespace Optima.App.Services;

/// <summary>
/// Turns on the Windows hypervisor features Google Play Games needs, through the elevated helper:
/// the same fix the first-run wizard runs. It needs an administrator prompt and, afterwards, a
/// restart of the PC, so it is only ever started by a click.
/// </summary>
public sealed class EnableHypervisorRepair : IRepairAction
{
    private readonly FirstRunFixService _fix;

    public EnableHypervisorRepair(FirstRunFixService fix) => _fix = fix;

    public string Id => "enable-hypervisor";
    public string Title => "enable the Windows hypervisor features";
    public string Changes => "Turns on Windows Hypervisor Platform and Virtual Machine Platform. Asks for administrator rights, takes a few minutes, and needs a restart of the PC to take effect.";
    public RepairTier Tier => RepairTier.Elevated;

    public async Task<RepairResult> RunAsync(Issue issue, CancellationToken ct = default)
    {
        // Only the hypervisor part of the wizard's fix: nothing here should open a download page.
        var result = await _fix.RunFixesAsync(new ReadinessReport(
            FirmwareVirtualizationOff: false, HypervisorFeaturesMissing: true, GpgMissing: false), ct);
        var summary = string.Join(" ", result.Log);

        if (result.Log.Any(line => line.Contains("declined", StringComparison.OrdinalIgnoreCase)))
        {
            return new RepairResult(RepairOutcome.NeedsUser, "The administrator prompt was declined, so nothing was changed.");
        }
        if (result.Log.Any(line => line.Contains("failed", StringComparison.OrdinalIgnoreCase)))
        {
            return new RepairResult(RepairOutcome.Failed, summary);
        }
        return result.RestartRequired
            ? new RepairResult(RepairOutcome.NeedsUser, summary + " Restart the PC to finish.")
            : new RepairResult(RepairOutcome.Fixed, summary);
    }
}

/// <summary>
/// What the repair policy needs to know about the window, kept where any thread can read it. The
/// window itself belongs to the UI thread and cannot be asked from the thread a repair decision
/// is made on.
/// </summary>
public static class RepairMoment
{
    private static volatile bool _windowVisible;

    /// <summary>The main window is on screen: not hidden in the tray, not minimized.</summary>
    public static bool WindowVisible
    {
        get => _windowVisible;
        set => _windowVisible = value;
    }
}
