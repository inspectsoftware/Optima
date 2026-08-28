namespace Optima.Core.Statistics;

/// <summary>
/// Renders a numeric series as a block-character sparkline, the SESSIONS page's chart engine.
/// Numbers always appear beside it, never replaced by it (UI convention shared with AsciiBar);
/// no charting dependency required.
/// </summary>
public static class AsciiSparkline
{
    private static readonly char[] Levels = ['▁', '▂', '▃', '▄', '▅', '▆', '▇', '█'];

    /// <summary>
    /// Renders values (left = first) into at most <paramref name="width"/> characters.
    /// Longer series are down-sampled by bucket means; quantization spans the series min..max.
    /// </summary>
    public static string Render(IReadOnlyList<double> values, int width)
    {
        if (values.Count == 0 || width <= 0)
        {
            return string.Empty;
        }

        var samples = values.Count <= width ? values : Downsample(values, width);
        var min = samples.Min();
        var max = samples.Max();
        var range = max - min;

        var chars = new char[samples.Count];
        for (var i = 0; i < samples.Count; i++)
        {
            // A flat series renders mid-height rather than all-min glyphs.
            var level = range <= double.Epsilon
                ? Levels.Length / 2
                : (int)Math.Min(Levels.Length - 1, (samples[i] - min) / range * Levels.Length);
            chars[i] = Levels[level];
        }
        return new string(chars);
    }

    /// <summary>Renders a long series into multiple lines of at most <paramref name="width"/> characters, no downsampling.</summary>
    public static IReadOnlyList<string> RenderWrapped(IReadOnlyList<double> values, int width)
    {
        if (values.Count == 0 || width <= 0)
        {
            return [];
        }

        // Quantize against the whole series so wrapped rows share one scale.
        var min = values.Min();
        var max = values.Max();
        var range = max - min;
        var lines = new List<string>();
        for (var start = 0; start < values.Count; start += width)
        {
            var count = Math.Min(width, values.Count - start);
            var chars = new char[count];
            for (var i = 0; i < count; i++)
            {
                var level = range <= double.Epsilon
                    ? Levels.Length / 2
                    : (int)Math.Min(Levels.Length - 1, (values[start + i] - min) / range * Levels.Length);
                chars[i] = Levels[level];
            }
            lines.Add(new string(chars));
        }
        return lines;
    }

    private static double[] Downsample(IReadOnlyList<double> values, int buckets)
    {
        var result = new double[buckets];
        for (var b = 0; b < buckets; b++)
        {
            var start = (int)((long)b * values.Count / buckets);
            var end = (int)((long)(b + 1) * values.Count / buckets);
            if (end <= start)
            {
                end = start + 1;
            }
            double sum = 0;
            for (var i = start; i < end; i++)
            {
                sum += values[i];
            }
            result[b] = sum / (end - start);
        }
        return result;
    }
}
