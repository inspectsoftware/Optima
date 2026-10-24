namespace Optima.Core.Configuration;

/// <summary>The find box on the SETTINGS page: which rows a query keeps.</summary>
public static class SettingsSearch
{
    /// <summary>
    /// True when every word of the query is in the row's texts (its label, hint and longer note, and
    /// the names of the card and section it sits in), in any case and any order, so "windows start"
    /// finds "Start with Windows" and "discord" finds every row of the Discord section. An empty
    /// query keeps every row.
    /// </summary>
    public static bool Matches(string? query, params string?[] texts)
    {
        var text = string.Join(' ', texts);
        return (query ?? string.Empty)
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .All(word => text.Contains(word, StringComparison.OrdinalIgnoreCase));
    }
}
