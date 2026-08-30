namespace Optima.Core.Crashes;

/// <summary>
/// Passive reader for Google Play Games on PC's own log folder
/// (%LOCALAPPDATA%\Google\Play Games\Logs). AndroidSerial.log mirrors logcat and is
/// rotated to AndroidSerial-bkup-* files; a crash can land on either side of a
/// rotation, so the tail is read across the live file plus the newest backup.
/// Read-only by design: Optima never writes into the platform's folders.
/// </summary>
public sealed class GpgLogReader
{
    private const string SerialLogName = "AndroidSerial.log";
    private const string SerialBackupPattern = "AndroidSerial-bkup-*.log";
    private const long TailBytes = 512 * 1024;

    private readonly string _logsDirectory;

    public GpgLogReader() : this(Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Google", "Play Games", "Logs"))
    {
    }

    /// <summary>Test constructor pointing at an arbitrary logs folder.</summary>
    public GpgLogReader(string logsDirectory) => _logsDirectory = logsDirectory;

    public bool LogsFolderExists => Directory.Exists(_logsDirectory);

    /// <summary>The platform's minidump folder next to Logs, when present.</summary>
    public string? CrashReportingDirectory
    {
        get
        {
            var parent = Path.GetDirectoryName(_logsDirectory);
            if (parent is null)
            {
                return null;
            }
            var dir = Path.Combine(parent, "CrashReporting");
            return Directory.Exists(dir) ? dir : null;
        }
    }

    /// <summary>
    /// Last <paramref name="maxLines"/> lines of the logcat mirror, newest rotation aware:
    /// when the live file is short (it just rotated), the newest backup's tail is prepended.
    /// Returns an empty list when the folder or files are missing; never throws.
    /// </summary>
    public IReadOnlyList<string> ReadRecentSerialLines(int maxLines = 400)
    {
        try
        {
            if (!LogsFolderExists)
            {
                return [];
            }

            var lines = new List<string>();
            var live = Path.Combine(_logsDirectory, SerialLogName);
            var liveLines = ReadTailLines(live);

            if (liveLines.Count < maxLines)
            {
                var newestBackup = Directory
                    .EnumerateFiles(_logsDirectory, SerialBackupPattern)
                    .Select(f => new FileInfo(f))
                    .OrderByDescending(f => f.LastWriteTimeUtc)
                    .FirstOrDefault();
                if (newestBackup is not null)
                {
                    lines.AddRange(ReadTailLines(newestBackup.FullName));
                }
            }

            lines.AddRange(liveLines);
            return lines.Count <= maxLines ? lines : lines[^maxLines..];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    /// <summary>Names and sizes of the platform's minidump files, newest first. The dumps
    /// themselves are never copied: they can contain process memory.</summary>
    public IReadOnlyList<string> ListMinidumpNames(int max = 10)
    {
        try
        {
            var dir = CrashReportingDirectory;
            if (dir is null)
            {
                return [];
            }
            return Directory.EnumerateFiles(dir, "*", SearchOption.TopDirectoryOnly)
                .Select(f => new FileInfo(f))
                .OrderByDescending(f => f.LastWriteTimeUtc)
                .Take(max)
                .Select(f => $"{f.Name} ({f.Length / 1024} KB, {f.LastWriteTime:yyyy-MM-dd HH:mm:ss})")
                .ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    private static List<string> ReadTailLines(string path)
    {
        if (!File.Exists(path))
        {
            return [];
        }

        // Share-friendly open: the platform keeps these files open for writing.
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        if (stream.Length > TailBytes)
        {
            stream.Seek(-TailBytes, SeekOrigin.End);
        }
        using var reader = new StreamReader(stream);
        var lines = new List<string>();
        while (reader.ReadLine() is { } line)
        {
            lines.Add(line);
        }
        // The first line after a mid-file seek is almost always torn; drop it.
        if (stream.Length > TailBytes && lines.Count > 0)
        {
            lines.RemoveAt(0);
        }
        return lines;
    }
}
