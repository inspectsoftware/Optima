using System.IO;
using System.IO.Compression;
using System.Text.RegularExpressions;
using Optima.App.Logging;

namespace Optima.App.Services;

/// <summary>
/// Turns a raw crash bundle into a shareable zip: every text file passes the secret redactor plus a personal-identifier
/// scrub (Windows user name, machine name, user profile paths) so the archive is safe to hand to developers.
/// </summary>
public static partial class CrashExporter
{
    [GeneratedRegex(@"(?i)[A-Z]:\\Users\\[^\\/\r\n""]+")]
    private static partial Regex UserProfilePath();

    public static string RedactText(string text)
    {
        var redacted = LogRedactor.Redact(text);
        redacted = UserProfilePath().Replace(redacted, m => m.Value[..(m.Value.IndexOf("Users", StringComparison.OrdinalIgnoreCase) + 5)] + @"\[user]");
        var userName = Environment.UserName;
        if (!string.IsNullOrWhiteSpace(userName) && userName.Length > 1)
        {
            redacted = redacted.Replace(userName, "[user]", StringComparison.OrdinalIgnoreCase);
        }
        var machine = Environment.MachineName;
        if (!string.IsNullOrWhiteSpace(machine) && machine.Length > 1)
        {
            redacted = redacted.Replace(machine, "[machine]", StringComparison.OrdinalIgnoreCase);
        }
        return redacted;
    }

    public static string ExportRedactedZip(string bundleDirectory)
    {
        var name = Path.GetFileName(bundleDirectory.TrimEnd(Path.DirectorySeparatorChar));
        var zipPath = Path.Combine(Path.GetDirectoryName(bundleDirectory)!, name + "-redacted.zip");
        if (File.Exists(zipPath))
        {
            File.Delete(zipPath);
        }

        using var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create);
        foreach (var file in Directory.EnumerateFiles(bundleDirectory))
        {
            var entryName = Path.GetFileName(file);
            var extension = Path.GetExtension(file).ToLowerInvariant();
            if (extension is ".txt" or ".log" or ".json" or ".md")
            {
                var entry = zip.CreateEntry(entryName, CompressionLevel.Optimal);
                using var writer = new StreamWriter(entry.Open());
                writer.Write(RedactText(File.ReadAllText(file)));
            }
            else
            {
                zip.CreateEntryFromFile(file, entryName, CompressionLevel.Optimal);
            }
        }
        return zipPath;
    }
}
