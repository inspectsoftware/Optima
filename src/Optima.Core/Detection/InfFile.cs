using System.Text.RegularExpressions;

namespace Optima.Core.Detection;

/// <summary>
/// Minimal INF reader, just enough to learn the hardware id a root-enumerated driver
/// must be installed against, plus the provider/description for display.
///
/// Root devices do not appear on their own, so installing one means creating the node
/// explicitly with its hardware id; that id only exists inside the INF's Models section.
/// This parses the subset of the INF grammar that matters and is deliberately tolerant:
/// a package that does not parse is reported as unusable rather than guessed at.
/// </summary>
public static partial class InfFile
{
    [GeneratedRegex(@"^\s*\[(?<name>[^\]]+)\]\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex SectionHeader();

    [GeneratedRegex(@"^\s*(?<key>[^=;]+?)\s*=\s*(?<value>[^;]*?)\s*(;.*)?$")]
    private static partial Regex KeyValue();

    /// <summary>Parsed INF facts. Null fields mean the INF did not declare them.</summary>
    public sealed record InfInfo(string? HardwareId, string? Provider, string? Description);

    public static InfInfo Parse(string infText)
    {
        var sections = SplitSections(infText);
        var strings = ReadStrings(sections);

        string? provider = null;
        if (sections.TryGetValue("Version", out var version))
        {
            provider = Expand(FirstValue(version, "Provider"), strings);
        }

        // [Manufacturer] maps a display name to one or more Models sections.
        var modelSectionNames = new List<string>();
        if (sections.TryGetValue("Manufacturer", out var manufacturer))
        {
            foreach (var line in manufacturer)
            {
                var kv = KeyValue().Match(line);
                if (!kv.Success)
                {
                    continue;
                }
                // "%Mfg% = Models, NTamd64.10.0...": the section is the first value, the
                // rest are target decorations that suffix it (Models.NTamd64.10.0).
                var parts = kv.Groups["value"].Value.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length == 0)
                {
                    continue;
                }
                modelSectionNames.Add(parts[0]);
                for (var i = 1; i < parts.Length; i++)
                {
                    modelSectionNames.Add($"{parts[0]}.{parts[i]}");
                }
            }
        }

        // Prefer a decorated (architecture-specific) section; that is what Windows uses.
        foreach (var name in modelSectionNames.OrderByDescending(n => n.Count(c => c == '.')))
        {
            if (!sections.TryGetValue(name, out var models))
            {
                continue;
            }
            foreach (var line in models)
            {
                var kv = KeyValue().Match(line);
                if (!kv.Success)
                {
                    continue;
                }
                // "%Desc% = Install, Root\MyDevice"
                var parts = kv.Groups["value"].Value.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length >= 2)
                {
                    return new InfInfo(
                        Expand(parts[1], strings),
                        provider,
                        Expand(kv.Groups["key"].Value, strings));
                }
            }
        }

        return new InfInfo(null, provider, null);
    }

    private static Dictionary<string, List<string>> SplitSections(string infText)
    {
        var sections = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        var current = new List<string>();
        sections[""] = current;

        foreach (var raw in infText.Split('\n'))
        {
            var line = raw.TrimEnd('\r').Trim();
            if (line.Length == 0 || line.StartsWith(';'))
            {
                continue;
            }

            var header = SectionHeader().Match(line);
            if (header.Success)
            {
                current = [];
                sections[header.Groups["name"].Value.Trim()] = current;
                continue;
            }
            current.Add(line);
        }
        return sections;
    }

    private static Dictionary<string, string> ReadStrings(Dictionary<string, List<string>> sections)
    {
        var strings = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, lines) in sections)
        {
            // [Strings] plus localized variants like [Strings.0409]
            if (!name.StartsWith("Strings", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            foreach (var line in lines)
            {
                var kv = KeyValue().Match(line);
                if (kv.Success)
                {
                    strings[kv.Groups["key"].Value.Trim()] = kv.Groups["value"].Value.Trim().Trim('"');
                }
            }
        }
        return strings;
    }

    private static string? FirstValue(List<string> lines, string key)
    {
        foreach (var line in lines)
        {
            var kv = KeyValue().Match(line);
            if (kv.Success && string.Equals(kv.Groups["key"].Value.Trim(), key, StringComparison.OrdinalIgnoreCase))
            {
                return kv.Groups["value"].Value.Trim();
            }
        }
        return null;
    }

    /// <summary>Resolves %Token% references against the INF's [Strings] table.</summary>
    private static string? Expand(string? value, Dictionary<string, string> strings)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }
        var trimmed = value.Trim().Trim('"');
        if (trimmed.Length > 2 && trimmed.StartsWith('%') && trimmed.EndsWith('%'))
        {
            var token = trimmed[1..^1];
            return strings.TryGetValue(token, out var resolved) ? resolved : trimmed;
        }
        return trimmed;
    }
}
