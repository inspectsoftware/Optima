namespace Optima.Core.Launch;

public enum OverlayCorner
{
    TopLeft,
    TopRight,
    BottomLeft,
    BottomRight,
}

/// <summary>A rectangle in whatever unit the caller works in (device pixels or DIPs).</summary>
public readonly record struct OverlayRect(double Left, double Top, double Width, double Height);

/// <summary>Pure corner-placement math for the FPS overlay window; unit-agnostic.</summary>
public static class OverlayPlacement
{
    public const double DefaultMargin = 16;

    public static (double X, double Y) Compute(
        OverlayCorner corner, OverlayRect workArea, double overlayWidth, double overlayHeight, double margin = DefaultMargin)
        => corner switch
        {
            OverlayCorner.TopLeft => (workArea.Left + margin, workArea.Top + margin),
            OverlayCorner.TopRight => (workArea.Left + workArea.Width - overlayWidth - margin, workArea.Top + margin),
            OverlayCorner.BottomLeft => (workArea.Left + margin, workArea.Top + workArea.Height - overlayHeight - margin),
            _ => (workArea.Left + workArea.Width - overlayWidth - margin, workArea.Top + workArea.Height - overlayHeight - margin),
        };

    /// <summary>Parses the persisted corner name; unknown values fall back to top right.</summary>
    public static OverlayCorner ParseCorner(string? text)
        => Enum.TryParse<OverlayCorner>(text, ignoreCase: true, out var corner) ? corner : OverlayCorner.TopRight;
}
