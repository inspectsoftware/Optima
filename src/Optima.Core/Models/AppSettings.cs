namespace Optima.Core.Models;

/// <summary>Persisted user settings (%LOCALAPPDATA%\Optima\config.json, §21).</summary>
public sealed record AppSettings
{
    public bool FirstRunCompleted { get; init; }
    public string SelectedProfileName { get; init; } = "Default";

    /// <summary>"Dark" or "Light" interface theme.</summary>
    public string Theme { get; init; } = "Dark";

    /// <summary>Accent color as #RRGGBB; the hover/pressed/glow family is derived from it.</summary>
    public string AccentColor { get; init; } = "#E8B45A";
    public bool DeveloperMode { get; init; }
    public string MinimumLogLevel { get; init; } = "Information";

    /// <summary>"Auto", "MttVdd" or "Mock": which virtual display provider to use.</summary>
    public string VirtualDisplayProvider { get; init; } = "Auto";

    /// <summary>Enable the external ETW frametime provider (requires the elevated helper).</summary>
    public bool EnableFrametimeCapture { get; init; } = true;

    /// <summary>Cached resolved paths so startup detection is instant; re-validated each run.</summary>
    public string? CachedGpgInstallDirectory { get; init; }
    public string? CachedGameLaunchUri { get; init; }

    /// <summary>Path to the virtual display driver settings XML (auto-detected, overridable).</summary>
    public string? VddSettingsPath { get; init; }

    /// <summary>Cosmetic per-display overrides (name, order, hidden), keyed by device path.</summary>
    public IReadOnlyDictionary<string, DisplayOverride> DisplayOverrides { get; init; }
        = new Dictionary<string, DisplayOverride>();

    /// <summary>Hide attached-but-inactive displays (the 0x0 phantom outputs) in the displays list.</summary>
    public bool HideInactiveDisplays { get; init; }

    /// <summary>Closing the main window hides Optima to the tray instead of exiting.</summary>
    public bool KeepInTrayOnClose { get; init; }

    /// <summary>Show the in-game FPS overlay while a session runs (borderless / windowed game only).</summary>
    public bool OverlayEnabled { get; init; }

    /// <summary>"TopLeft", "TopRight", "BottomLeft" or "BottomRight".</summary>
    public string OverlayCorner { get; init; } = "TopRight";

    /// <summary>Overlay window opacity, 0.2 to 1.0.</summary>
    public double OverlayOpacity { get; init; } = 0.8;

    /// <summary>Show the ping / jitter / loss line on the overlay.</summary>
    public bool OverlayShowNetwork { get; init; } = true;

    /// <summary>Host pinged for link quality when no game endpoint answers ICMP.</summary>
    public string NetworkReferenceHost { get; init; } = "1.1.1.1";

    /// <summary>
    /// Watch mode: when the game starts outside Optima, apply the selected profile
    /// automatically and restore it when the game exits.
    /// </summary>
    public bool EnableWatchMode { get; init; }

    /// <summary>Developer: replace the ETW FPS provider with the deterministic mock (restart required).</summary>
    public bool UseMockMetricsProvider { get; init; }
}
