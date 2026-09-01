using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Optima.Core.Abstractions;
using Optima.Core.Configuration;
using Optima.Core.Launch;
using Optima.Core.Models;
using Optima.Core.Statistics;
using Microsoft.Extensions.Logging;

namespace Optima.App.ViewModels;

/// <summary>
/// The guided benchmark flow on the SESSIONS page (§14): "compare A vs B over N runs each",
/// driving real sessions through the orchestrator with alternating profiles. Each run starts
/// on an explicit click (a run ends when the user quits the game; relaunching it unasked
/// would be hostile), configuration drift aborts the plan, and the result leads with the
/// per-run Welch verdict since pooled per-second samples overstate significance.
/// </summary>
public sealed partial class GuidedBenchmarkViewModel : ObservableObject
{
    private readonly LaunchOrchestrator _orchestrator;
    private readonly ProfileService _profiles;
    private readonly ITweakService _tweaks;
    private readonly ISessionStore _sessions;
    private readonly ILogger<GuidedBenchmarkViewModel> _logger;

    private GuidedBenchmarkPlan? _plan;
    private CancellationTokenSource? _cts;
    private bool _cancelRequested;

    public GuidedBenchmarkViewModel(
        LaunchOrchestrator orchestrator,
        ProfileService profiles,
        ITweakService tweaks,
        ISessionStore sessions,
        ILogger<GuidedBenchmarkViewModel> logger)
    {
        _orchestrator = orchestrator;
        _profiles = profiles;
        _tweaks = tweaks;
        _sessions = sessions;
        _logger = logger;
    }

    public IReadOnlyList<int> RunOptions { get; } = [3, 5, 7];

    [ObservableProperty] private LaunchProfile? _profileA;
    [ObservableProperty] private LaunchProfile? _profileB;
    [ObservableProperty] private int _runsPerProfile = 3;
    [ObservableProperty] private bool _planActive;
    [ObservableProperty] private bool _canRunNext;
    [ObservableProperty] private bool _runInProgress;
    [ObservableProperty] private string _progress = string.Empty;
    [ObservableProperty] private string _status = string.Empty;
    [ObservableProperty] private PerRunComparison? _perRunResult;
    [ObservableProperty] private BenchmarkComparison? _pooledResult;

    public ObservableCollection<string> RunLog { get; } = [];

    [RelayCommand]
    private async Task StartPlanAsync()
    {
        if (ProfileA is null || ProfileB is null)
        {
            Status = "pick both profiles first";
            return;
        }
        if (string.Equals(ProfileA.Name, ProfileB.Name, StringComparison.OrdinalIgnoreCase))
        {
            Status = "pick two different profiles";
            return;
        }

        try
        {
            _plan = new GuidedBenchmarkPlan(ProfileA.Name, ProfileB.Name, RunsPerProfile);
            _plan.SetBaseline(
                await GetEnabledTweakIdsAsync(),
                LaunchProfileHasher.ComputeHash(ProfileA),
                LaunchProfileHasher.ComputeHash(ProfileB));
            _cts = new CancellationTokenSource();
            _cancelRequested = false;

            RunLog.Clear();
            PerRunResult = null;
            PooledResult = null;
            PlanActive = true;
            CanRunNext = true;
            RunInProgress = false;
            Progress = _plan.Progress;
            Status = "plan ready · every run launches the game, plays until you quit it, then counts";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Starting the guided benchmark failed");
            Status = "could not start the plan · see logs";
        }
    }

