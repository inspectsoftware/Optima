using System.Text;
using Optima.Core.Models;

namespace Optima.Core.Health;

/// <summary>
/// An issue as text that stands on its own, for the clipboard: what it is, how often and since
/// when, what the error guide says, every kept occurrence with its whole exception, and what led
/// up to the first one. Redacted like everything else that leaves the app.
/// </summary>
public static class IssueReport
{
    private const string TimeFormat = "yyyy-MM-dd HH:mm:ss.fff zzz";

    public static string Format(Issue issue, ReportEnvironment environment, RedactionIdentity? identity = null)
    {
        var text = new StringBuilder();
        text.Append("Optima ").Append(environment.AppVersion).Append(" on ").AppendLine(environment.OsVersion);
        text.Append('[').Append(issue.Severity.ToString().ToUpperInvariant()).Append("] ").AppendLine(issue.Title);
        text.Append("code: ").Append(issue.Code)
            .Append(issue.Count == 1 ? ", seen once" : $", seen {issue.Count} times")
            .Append(", first ").Append(issue.FirstSeen.ToString(TimeFormat));
        if (issue.LastSeen != issue.FirstSeen)
        {
            text.Append(", last ").Append(issue.LastSeen.ToString(TimeFormat));
        }
        text.AppendLine();
        if (issue.Detail.Length > 0)
        {
            text.AppendLine(issue.Detail);
        }

        if (ErrorCatalog.Find(issue.Code) is { } guide)
        {
            text.AppendLine().AppendLine(ErrorCatalog.FormatPlaintext(guide));
        }

        if (issue.Evidence.Count > 0)
        {
            text.AppendLine().AppendLine(issue.Count > issue.Evidence.Count
                ? $"Occurrences (the first and the latest {issue.Evidence.Count - 1} of {issue.Count}):"
                : "Occurrences:");
            foreach (var record in issue.Evidence)
            {
                text.Append(record.Timestamp.ToString(TimeFormat)).Append(" [").Append(record.LevelText).Append("] ")
                    .Append(record.Source.Length > 0 ? record.Source : "Optima").Append(": ").AppendLine(record.Message);
                if (record.Exception is { } exception)
                {
                    text.AppendLine(exception.FullText);
                }
            }
        }

        LogReport.AppendLeadUp(text, issue.LeadUp);
        return Redactor.Redact(text.ToString().TrimEnd(), identity ?? RedactionIdentity.Current);
    }
}
