namespace Optima.Core.Models;

/// <summary>A concrete display mode (resolution + refresh rate).</summary>
public readonly record struct DisplayMode(int Width, int Height, int RefreshRate)
{
    public override string ToString() => $"{Width}x{Height} @ {RefreshRate} Hz";

    public bool IsValid => Width >= 640 && Height >= 480 && RefreshRate >= 24 && RefreshRate <= 500;
}

/// <summary>Identifies one attached display and its current mode.</summary>
public sealed record DisplayInfo
{
    public required string DeviceName { get; init; }

    public required string FriendlyName { get; init; }

    public string AdapterName { get; init; } = string.Empty;

    public string DevicePath { get; init; } = string.Empty;

    public DisplayMode CurrentMode { get; init; }

    public bool IsPrimary { get; init; }

    public bool IsActive { get; init; }
}
