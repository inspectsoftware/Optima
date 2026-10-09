using Microsoft.Extensions.Logging;
using Optima.Core.Models;

namespace Optima.Core.Health;

/// <summary>What a log record turns out to be: which issue, how serious, and what to call it.</summary>
public sealed record IssueMatch(string Key, string Code, IssueSeverity Severity, string Title, string Detail);

/// <summary>
/// Decides which log records are issues. Pure, so every rule is a test.
///
/// The rules go from specific to general. A line Optima wrote with an error code is that code. A
/// line from a known place (a crash handler, the restore path) is the code that place stands for.
/// And any error nothing claims is still an issue, UNCLASSIFIED, with everything it carried: no
/// rule set knows every fault, so the promise is not that every fault is recognised but that no
/// error goes by unlisted.
/// </summary>
public static class LogRuleTable
{
    /// <summary>The engine's own namespace. Its lines never become issues, or one bad line would feed itself.</summary>
    public const string OwnNamespace = "Optima.Core.Health";

    public const string Unclassified = "UNCLASSIFIED";

    /// <param name="PerCallSite">
    /// The code covers many unrelated failures (any background task, any unexpected error), so the
    /// issue is one per place it was thrown from rather than one for the lot.
    /// </param>
    private sealed record TemplateRule(string TemplateStart, string Code, IssueSeverity Severity, bool PerCallSite = false);

    private static readonly TemplateRule[] Templates =
    [
        new("Unhandled UI exception", "OPTIMA_CRASHED", IssueSeverity.Critical),
        new("Unhandled AppDomain exception", "OPTIMA_CRASHED", IssueSeverity.Critical),
        new("The previous run of Optima ended on a fatal error", "PREVIOUS_RUN_CRASHED", IssueSeverity.Error),
        new("The previous run of Optima (", "UNCLEAN_EXIT", IssueSeverity.Note),
        new("Unobserved task exception", "BACKGROUND_TASK_FAILED", IssueSeverity.Error, PerCallSite: true),
        new("Failed restoring {What}", "RESTORE_STEP_FAILED", IssueSeverity.Error),
        new("Emergency restore ", "RESTORE_STEP_FAILED", IssueSeverity.Error),
        new("Corrupt JSON at {Path}; recovered", "SETTINGS_RECOVERED", IssueSeverity.Note),
        new("Corrupt JSON at {Path}", "SETTINGS_CORRUPT", IssueSeverity.Error),
        new("Elevated helper not found", "HELPER_MISSING", IssueSeverity.Error),
        new("Optima Shield is missing", "SHIELD_MISSING", IssueSeverity.Error),
        new("Unexpected session failure", "UNEXPECTED", IssueSeverity.Error, PerCallSite: true),
        new("Session task faulted", "UNEXPECTED", IssueSeverity.Error, PerCallSite: true),
    ];

    /// <summary>
    /// Codes whose weight is not the weight of the line that reported them. A power plan the PC
    /// does not offer is logged as a warning, and is something to know rather than something wrong.
    /// </summary>
    private static readonly Dictionary<string, IssueSeverity> CodeSeverity = new(StringComparer.OrdinalIgnoreCase)
    {
        ["POWER_PLAN_UNAVAILABLE"] = IssueSeverity.Note,
        ["UNCLEAN_EXIT"] = IssueSeverity.Note,
    };

    /// <summary>Every code a template rule or a check can raise, for the test that holds the guide to them.</summary>
    public static IReadOnlyList<string> Codes { get; } =
    [
        .. Templates.Select(t => t.Code).Distinct(),
        Unclassified,
        "ISSUES_OVERFLOW",
        "VIRTUALIZATION_OFF",
        "HYPERVISOR_OFF",
        "DISK_SPACE_LOW",
        "VDD_RESTORE_PENDING",
    ];

    /// <summary>The weight an issue of this code has, whoever reported it and at whatever level.</summary>
    public static IssueSeverity SeverityFor(string code, IssueSeverity reported)
        => CodeSeverity.TryGetValue(code, out var severity) ? severity : reported;

    public static IssueMatch? Classify(LogRecord record)
    {
        if (record.Level < LogLevel.Warning || record.Source.StartsWith(OwnNamespace, StringComparison.Ordinal))
        {
            return null;
        }

        foreach (var rule in Templates)
        {
            if (record.Template.StartsWith(rule.TemplateStart, StringComparison.Ordinal))
            {
                return Match(rule.Code, rule.Severity, record, rule.PerCallSite);
            }
        }

        // A line that names its own code: a typed error, or a step that was skipped.
        if (record.ErrorCode is { Length: > 0 } code)
        {
            var reported = record.Level >= LogLevel.Error ? IssueSeverity.Error : IssueSeverity.Warning;
            return Match(code, reported, record, perCallSite: false);
        }

        // A warning nobody claims is a line in the log. An error nobody claims is still an issue.
        return record.Level >= LogLevel.Error
            ? Match(Unclassified, record.Level >= LogLevel.Critical ? IssueSeverity.Critical : IssueSeverity.Error, record, perCallSite: true)
            : null;
    }

    private static IssueMatch Match(string code, IssueSeverity reported, LogRecord record, bool perCallSite)
    {
        var guide = ErrorCatalog.Find(code);
        // The unclassified have no title of their own; what the line said is the only name they have.
        var title = code == Unclassified || guide is null ? Shorten(record.Message, 140) : guide.Title;
        return new IssueMatch(
            perCallSite ? CallSiteKey(code, record) : code,
            code,
            SeverityFor(code, reported),
            title,
            record.Line);
    }

    /// <summary>
    /// The same place failing the same way is the same issue, whatever values the message carried:
    /// the key is made of the template and the throw site, never of the rendered message.
    /// </summary>
    private static string CallSiteKey(string code, LogRecord record)
        => string.Join('|', code, record.Source, record.Template,
            record.Exception?.TypeName ?? string.Empty, record.Exception?.TopFrame ?? string.Empty);

    private static string Shorten(string text, int length)
    {
        var line = text.AsSpan();
        var end = line.IndexOfAny('\r', '\n');
        if (end >= 0)
        {
            line = line[..end];
        }
        return line.Length <= length ? line.ToString() : string.Concat(line[..(length - 1)], "…");
    }
}
