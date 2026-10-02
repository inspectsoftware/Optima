using Optima.Core.Configuration;
using Optima.Core.Detection;

namespace Optima.Watchdog;

/// <summary>
/// One-shot mode the setup runs: <c>Optima.Watchdog.exe --install-driver [&lt;folder&gt;]</c> stages the
/// bundled virtual display driver, gives it a device node and writes the default settings file,
/// then exits instead of serving the IPC pipe.
///
/// The setup cannot do this itself: it installs per-user and never runs elevated. It launches this
/// helper instead, whose manifest asks Windows for administrator rights, so the user sees one UAC
/// prompt during setup rather than having to find the install on the app's Display page.
///
/// Exit codes: 0 installed (or already installed), 1 the install failed, 2 nothing installable was
/// found in the folder, 3 the arguments were rejected.
/// </summary>
internal static class InstallDriverCommand
{
    internal const string SwitchName = "--install-driver";

    internal static async Task<int> RunAsync(string[] args, CancellationToken ct)
    {
        var folder = args.Length > 1 && args[1].Length > 0
            ? args[1]
            : Path.Combine(AppContext.BaseDirectory, "drivers");

        if (!IsAcceptableFolder(folder))
        {
            HelperLog.Write($"--install-driver rejected folder {folder}");
            Console.Error.WriteLine("Refusing a driver folder outside the application directory.");
            return 3;
        }

        var notes = new List<string>();
        var package = BundledDriverPackage.Find(folder, notes.Add);
        foreach (var note in notes)
        {
            HelperLog.Write(note);
        }

        if (package is null)
        {
            HelperLog.Write($"--install-driver: no installable driver package in {folder}");
            Console.Error.WriteLine($"No Display-class driver package for this architecture was found in {folder}.");
            return 2;
        }

        HelperLog.Write($"Installing the bundled driver {package.DisplayName} ({package.HardwareId}) from {package.InfPath}");
        if (!package.HasCatalog)
        {
            HelperLog.Write($"Driver package {package.InfPath} has no .cat catalog; Windows will very likely reject it");
        }

        // Written before the device node exists, so the driver already has modes to advertise the
        // first time it loads; an existing file is never touched.
        EnsureDefaultSettingsFile();

        var installed = await CommandExecutor.InstallDriverPackageAsync(package.InfPath, package.HardwareId, ct);
        if (!installed.Success)
        {
            HelperLog.Write($"--install-driver failed: {installed.Error}");
            Console.Error.WriteLine(installed.Error);
            return 1;
        }

        HelperLog.Write("Virtual display driver installed "
            + $"(already present: {installed.AlreadyPresent}, restart required: {installed.RebootRequired})");
        return 0;
    }

    private static void EnsureDefaultSettingsFile()
    {
        var path = VddSettingsDefaults.DefaultPath;
        try
        {
            if (File.Exists(path))
            {
                return;
            }

            var directory = Path.GetDirectoryName(path);
            if (directory is not null)
            {
                Directory.CreateDirectory(directory);
            }
            File.WriteAllText(path, VddSettingsDefaults.DefaultXml);
            HelperLog.Write($"Default driver settings written to {path}");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            HelperLog.Write($"Could not write {path}: {ex.Message}");
        }
    }

    /// <summary>The folder must live inside the application directory, so a stray argument can never point the admin-only install at an arbitrary INF.</summary>
    private static bool IsAcceptableFolder(string folder)
    {
        string full, root;
        try
        {
            full = Path.GetFullPath(folder);
            root = Path.GetFullPath(AppContext.BaseDirectory);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }

        if (!root.EndsWith(Path.DirectorySeparatorChar))
        {
            root += Path.DirectorySeparatorChar;
        }
        return full.StartsWith(root, StringComparison.OrdinalIgnoreCase);
    }
}
