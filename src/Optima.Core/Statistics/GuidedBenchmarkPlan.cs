namespace Optima.Core.Statistics;

public enum BenchmarkPlanState
{
    AwaitingNextRun,
    Running,
    Completed,
    Aborted,
}

public enum BenchmarkRunOutcome
{
    /// <summary>The run counted; the plan advanced.</summary>
    Accepted,
    /// <summary>The run produced no usable data and was re-queued for the same profile.</summary>
    Retry,
    /// <summary>Too many failed attempts; the plan gave up.</summary>
    Aborted,
}

public sealed record BenchmarkDrift(bool HasDrift, string Message)
{
    public static readonly BenchmarkDrift None = new(false, string.Empty);
}

/// <summary>
/// The guided benchmark state machine (§14), pure and UI-free. Runs alternate A,B,A,B so
/// time-of-day effects hit both profiles equally. A run only counts when the session
/// succeeded AND captured frames; failed runs re-queue (bounded). The configuration is
/// snapshotted at start, and any drift (tweaks toggled, a profile edited) aborts the plan:
/// a comparison across mixed configurations answers no question.
/// </summary>
public sealed class GuidedBenchmarkPlan
{
    private const int ExtraAttemptAllowance = 4;

    private readonly List<string> _runOrder = [];
    private readonly List<long> _acceptedA = [];
    private readonly List<long> _acceptedB = [];

    private IReadOnlyList<string> _baselineTweakIds = [];
    private string _baselineHashA = string.Empty;
    private string _baselineHashB = string.Empty;
    private int _attempts;

    public GuidedBenchmarkPlan(string profileA, string profileB, int runsPerProfile = 3)
    {
        if (runsPerProfile < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(runsPerProfile));
        }
        ProfileA = profileA;
        ProfileB = profileB;
        for (var i = 0; i < runsPerProfile; i++)
        {
            _runOrder.Add(profileA);
            _runOrder.Add(profileB);
        }
    }

    public string ProfileA { get; }
    public string ProfileB { get; }
    public BenchmarkPlanState State { get; private set; } = BenchmarkPlanState.AwaitingNextRun;
    public int TotalRuns => _runOrder.Count;
    public int CompletedRuns => _acceptedA.Count + _acceptedB.Count;
    public IReadOnlyList<long> AcceptedSessionIdsA => _acceptedA;
    public IReadOnlyList<long> AcceptedSessionIdsB => _acceptedB;

    /// <summary>The profile of the next run; null once the plan is finished.</summary>
    public string? NextProfileName
        => State is BenchmarkPlanState.Completed or BenchmarkPlanState.Aborted ? null : _runOrder[CompletedRuns];

    public string Progress => State switch
    {
        BenchmarkPlanState.Completed => $"all {TotalRuns} runs complete",
        BenchmarkPlanState.Aborted => "plan aborted",
        BenchmarkPlanState.Running => $"run {CompletedRuns + 1} of {TotalRuns} in progress · profile {NextProfileName}",
        _ => $"run {CompletedRuns + 1} of {TotalRuns} · next: {NextProfileName}",
    };

    /// <summary>Captures the configuration every run must keep.</summary>
    public void SetBaseline(IReadOnlyList<string> enabledTweakIds, string profileHashA, string profileHashB)
    {
        _baselineTweakIds = [.. enabledTweakIds];
        _baselineHashA = profileHashA;
        _baselineHashB = profileHashB;
    }

    /// <summary>Checked before every run; drifted configuration refuses the run and aborts.</summary>
    public BenchmarkDrift CheckDrift(IReadOnlyList<string> enabledTweakIds, string profileHashA, string profileHashB)
    {
        if (!_baselineTweakIds.SequenceEqual(enabledTweakIds, StringComparer.Ordinal))
        {
            var changed = _baselineTweakIds.Except(enabledTweakIds).Concat(enabledTweakIds.Except(_baselineTweakIds));
            return new BenchmarkDrift(true, $"tweak state changed: {string.Join(", ", changed)} · benchmark aborted");
        }
        if (!string.Equals(_baselineHashA, profileHashA, StringComparison.Ordinal))
        {
            return new BenchmarkDrift(true, $"profile '{ProfileA}' was edited mid-plan · benchmark aborted");
        }
        if (!string.Equals(_baselineHashB, profileHashB, StringComparison.Ordinal))
        {
            return new BenchmarkDrift(true, $"profile '{ProfileB}' was edited mid-plan · benchmark aborted");
        }
        return BenchmarkDrift.None;
    }

    public void BeginRun()
    {
        if (State != BenchmarkPlanState.AwaitingNextRun)
        {
            throw new InvalidOperationException($"Cannot begin a run in state {State}.");
        }
        State = BenchmarkPlanState.Running;
    }

    public BenchmarkRunOutcome ReportResult(bool success, bool hasData, long sessionId)
    {
        if (State != BenchmarkPlanState.Running)
        {
            throw new InvalidOperationException($"No run is in progress (state {State}).");
        }

        _attempts++;
        if (success && hasData)
        {
            var profile = _runOrder[CompletedRuns];
            (string.Equals(profile, ProfileA, StringComparison.OrdinalIgnoreCase) ? _acceptedA : _acceptedB).Add(sessionId);
            State = CompletedRuns >= TotalRuns ? BenchmarkPlanState.Completed : BenchmarkPlanState.AwaitingNextRun;
            return BenchmarkRunOutcome.Accepted;
        }

        if (_attempts >= TotalRuns + ExtraAttemptAllowance)
        {
            State = BenchmarkPlanState.Aborted;
            return BenchmarkRunOutcome.Aborted;
        }
        State = BenchmarkPlanState.AwaitingNextRun;
        return BenchmarkRunOutcome.Retry;
    }

    public void Abort() => State = BenchmarkPlanState.Aborted;
}
