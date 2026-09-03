namespace Optima.Core.Configuration;

/// <summary>One build's worth of changes: the `## date - title` heading plus its bullets.</summary>
public sealed record ChangelogEntry
{
    public required string Date { get; init; }
    public required string Title { get; init; }
    public IReadOnlyList<string> Changes { get; init; } = [];
}

/// <summary>Parses the shipped CHANGELOG.md for the UPDATE LOG page.</summary>
public static class ChangelogParser
{
    public static IReadOnlyList<ChangelogEntry> Parse(string markdown)
    {
        var entries = new List<ChangelogEntry>();
        string? date = null;
        string? title = null;
        var changes = new List<string>();

        void Flush()
        {
            if (title is not null)
            {
                entries.Add(new ChangelogEntry { Date = date ?? string.Empty, Title = title, Changes = [.. changes] });
            }
            changes.Clear();
        }

        foreach (var raw in markdown.Split('\n'))
        {
            var line = raw.TrimEnd('\r');

            if (line.StartsWith("## ", StringComparison.Ordinal))
            {
                Flush();
                var heading = line[3..].Trim();
                var separator = heading.IndexOf(" - ", StringComparison.Ordinal);
                if (separator > 0)
                {
                    date = heading[..separator].Trim();
                    title = heading[(separator + 3)..].Trim();
                }
                else
                {
                    date = string.Empty;
                    title = heading;
                }
                continue;
            }

            if (title is null)
            {
                continue;
            }

            var trimmed = line.TrimStart();
            if (trimmed.StartsWith("- ", StringComparison.Ordinal))
            {
                changes.Add(trimmed[2..].Trim());
            }
            else if (trimmed.Length > 0 && changes.Count > 0 && line.StartsWith(" ", StringComparison.Ordinal))
            {
                changes[^1] += " " + trimmed;
            }
        }

        Flush();
        return entries;
    }
}
