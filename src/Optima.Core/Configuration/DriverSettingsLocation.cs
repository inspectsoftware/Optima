namespace Optima.Core.Configuration;

/// <summary>
/// Where the virtual display driver actually reads its settings file. The driver looks up the
/// VDDPATH folder in the registry first and only falls back to its built-in folder, so a file
/// Optima writes to the built-in folder while VDDPATH points elsewhere is never read.
/// Lives in Core because both the app and the elevated helper's install mode need it.
/// </summary>
public static class DriverSettingsLocation
{
    public const string RegistryKey = @"SOFTWARE\MikeTheTech\VirtualDisplayDriver";
    public const string RegistryValue = "VDDPATH";
    public const string FileName = "vdd_settings.xml";

    /// <summary>
    /// The user's own path when set; else <see cref="FileName"/> in the VDDPATH folder when that
    /// value exists; else <see cref="VddSettingsDefaults.DefaultPath"/>. Read-only: the registry is
    /// never written. <paramref name="readVddPath"/> replaces the registry read in tests.
    /// </summary>
    public static string Resolve(string? userOverride, Func<string?>? readVddPath = null)
        => Locate(userOverride, readVddPath).Path;

    /// <summary>
    /// <see cref="Resolve"/> plus where the answer came from ("your setting", "VDDPATH" or
    /// "default"), for the log: a file edited in one folder while the driver reads another is
    /// otherwise invisible. That is why VDDPATH is read even when the user's own path wins: when the
    /// two differ, the source names the file the driver reads instead.
    /// </summary>
    public static (string Path, string Source) Locate(string? userOverride, Func<string?>? readVddPath = null)
    {
        var folder = (readVddPath ?? ReadVddPathFromRegistry)();
        var driverPath = string.IsNullOrWhiteSpace(folder) ? null : Path.Combine(folder.Trim(), FileName);

        if (!string.IsNullOrWhiteSpace(userOverride))
        {
            return driverPath is null || string.Equals(Path.GetFullPath(driverPath), Path.GetFullPath(userOverride), StringComparison.OrdinalIgnoreCase)
                ? (userOverride, "your setting")
                : (userOverride, $"your setting, but VDDPATH makes the driver read {driverPath}");
        }

        return driverPath is null
            ? (VddSettingsDefaults.DefaultPath, "default")
            : (driverPath, "VDDPATH");
    }

    private static string? ReadVddPathFromRegistry()
    {
        if (!OperatingSystem.IsWindows())
        {
            return null;
        }
        try
        {
            // The driver is 64-bit and reads the 64-bit view; a 32-bit caller would otherwise be redirected.
            using var hive = Microsoft.Win32.RegistryKey.OpenBaseKey(
                Microsoft.Win32.RegistryHive.LocalMachine, Microsoft.Win32.RegistryView.Registry64);
            using var key = hive.OpenSubKey(RegistryKey, writable: false);
            return key?.GetValue(RegistryValue) as string;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            return null;
        }
    }
}
