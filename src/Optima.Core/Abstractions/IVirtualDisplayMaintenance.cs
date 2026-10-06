namespace Optima.Core.Abstractions;

/// <summary>
/// What can be done to a virtual display driver that is installed and not answering, short of
/// reinstalling it.
/// </summary>
public interface IVirtualDisplayMaintenance
{
    /// <summary>
    /// Asks the driver to re-read its settings and attach its display again. The most common
    /// virtual display fault, a device that is enabled with its output parked, is cured by this.
    /// It goes through the elevated helper when the driver's pipe refuses a direct write, so it can
    /// raise an administrator prompt: it belongs in a launch the player started, not in the
    /// background.
    /// </summary>
    Task ReloadDriverAsync(CancellationToken ct = default);
}
