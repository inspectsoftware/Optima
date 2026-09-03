namespace Optima.Core.Launch;

public enum TrayWindowAction
{
    None,
    Hide,
    Restore,
}

/// <summary>Decides when the main window should hide to the tray during a game session and when it should come back.</summary>
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

    public void OnManualShow() => _autoHidden = false;
}
