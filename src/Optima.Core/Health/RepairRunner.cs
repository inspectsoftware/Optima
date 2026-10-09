using Microsoft.Extensions.Logging;
using Optima.Core.Models;

namespace Optima.Core.Health;

/// <summary>What the policy needs to know about the moment, asked for fresh at every decision.</summary>
public sealed record RepairEnvironment(
    AutoRepairMode Mode, bool GameRunning, bool WindowVisible, bool HelperConnected, bool ElevationDeclinedThisRun);

/// <summary>
/// Runs repairs: the ones the player asks for, and the ones <see cref="RepairPolicy"/> allows
/// unasked. One at a time, each written down before anything else happens, and each followed by
/// the check that proves whether it worked where the issue has one.
/// </summary>
public sealed class RepairRunner : IDisposable
{
    private readonly IssueEngine _issues;
    private readonly Dictionary<string, IRepairAction> _actions;
    private readonly Func<RepairEnvironment> _environment;
    private readonly Func<DateTimeOffset> _clock;
    private readonly Func<Issue, IRepairAction, CancellationToken, Task<bool>>? _announce;
    private readonly ILogger<RepairRunner> _logger;
    private readonly SemaphoreSlim _one = new(1, 1);
    private int _evaluating;
    private int _again;
    private bool _started;

    public RepairRunner(
        IssueEngine issues,
        IEnumerable<IRepairAction> actions,
        Func<RepairEnvironment> environment,
        ILogger<RepairRunner> logger,
        Func<DateTimeOffset>? clock = null,
        Func<Issue, IRepairAction, CancellationToken, Task<bool>>? announce = null)
    {
        _announce = announce;
        _issues = issues;
        _actions = actions.ToDictionary(a => a.Id, StringComparer.OrdinalIgnoreCase);
        _environment = environment;
        _logger = logger;
        _clock = clock ?? (() => DateTimeOffset.Now);
    }

    /// <summary>Raised after every repair that ran, asked for or not, on whatever thread ran it.</summary>
    public event Action<RepairAttempt>? Attempted;

    /// <summary>What a repair is called on its button; the id itself for one that is not registered here.</summary>
    public string TitleFor(string repairId) => _actions.TryGetValue(repairId, out var action) ? action.Title : repairId;

    /// <summary>The repairs an issue offers as buttons: its ladder, then the ones that are only ever run by hand.</summary>
    public IReadOnlyList<IRepairAction> ActionsFor(Issue issue)
        => RepairCatalog.For(issue.Code).All
            .Select(id => _actions.GetValueOrDefault(id))
            .OfType<IRepairAction>()
            .ToList();

    /// <summary>From now on every change to the issue list is looked at for something to repair.</summary>
    public void Start()
    {
        if (_started)
        {
            return;
        }
        _started = true;
        _issues.Changed += OnIssuesChanged;
    }

    private void OnIssuesChanged() => _ = Task.Run(() => EvaluateAsync());

    /// <summary>
    /// One pass over the open issues: for each with a ladder, ask the policy and do what it says.
    /// Also called when the moment changes without the list changing: the game closed, the window
    /// came back, the mode was switched.
    /// </summary>
    public async Task EvaluateAsync(CancellationToken ct = default)
    {
        // A pass that is asked for while one runs is not lost: the running one goes round again.
        if (Interlocked.Exchange(ref _evaluating, 1) == 1)
        {
            Volatile.Write(ref _again, 1);
            return;
        }
        try
        {
            do
            {
                Volatile.Write(ref _again, 0);
                foreach (var issue in _issues.Issues)
                {
                    if (issue.State is IssueState.Open or IssueState.NeedsUser)
                    {
                        await ConsiderAsync(issue, ct).ConfigureAwait(false);
                    }
                }
            }
            while (Volatile.Read(ref _again) == 1);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Looking for something to repair failed");
        }
        finally
        {
            Volatile.Write(ref _evaluating, 0);
            // Asked for between the loop's last look and the line above: nobody is running any
            // more, and the request would wait for whatever happens to ask next.
            if (Interlocked.Exchange(ref _again, 0) == 1 && !ct.IsCancellationRequested)
            {
                _ = Task.Run(() => EvaluateAsync(ct), CancellationToken.None);
            }
        }
    }

    private async Task ConsiderAsync(Issue issue, CancellationToken ct)
    {
        var ladder = RepairCatalog.For(issue.Code).Ladder
            .Select(id => _actions.GetValueOrDefault(id))
            .OfType<IRepairAction>()
            .Select(a => new RepairStep(a.Id, a.Tier))
            .ToList();
        if (ladder.Count == 0)
        {
            return;
        }

        var environment = _environment();
        var now = _clock();
        var all = _issues.Attempts;
        var unasked = all.Where(a => a.Trigger == RepairTrigger.Background).ToList();
        var decision = RepairPolicy.Decide(new RepairRequest
        {
            Ladder = ladder,
            History = all.Where(a => a.IssueKey == issue.Key).ToList(),
            Mode = environment.Mode,
            Now = now,
            GameRunning = environment.GameRunning,
            WindowVisible = environment.WindowVisible,
            HelperConnected = environment.HelperConnected,
            ElevationDeclinedThisRun = environment.ElevationDeclinedThisRun,
            AutomaticRepairsLastHour = unasked.Count(a => now - a.At < TimeSpan.FromHours(1)),
            LastDisruptiveAt = unasked.Where(a => a.Tier != RepairTier.Safe).Select(a => (DateTimeOffset?)a.At).Max(),
        });

        switch (decision.Verdict)
        {
            case RepairVerdict.Run when decision.Step is { } step:
                var action = _actions[step.Id];
                if (action.Tier != RepairTier.Safe && !await AnnounceAsync(issue, action, ct).ConfigureAwait(false))
                {
                    break;
                }
                await RunCoreAsync(issue, action, RepairTrigger.Background, ct).ConfigureAwait(false);
                break;
            case RepairVerdict.AskUser:
                _issues.SetState(issue.Key, IssueState.NeedsUser, decision.Reason);
                break;
            case RepairVerdict.Defer:
                _issues.SetState(issue.Key, IssueState.Open, decision.Reason);
                break;
        }
    }

