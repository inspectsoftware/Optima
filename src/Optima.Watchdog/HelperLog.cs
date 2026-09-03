using System.Globalization;

namespace Optima.Watchdog;

/// <summary>Minimal append-only log for the elevated helper.</summary>
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
            return Path.Combine(dir, $"optima-watchdog-{DateTime.Now:yyyyMMdd}.log");
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
