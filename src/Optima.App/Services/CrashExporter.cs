using System.IO;
using System.IO.Compression;
using Optima.Core.Health;

namespace Optima.App.Services;

/// <summary>
/// Turns a raw crash bundle into a shareable zip: every text file passes the redactor (secrets, Windows user name,
/// machine name, user profile paths) so the archive is safe to hand to developers.
/// </summary>
public static class CrashExporter
{
    public static string RedactText(string text) => Redactor.Redact(text);

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