    /// <summary>
    /// A repair that interrupts or asks for administrator rights says so before it runs, and the
    /// player gets a few seconds to stop it. Stopping it is an answer, and it is kept: the attempt
    /// is written down as left to the player, which ends the ladder until they press the button.
    /// </summary>
    /// <returns>True when the repair should go ahead.</returns>
    private async Task<bool> AnnounceAsync(Issue issue, IRepairAction action, CancellationToken ct)
    {
        if (_announce is null)
        {
            return true;
        }
        if (!await _announce(issue, action, ct).ConfigureAwait(false))
        {
            await _issues.RecordAttemptAsync(new RepairAttempt(
                issue.Key, action.Id, action.Tier, RepairTrigger.Background, _clock(), RepairOutcome.NeedsUser,
                "You stopped it before it ran.")).ConfigureAwait(false);
            _issues.SetState(issue.Key, IssueState.NeedsUser, $"You stopped \"{action.Title}\". It is on the card when you want it.");
            return false;
        }
        // A few seconds passed. If a game started in them, the repair waits like any other.
        if (_environment().GameRunning)
        {
            _issues.SetState(issue.Key, IssueState.Open, "waiting for the game to close: nothing is repaired while it runs");
            return false;
        }
        return true;
    }

    /// <summary>Runs a repair the player asked for. No policy: the click is the permission.</summary>
    public Task<RepairResult> RunAsync(string issueKey, string repairId, CancellationToken ct = default)
    {
        var issue = _issues.Issues.FirstOrDefault(i => i.Key == issueKey);
        if (issue is null || !_actions.TryGetValue(repairId, out var action))
        {
            return Task.FromResult(new RepairResult(RepairOutcome.NotNeeded, "The issue is no longer on the list."));
        }
        return RunCoreAsync(issue, action, RepairTrigger.User, ct);
    }

    private async Task<RepairResult> RunCoreAsync(Issue issue, IRepairAction action, RepairTrigger trigger, CancellationToken ct)
    {
        await _one.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            _issues.SetState(issue.Key, IssueState.Repairing, action.Title + "…");

            RepairResult result;
            try
            {
                result = await action.RunAsync(issue, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Nothing looks at an issue marked as being repaired, so one left that way by a
                // cancel would say "repairing" for good.
                _issues.SetState(issue.Key, IssueState.Open, "the repair was cancelled");
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Repair {Repair} threw", action.Id);
                result = new RepairResult(RepairOutcome.Failed, ExceptionDetail.Capture(ex).Summary);
            }

            var attempt = new RepairAttempt(issue.Key, action.Id, action.Tier, trigger, _clock(), result.Outcome, result.Summary);
            await _issues.RecordAttemptAsync(attempt).ConfigureAwait(false);
            _logger.LogInformation("Repair {Repair} for {Code} ({Trigger}): {Outcome}. {Summary}",
                action.Id, issue.Code, trigger.ToString(), result.Outcome.ToString(), result.Summary);

            var state = result.Outcome switch
            {
                RepairOutcome.Fixed or RepairOutcome.NotNeeded => IssueState.Repaired,
                RepairOutcome.NeedsUser => IssueState.NeedsUser,
                _ => IssueState.Open,
            };
            var stillThere = false;
            if (result.Outcome == RepairOutcome.NotNeeded && RepairCatalog.For(issue.Code).VerifyCheck is null)
            {
                // "Nothing needed doing" changed nothing, and with no check to ask there is no
                // reason to call the issue repaired. It stays open for the next step of the ladder.
                state = IssueState.Open;
            }
            if (state == IssueState.Repaired && RepairCatalog.For(issue.Code).VerifyCheck is { } check)
            {
                // The repair's own word is not the proof; the check that raised the issue is.
                var verified = await _issues.RunCheckAsync(check, ct).ConfigureAwait(false);
                stillThere = verified is { Status: DiagnosticStatus.Warning or DiagnosticStatus.Fail };
                if (stillThere)
                {
                    state = IssueState.Open;
                }
            }

            _issues.SetState(issue.Key, state, Describe(action, trigger, result, stillThere));
            try
            {
                Attempted?.Invoke(attempt);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "A repair listener failed");
            }
            return result;
        }
        finally
        {
            _one.Release();
        }
    }

    private string Describe(IRepairAction action, RepairTrigger trigger, RepairResult result, bool stillThere)
    {
        var who = trigger == RepairTrigger.Background ? "Optima ran" : "You ran";
        var at = _clock().ToLocalTime().ToString("HH:mm");
        var outcome = result.Outcome switch
        {
            RepairOutcome.Fixed when stillThere => "it ran, and the check still fails",
            RepairOutcome.Fixed => "done",
            RepairOutcome.NotNeeded => "nothing needed doing",
            RepairOutcome.NeedsUser => "the rest is yours",
            _ => "it did not work",
        };
        return $"{who} \"{action.Title}\" at {at}: {outcome}. {result.Summary}".TrimEnd();
    }

    public void Dispose()
    {
        if (_started)
        {
            _issues.Changed -= OnIssuesChanged;
        }
        _one.Dispose();
    }
}
