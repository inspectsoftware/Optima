using System.Runtime.InteropServices;
using Optima.Core.Abstractions;

namespace Optima.Core.Detection;

/// <summary>
/// Picks the driver package to install out of the folder that ships next to the application.
/// A distribution can hold several INFs (one per architecture, plus unrelated drivers), so
/// selection is explicit rather than "first file found": the package must be Display class and
/// target the running architecture. Where an INF lists several hardware ids, the root-enumerated
/// one wins, since that is the only kind of device node the installer can create.
/// </summary>
public static class BundledDriverPackage
{
    public static DriverPackageInfo? Find(string folder, Action<string>? note = null)
        => Find(folder, RuntimeInformation.OSArchitecture, note);

    public static DriverPackageInfo? Find(string folder, Architecture architecture, Action<string>? note = null)
    {
        if (!Directory.Exists(folder))
        {
            return null;
        }

        DriverPackageInfo? otherArchitecture = null;

        foreach (var inf in Directory.EnumerateFiles(folder, "*.inf", SearchOption.AllDirectories).OrderBy(p => p))
        {
            try
            {
                var parsed = InfFile.Parse(File.ReadAllText(inf));
                if (string.IsNullOrWhiteSpace(parsed.HardwareId))
                {
                    note?.Invoke($"Skipping {inf}: no hardware id declared");
                    continue;
                }

                // The installer creates a Display-class device node, so anything else in
                // the folder (an audio driver, for instance) must not be selected.
                if (!string.Equals(parsed.DeviceClass, "Display", StringComparison.OrdinalIgnoreCase))
                {
                    note?.Invoke($"Skipping {inf}: class is {parsed.DeviceClass}, not Display");
                    continue;
                }

                var package = new DriverPackageInfo
                {
                    InfPath = inf,
                    HardwareId = parsed.HardwareId,
                    Provider = parsed.Provider ?? string.Empty,
                    DisplayName = parsed.Description ?? Path.GetFileNameWithoutExtension(inf),
                    HasCatalog = Directory.EnumerateFiles(Path.GetDirectoryName(inf)!, "*.cat").Any(),
                };

                if (parsed.TargetsArchitecture(architecture))
                {
                    return package;
                }

                otherArchitecture ??= package;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                note?.Invoke($"Could not read driver package {inf}: {ex.Message}");
            }
        }

        if (otherArchitecture is not null)
        {
            note?.Invoke($"A bundled driver was found but none targets {architecture}");
        }
        return null;
    }
}
