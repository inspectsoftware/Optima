namespace COPSBootstrapper.Core.Models;

/// <summary>Persisted user settings (%LOCALAPPDATA%\COPSBootstrapper\config.json, §21).</summary>
public sealed record AppSettings
{
    public bool FirstRunCompleted { get; init; }
    public string SelectedProfileName { get; init; } = "Default";
    public bool DeveloperMode { get; init; }
    public string MinimumLogLevel { get; init; } = "Information";

    /// <summary>"Auto", "MttVdd" or "Mock" — which virtual display provider to use.</summary>
    public string VirtualDisplayProvider { get; init; } = "Auto";

    /// <summary>Enable the external ETW frametime provider (requires the elevated helper).</summary>
    public bool EnableFrametimeCapture { get; init; } = true;

    /// <summary>Cached resolved paths so startup detection is instant; re-validated each run.</summary>
    public string? CachedGpgInstallDirectory { get; init; }
    public string? CachedGameLaunchUri { get; init; }

    /// <summary>Path to the virtual display driver settings XML (auto-detected, overridable).</summary>
    public string? VddSettingsPath { get; init; }
}
