namespace Optima.Core.Statistics;

/// <summary>One published sample window: the dominant presenter's fps and average frametime.</summary>
public sealed record PresentWindowSample(double Fps, double AverageFrametimeMs, int ProcessId);

/// <summary>Everything accumulated over a capture: the dominant presenter's frametimes plus per-window fps.</summary>
public sealed record PresentCaptureResult(
    IReadOnlyList<double> FrametimesMs,
    IReadOnlyList<double> FpsSamples,
    int DominantProcessId);

/// <summary>Pure present-event bookkeeping for the ETW collector (§12/§13).</summary>
public sealed class PresentWindowAggregator
{
    private const double MinDeltaMs = 0.05;
    private const double MaxDeltaMs = 2000;

    private sealed class PidState
    {
        public double LastPresentMs = -1;
        public int PresentsInWindow;
        public double FrametimeSumInWindow;
        public long TotalPresents;
        public List<double> FrametimesMs { get; } = [];
    }

    private readonly Dictionary<int, PidState> _candidates;
    private readonly double _intervalMs;
    private readonly List<double> _fpsSamples = [];

    public PresentWindowAggregator(IReadOnlyCollection<int> candidateProcessIds, double intervalMs = 1000)
    {
        if (candidateProcessIds.Count == 0)
        {
            throw new ArgumentException("At least one candidate process id is required.", nameof(candidateProcessIds));
        }
        _candidates = candidateProcessIds.Distinct().ToDictionary(pid => pid, _ => new PidState());
        _intervalMs = intervalMs;
    }

    public void RecordPresent(int processId, double timestampMs)
    {
        if (!_candidates.TryGetValue(processId, out var state))
        {
            return;
        }
        if (state.LastPresentMs >= 0)
        {
            var delta = timestampMs - state.LastPresentMs;
            if (delta > MinDeltaMs && delta < MaxDeltaMs)
            {
                state.FrametimesMs.Add(delta);
                state.PresentsInWindow++;
                state.FrametimeSumInWindow += delta;
                state.TotalPresents++;
            }
        }
        state.LastPresentMs = timestampMs;
    }

    public PresentWindowSample? CompleteWindow()
    {
        PidState? best = null;
        var bestPid = 0;
        foreach (var (pid, state) in _candidates)
        {
            if (state.PresentsInWindow > (best?.PresentsInWindow ?? 0))
            {
                best = state;
                bestPid = pid;
            }
        }

        PresentWindowSample? sample = null;
        if (best is not null)
        {
            var fps = best.PresentsInWindow * 1000.0 / _intervalMs;
            var avgFrametime = best.FrametimeSumInWindow / best.PresentsInWindow;
            _fpsSamples.Add(fps);
            sample = new PresentWindowSample(fps, avgFrametime, bestPid);
        }

        foreach (var state in _candidates.Values)
        {
            state.PresentsInWindow = 0;
            state.FrametimeSumInWindow = 0;
        }
        return sample;
    }

    public PresentCaptureResult Complete()
    {
        var dominant = _candidates.OrderByDescending(kv => kv.Value.TotalPresents).First();
        return new PresentCaptureResult(
            [.. dominant.Value.FrametimesMs],
            [.. _fpsSamples],
            dominant.Value.TotalPresents > 0 ? dominant.Key : 0);
    }
}
