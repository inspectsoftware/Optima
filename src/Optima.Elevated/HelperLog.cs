using System.Globalization;

namespace Optima.Elevated;

/// <summary>
/// Minimal append-only log for the elevated helper.
///
/// The helper runs as a separate elevated process with no console and no UI, so without
/// this every failure inside it is invisible: the caller only ever sees a success flag
/// and a one-line message. Deliberately dependency-free, because the helper should stay
/// small and must not drag a logging framework into an elevated process.
/// </summary>
internal static class HelperLog
{
    private static readonly object Gate = new();
    private static readonly string LogPath = BuildPath();

    private static string BuildPath()
    {
        try
        {
            var dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Optima", "logs");
            Directory.CreateDirectory(dir);
            return Path.Combine(dir, $"optima-elevated-{DateTime.Now:yyyyMMdd}.log");
        }
        catch (Exception)
        {
            return string.Empty;
        }
    }

    internal static void Write(string message)
    {
        if (LogPath.Length == 0)
        {
            return;
        }
        try
        {
            var line = string.Format(
                CultureInfo.InvariantCulture,
                "{0:yyyy-MM-dd HH:mm:ss.fff} [pid {1}] {2}{3}",
                DateTime.Now, Environment.ProcessId, message, Environment.NewLine);
            lock (Gate)
            {
                File.AppendAllText(LogPath, line);
            }
        }
        catch (Exception)
        {
            // Logging must never take the helper down.
        }
    }
}
