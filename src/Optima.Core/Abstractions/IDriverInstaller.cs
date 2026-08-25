namespace Optima.Core.Abstractions;

/// <summary>Why the virtual display driver is not usable right now.</summary>
public enum DriverState
{
    /// <summary>Device present and usable.</summary>
    Installed,

    /// <summary>No device, but a driver package ships alongside the app and can be installed.</summary>
    NotInstalledPackageAvailable,

    /// <summary>No device and no package to install from.</summary>
    NotInstalledNoPackage,
}

/// <summary>A driver package discovered next to the application.</summary>
public sealed record DriverPackageInfo
{
    /// <summary>Absolute path of the .inf.</summary>
    public required string InfPath { get; init; }

    /// <summary>Hardware id the root device node must be created with, read from the .inf.</summary>
    public required string HardwareId { get; init; }

    public string Provider { get; init; } = string.Empty;

    public string DisplayName { get; init; } = string.Empty;

    /// <summary>True when a .cat sits beside the .inf — its absence guarantees the install will be rejected.</summary>
    public bool HasCatalog { get; init; }
}

/// <summary>
/// Installs the bundled virtual display driver so an end user never has to touch
/// Device Manager or devcon (§ driver bundling). All privileged work is delegated to
/// the elevated helper; every step is reversible through <see cref="UninstallAsync"/>.
/// </summary>
public interface IDriverInstaller
{
    /// <summary>The package shipped alongside the app, or null when none was bundled.</summary>
    DriverPackageInfo? FindBundledPackage();

    Task<DriverState> GetStateAsync(CancellationToken ct = default);

    /// <summary>
    /// Stages the package, creates the root-enumerated device node, and writes a default
    /// settings file. Returns a friendly error on failure rather than throwing.
    /// </summary>
    Task<DriverInstallResult> InstallAsync(CancellationToken ct = default);

    Task<DriverInstallResult> UninstallAsync(CancellationToken ct = default);
}

public sealed record DriverInstallResult
{
    public required bool Success { get; init; }
    public Models.UserFriendlyError? Error { get; init; }
    public bool RestartRequired { get; init; }

    public static DriverInstallResult Ok(bool restartRequired = false)
        => new() { Success = true, RestartRequired = restartRequired };

    public static DriverInstallResult Fail(Models.UserFriendlyError error)
        => new() { Success = false, Error = error };
}
