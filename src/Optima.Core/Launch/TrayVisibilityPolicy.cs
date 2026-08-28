namespace Optima.Core.Launch;

public enum TrayWindowAction
{
    None,
    Hide,
    Restore,
}

/// <summary>
/// Decides when the main window should hide to the tray during a game session and
/// when it should come back. Hides once the game is actually running (Monitoring),
/// so early launch failures stay visible; restores on session end only if the hide
/// was automatic and the user has not already brought the window back themselves.
/// </summary>
public sealed class TrayVisibilityPolicy
{
    private bool _autoHidden;

    public TrayWindowAction OnPhase(LaunchPhase phase)
    {
        if (phase == LaunchPhase.Monitoring && !_autoHidden)
        {
            _autoHidden = true;
            return TrayWindowAction.Hide;
        }
        if (phase is LaunchPhase.Completed or LaunchPhase.Failed && _autoHidden)
        {
            _autoHidden = false;
            return TrayWindowAction.Restore;
        }
        return TrayWindowAction.None;
    }

    /// <summary>The user restored the window themselves; session end must not steal focus.</summary>
    public void OnManualShow() => _autoHidden = false;
}
