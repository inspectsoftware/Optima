namespace Optima.Core.Statistics;

/// <summary>
/// Picks the samples worth drawing when a series has more of them than its chart has pixel columns:
/// the lowest and the highest of every column, in the order they occur, and both ends. A line
/// through those covers the same pixels as a line through all of them, so every spike and dip
/// stays where it was while an hour of per-second samples comes down to a few hundred points.
/// </summary>
public static class SeriesEnvelope
{
    /// <summary>
    /// Fills <paramref name="indices"/> with the positions to draw, ascending. A series of at most
    /// two samples per column is returned whole.
    /// </summary>
    public static void Select(IReadOnlyList<double> values, int columns, List<int> indices)
    {
        indices.Clear();
        if (columns < 1 || values.Count <= 2 * columns)
        {
            for (var i = 0; i < values.Count; i++)
            {
                indices.Add(i);
            }
            return;
        }

        indices.Add(0);
        for (var column = 0; column < columns; column++)
        {
            var start = (int)((long)column * values.Count / columns);
            var end = (int)((long)(column + 1) * values.Count / columns);
            var low = start;
            var high = start;
            for (var i = start + 1; i < end; i++)
            {
                if (values[i] < values[low])
                {
                    low = i;
                }
                if (values[i] > values[high])
                {
                    high = i;
                }
            }
            Add(Math.Min(low, high));
            Add(Math.Max(low, high));
        }
        Add(values.Count - 1);

        // Columns are walked left to right, so a repeat can only be the index added last.
        void Add(int index)
        {
            if (indices[^1] != index)
            {
                indices.Add(index);
            }
        }
    }
}
