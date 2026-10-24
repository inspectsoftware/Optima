namespace Optima.Core.Configuration;

/// <summary>The find box on the SETTINGS page: which rows a query keeps.</summary>
public static class SettingsSearch
{
    /// <summary>
    /// True when every word of the query is in the row's label or hint, in any case and any order,
    /// so "windows start" finds "Start with Windows". An empty query keeps every row.
    /// </summary>
    public static bool Matches(string? query, string label, string? hint)
    {
        var text = label + " " + hint;
        return (query ?? string.Empty)
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .All(word => text.Contains(word, StringComparison.OrdinalIgnoreCase));
    }
}
