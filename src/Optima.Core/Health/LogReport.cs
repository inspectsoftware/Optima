using System.Text;
using Optima.Core.Models;

namespace Optima.Core.Health;

/// <summary>Which build on which Windows a report comes from.</summary>
public sealed record ReportEnvironment(string AppVersion, string OsVersion);

/// <summary>
/// The text forms of a log record that leave the app: a line of the export, and the report copied
/// from the detail pane. A report has to stand on its own, because the person who can fix the
/// problem is rarely at the PC it happened on: it carries the build, the full error with the code
/// Windows returned, what the error guide says about it, and the lines that led up to it.
/// Both are redacted here, so no caller can forget to.
/// </summary>
public static class LogReport
{
    private const string TimeFormat = "yyyy-MM-dd HH:mm:ss.fff zzz";

    public static string FormatExport(IEnumerable<LogRecord> records, RedactionIdentity? identity = null)
    {
        var text = new StringBuilder();
        foreach (var record in records)
        {
            text.Append(record.Timestamp.ToString(TimeFormat)).Append(" [").Append(record.LevelText).Append("] ")
                .Append(record.Source).Append(": ").AppendLine(record.Message);
            if (record.Exception is { } exception)
            {
                text.AppendLine(exception.FullText);
            }
        }
        return Redactor.Redact(text.ToString(), identity ?? RedactionIdentity.Current);
    }

    public static string FormatReport(
        LogRecord record, IReadOnlyList<LogRecord> leadUp, ReportEnvironment environment, RedactionIdentity? identity = null)
    {
        var text = new StringBuilder();
        text.Append("Optima ").Append(environment.AppVersion).Append(" on ").AppendLine(environment.OsVersion);
        text.Append(record.Timestamp.ToString(TimeFormat)).Append("  ").Append(record.LevelText)
            .Append("  ").AppendLine(record.Source.Length > 0 ? record.Source : "Optima");
        text.AppendLine(record.Message);

        if (record.Exception is { NativeErrorCode: { } native } failed)
        {
            text.AppendLine().Append("Windows error ").Append(native).Append(": ").AppendLine(failed.NativeErrorText);
        }

        if (ErrorCatalog.Find(record.ErrorCode) is { } guide)
        {
            text.AppendLine().AppendLine(ErrorCatalog.FormatPlaintext(guide));
        }
        else if (record.ErrorCode.Length > 0)
        {
            text.AppendLine().Append("code: ").AppendLine(record.ErrorCode);
        }

        if (record.Exception is { } exception)
        {
            text.AppendLine().AppendLine("Exception:").AppendLine(exception.FullText);
        }

        if (record.Properties.Count > 0)
        {
            text.AppendLine().AppendLine("Arguments:");
            foreach (var (name, value) in record.Properties.OrderBy(p => p.Key, StringComparer.Ordinal))
            {
                text.Append("  ").Append(name).Append(" = ").AppendLine(value);
            }
        }

        if (leadUp.Count > 0)
        {
            text.AppendLine().AppendLine("Leading up to it:");
            foreach (var earlier in leadUp)
            {
                text.Append("  ").Append(earlier.Timestamp.ToString("HH:mm:ss.fff")).Append(' ')
                    .Append(earlier.LevelText.PadRight(8)).Append(' ')
                    .Append(earlier.ShortSource).Append(": ").AppendLine(earlier.Line);
            }
        }

        return Redactor.Redact(text.ToString().TrimEnd(), identity ?? RedactionIdentity.Current);
    }
}
