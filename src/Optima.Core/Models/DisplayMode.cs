namespace Optima.Core.Models;

/// <summary>A concrete display mode (resolution + refresh rate).</summary>
public readonly record struct DisplayMode(int Width, int Height, int RefreshRate)
{
    public override string ToString() => $"{Width}x{Height} @ {RefreshRate} Hz";

    // 500 Hz comfortably covers real hardware while rejecting the 999/9999 placeholder
    // rates virtual display drivers ship in their default settings files.
    public bool IsValid => Width >= 640 && Height >= 480 && RefreshRate >= 24 && RefreshRate <= 500;
}

/// <summary>Identifies one attached display and its current mode.</summary>
public sealed record DisplayInfo
{
    /// <summary>GDI device name, e.g. <c>\\.\DISPLAY3</c>.</summary>
    public required string DeviceName { get; init; }

    /// <summary>Human readable monitor / adapter description, e.g. "Virtual Display Driver".</summary>
    public required string FriendlyName { get; init; }

    /// <summary>Adapter device string the display is connected to.</summary>
    public string AdapterName { get; init; } = string.Empty;

    /// <summary>PnP device path (used to correlate with SetupAPI device instances).</summary>
    public string DevicePath { get; init; } = string.Empty;

    public DisplayMode CurrentMode { get; init; }

    public bool IsPrimary { get; init; }

    public bool IsActive { get; init; }
}
