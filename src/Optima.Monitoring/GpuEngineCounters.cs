using System.Collections;
using System.Diagnostics;

namespace Optima.Monitoring;

/// <summary>Fallback GPU utilization for systems without NVML: the documented "GPU Engine" performance counters, read as ONE category snapshot per tick.</summary>
public sealed class GpuEngineCounters
{
    private const string CategoryName = "GPU Engine";
    private const string CounterName = "Utilization Percentage";
    private const string EngineSuffix = "engtype_3D";

    private PerformanceCounterCategory? _category;
    private Dictionary<string, (long Raw, long Timestamp)> _previous = new(StringComparer.OrdinalIgnoreCase);

    public double Sample()
    {
        _category ??= new PerformanceCounterCategory(CategoryName);
        var snapshot = new List<(string Instance, long Raw, long Timestamp)>();
        foreach (DictionaryEntry counter in _category.ReadCategory())
        {
            if (!string.Equals(counter.Key as string, CounterName, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            foreach (DictionaryEntry instance in (InstanceDataCollection)counter.Value!)
            {
                var name = (string)instance.Key;
                if (!name.EndsWith(EngineSuffix, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }
                var sample = ((InstanceData)instance.Value!).Sample;
                snapshot.Add((name, sample.RawValue, sample.TimeStamp100nSec));
            }
        }

        var (utilization, next) = Compute(_previous, snapshot);
        _previous = next;
        return utilization;
    }

    public static (double Utilization, Dictionary<string, (long Raw, long Timestamp)> Next) Compute(
        IReadOnlyDictionary<string, (long Raw, long Timestamp)> previous,
        IReadOnlyList<(string Instance, long Raw, long Timestamp)> current)
    {
        var next = new Dictionary<string, (long Raw, long Timestamp)>(StringComparer.OrdinalIgnoreCase);
        var sum = 0.0;
        foreach (var (instance, raw, timestamp) in current)
        {
            next[instance] = (raw, timestamp);
            if (previous.TryGetValue(instance, out var before) && timestamp > before.Timestamp && raw >= before.Raw)
            {
                sum += 100.0 * (raw - before.Raw) / (timestamp - before.Timestamp);
            }
        }
        return (Math.Clamp(sum, 0, 100), next);
    }
}
