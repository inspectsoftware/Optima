namespace Optima.Core.Abstractions;

/// <summary>Why the virtual display driver is not usable right now.</summary>
public enum DriverState
{
    Installed,

    NotInstalledPackageAvailable,

    NotInstalledNoPackage,
}

/// <summary>A driver package discovered next to the application.</summary>
public sealed record DriverPackageInfo
{
    public required string InfPath { get; init; }

    public required string HardwareId { get; init; }

    public string Provider { get; init; } = string.Empty;

    public string DisplayName { get; init; } = string.Empty;

    public bool HasCatalog { get; init; }
}

/// <summary>Installs the bundled virtual display driver so an end user never has to touch Device Manager or devcon (§ driver bundling).</summary>
public interface IDriverInstaller
{
    DriverPackageInfo? FindBundledPackage();

    Task<DriverState> GetStateAsync(CancellationToken ct = default);

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
