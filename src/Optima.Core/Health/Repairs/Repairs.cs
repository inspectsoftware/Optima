using Optima.Core.Abstractions;
using Optima.Core.Configuration;

namespace Optima.Core.Health.Repairs;

/// <summary>
/// Starts Google Play Games when it is not running. It is what the error guide tells the player to
/// do after a failed launch, and it does nothing at all when the platform is already up.
/// </summary>
public sealed class StartPlatformRepair : IRepairAction
{
    private readonly IPlatformControl _platform;

    public StartPlatformRepair(IPlatformControl platform) => _platform = platform;

    public string Id => "start-platform";
    public string Title => "start Google Play Games";
    public string Changes => "Starts Google Play Games if it is not running. Nothing is stopped or closed.";
    public RepairTier Tier => RepairTier.Safe;

    public async Task<RepairResult> RunAsync(Issue issue, CancellationToken ct = default)
    {
        var running = await _platform.CountRunningAsync(ct).ConfigureAwait(false);
        if (running > 0)
        {
            return new RepairResult(RepairOutcome.NotNeeded, $"Google Play Games is already running ({running} processes).");
        }
        return await _platform.StartAsync(ct).ConfigureAwait(false)
            ? new RepairResult(RepairOutcome.Fixed, "Google Play Games was not running and has been started.")
            : new RepairResult(RepairOutcome.Failed, "Google Play Games was not found, so there was nothing to start.");
    }
}

/// <summary>
/// Stops Google Play Games and starts it again. It ends a running game with it, which is why it is
/// never on a ladder that runs while one is on, and never automatic unless the player chose that.
/// </summary>
public sealed class RestartPlatformRepair : IRepairAction
{
    private readonly IPlatformControl _platform;
    private readonly TimeSpan _settle;

    /// <param name="settle">How long the platform gets to finish stopping before it is started again.</param>
    public RestartPlatformRepair(IPlatformControl platform, TimeSpan? settle = null)
    {
        _platform = platform;
        _settle = settle ?? TimeSpan.FromMilliseconds(1500);
    }

    public string Id => "restart-platform";
    public string Title => "restart Google Play Games";
    public string Changes => "Stops every Google Play Games process and starts it again. A running game is closed with it.";
    public RepairTier Tier => RepairTier.Disruptive;

    public async Task<RepairResult> RunAsync(Issue issue, CancellationToken ct = default)
    {
        var stopped = await _platform.StopAsync(ct).ConfigureAwait(false);
        await Task.Delay(_settle, ct).ConfigureAwait(false);
        return await _platform.StartAsync(ct).ConfigureAwait(false)
            ? new RepairResult(RepairOutcome.Fixed, $"Stopped {stopped} process(es) and started Google Play Games again.")
            : new RepairResult(RepairOutcome.Failed, $"Stopped {stopped} process(es); Google Play Games was not found to start again.");
    }
}

/// <summary>
/// Runs what is left of a restore that did not finish. The steps that failed are kept on disk, so
/// this puts back exactly those and nothing that was already put back.
/// </summary>
public sealed class RetryRestoreRepair : IRepairAction
{
    private readonly IRecoveryService _recovery;

    public RetryRestoreRepair(IRecoveryService recovery) => _recovery = recovery;

    public string Id => "retry-restore";
    public string Title => "try the restore again";
    public string Changes => "Puts back the system settings a session changed and could not restore: display layout, power plan, the virtual display. Switching the virtual display off can ask for administrator rights.";

    // The display steps go through the elevated helper when the direct way is refused.
    public RepairTier Tier => RepairTier.Elevated;

    public async Task<RepairResult> RunAsync(Issue issue, CancellationToken ct = default)
    {
        var pending = await _recovery.GetPendingAsync(ct).ConfigureAwait(false);
        if (pending is null)
        {
            return new RepairResult(RepairOutcome.NotNeeded, "Nothing is waiting to be restored.");
        }
        var report = await _recovery.RestoreAsync(pending, ct).ConfigureAwait(false);
        return report.AllRestored
            ? new RepairResult(RepairOutcome.Fixed, "Everything that was left has been put back.")
            : new RepairResult(RepairOutcome.Failed, "Still not restored: " + string.Join(", ", report.Failed) + ".");
    }
}

/// <summary>
/// Drops a pending restore of the virtual display's settings file: the file stays as it is now.
/// Only ever run by hand, because the other answer, restoring the old backup, is just as valid and
/// only the player knows which one is right. The backup itself is not deleted.
/// </summary>
public sealed class DiscardDisplayRestoreRepair : IRepairAction
{
    private readonly AppPaths _paths;

    public DiscardDisplayRestoreRepair(AppPaths paths) => _paths = paths;

    public string Id => "discard-display-restore";
    public string Title => "keep the settings file as it is";
    public string Changes => "Deletes the marker, so the next virtual display session leaves the settings file alone. The backup stays in the backups folder.";
    public RepairTier Tier => RepairTier.Safe;

    public Task<RepairResult> RunAsync(Issue issue, CancellationToken ct = default)
    {
        if (!File.Exists(_paths.VddRestoreMarkerFile))
        {
            return Task.FromResult(new RepairResult(RepairOutcome.NotNeeded, "No restore is pending any more."));
        }
        File.Delete(_paths.VddRestoreMarkerFile);
        return Task.FromResult(new RepairResult(RepairOutcome.Fixed,
            "The marker is gone and the settings file is untouched. The backup is still in " + _paths.BackupsDirectory + "."));
    }
}