    [RelayCommand]
    private async Task RunNextAsync()
    {
        if (_plan is null || _plan.State != BenchmarkPlanState.AwaitingNextRun || _cts is null)
        {
            return;
        }

        try
        {
            // Refuse to run under a drifted configuration; a mixed comparison answers nothing.
            var currentA = await _profiles.GetProfileAsync(_plan.ProfileA);
            var currentB = await _profiles.GetProfileAsync(_plan.ProfileB);
            var drift = _plan.CheckDrift(
                await GetEnabledTweakIdsAsync(),
                LaunchProfileHasher.ComputeHash(currentA),
                LaunchProfileHasher.ComputeHash(currentB));
            if (drift.HasDrift)
            {
                _plan.Abort();
                FinishPlanUi();
                Status = $"FAIL · {drift.Message}";
                return;
            }

            var profileName = _plan.NextProfileName!;
            var profile = await _profiles.GetProfileAsync(profileName);
            _plan.BeginRun();
            CanRunNext = false;
            RunInProgress = true;
            Progress = _plan.Progress;
            Status = "session running · quit the game when the round is done";

            var runNumber = _plan.CompletedRuns + 1;
            var result = await Task.Run(() => _orchestrator.RunSessionAsync(profile, LaunchKind.Benchmark, _cts.Token));

            var outcome = _plan.ReportResult(
                result.Success,
                result.Session?.Stats.HasData == true,
                result.Session?.Id ?? 0);

            RunLog.Add(outcome switch
            {
                BenchmarkRunOutcome.Accepted =>
                    $"OK · run {runNumber} · {profileName} · {result.Session!.Stats.AverageFps:F0} fps avg",
                BenchmarkRunOutcome.Retry =>
                    $"RETRY · run {runNumber} · {profileName} · {(result.Success ? "no fps data captured" : result.Error?.Title ?? "failed")} · re-queued",
                _ =>
                    $"FAIL · run {runNumber} · {profileName} · too many failed attempts",
            });

            if (_cancelRequested && _plan.State != BenchmarkPlanState.Completed)
            {
                _plan.Abort();
            }

            switch (_plan.State)
            {
                case BenchmarkPlanState.Completed:
                    await ShowResultsAsync();
                    FinishPlanUi();
                    Status = "plan complete";
                    break;
                case BenchmarkPlanState.Aborted:
                    FinishPlanUi();
                    Status = _cancelRequested ? "plan cancelled · all settings were restored" : "plan aborted";
                    break;
                default:
                    CanRunNext = true;
                    RunInProgress = false;
                    Progress = _plan.Progress;
                    Status = "ready for the next run";
                    break;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Guided benchmark run failed");
            _plan?.Abort();
            FinishPlanUi();
            Status = "the run failed unexpectedly · see logs";
        }
    }

    [RelayCommand]
    private void CancelPlan()
    {
        if (_plan is null)
        {
            return;
        }
        _cancelRequested = true;
        if (_plan.State == BenchmarkPlanState.Running)
        {
            // The active session is cancelled and restored; RunNextAsync finishes the plan.
            _cts?.Cancel();
            Status = "cancelling · restoring settings";
            return;
        }
        _plan.Abort();
        FinishPlanUi();
        Status = "plan cancelled";
    }

    private async Task ShowResultsAsync()
    {
        var sessionsA = await _sessions.GetSessionsByIdsAsync(_plan!.AcceptedSessionIdsA);
        var sessionsB = await _sessions.GetSessionsByIdsAsync(_plan.AcceptedSessionIdsB);
        PerRunResult = BenchmarkComparer.ComparePerRun(_plan.ProfileA, sessionsA, _plan.ProfileB, sessionsB);
        PooledResult = BenchmarkComparer.Compare(_plan.ProfileA, sessionsA, _plan.ProfileB, sessionsB);
    }

    private void FinishPlanUi()
    {
        PlanActive = false;
        CanRunNext = false;
        RunInProgress = false;
        Progress = _plan?.Progress ?? string.Empty;
        _cts?.Dispose();
        _cts = null;
    }

    private async Task<IReadOnlyList<string>> GetEnabledTweakIdsAsync()
    {
        var states = await _tweaks.GetStatesAsync();
        return states
            .Where(s => s.Status == TweakStatus.Enabled)
            .Select(s => s.Definition.Id)
            .OrderBy(id => id, StringComparer.Ordinal)
            .ToList();
    }
}
