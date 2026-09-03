using Optima.Core.Models;

namespace Optima.Core.Statistics;

/// <summary>Ping-result bookkeeping: rolling-window live values (what the readout shows) plus a whole-session aggregate (what the session row stores).</summary>
public sealed class NetworkQualityCalculator
{
    private readonly int _windowSize;
    private readonly Queue<double?> _window = [];

    private int _totalSent;
    private int _totalLost;
    private double _totalRttSum;
    private double _totalJitterSum;
    private int _totalJitterCount;
    private double? _lastRtt;

    public NetworkQualityCalculator(int windowSize = 30)
    {
        _windowSize = windowSize;
    }

    public void AddResult(double? rttMs)
    {
        _window.Enqueue(rttMs);
        while (_window.Count > _windowSize)
        {
            _window.Dequeue();
        }

        _totalSent++;
        if (rttMs is null)
        {
            _totalLost++;
            return;
        }
        _totalRttSum += rttMs.Value;
        if (_lastRtt is not null)
        {
            _totalJitterSum += Math.Abs(rttMs.Value - _lastRtt.Value);
            _totalJitterCount++;
        }
        _lastRtt = rttMs;
    }

    public double AveragePingMs
    {
        get
        {
            var successes = _window.Where(r => r is not null).Select(r => r!.Value).ToList();
            return successes.Count == 0 ? 0 : successes.Average();
        }
    }

    public double JitterMs
    {
        get
        {
            var successes = _window.Where(r => r is not null).Select(r => r!.Value).ToList();
            if (successes.Count < 2)
            {
                return 0;
            }
            double sum = 0;
            for (var i = 1; i < successes.Count; i++)
            {
                sum += Math.Abs(successes[i] - successes[i - 1]);
            }
            return sum / (successes.Count - 1);
        }
    }

    public double PacketLossPct
        => _window.Count == 0 ? 0 : 100.0 * _window.Count(r => r is null) / _window.Count;

    public NetworkQualityStats SessionAggregate
    {
        get
        {
            var received = _totalSent - _totalLost;
            return new NetworkQualityStats
            {
                AveragePingMs = received == 0 ? 0 : _totalRttSum / received,
                JitterMs = _totalJitterCount == 0 ? 0 : _totalJitterSum / _totalJitterCount,
                PacketLossPct = _totalSent == 0 ? 0 : 100.0 * _totalLost / _totalSent,
                SampleCount = _totalSent,
            };
        }
    }
}
