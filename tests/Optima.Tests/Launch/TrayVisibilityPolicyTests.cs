using Optima.Core.Launch;
using Xunit;

namespace Optima.Tests.Launch;

public sealed class TrayVisibilityPolicyTests
{
    [Fact]
    public void Hides_when_the_game_starts_running()
    {
        var policy = new TrayVisibilityPolicy();
        Assert.Equal(TrayWindowAction.None, policy.OnPhase(LaunchPhase.Validating));
        Assert.Equal(TrayWindowAction.None, policy.OnPhase(LaunchPhase.WaitingForGame));
        Assert.Equal(TrayWindowAction.Hide, policy.OnPhase(LaunchPhase.Monitoring));
    }

    [Fact]
    public void Hides_only_once_per_session()
    {
        var policy = new TrayVisibilityPolicy();
        Assert.Equal(TrayWindowAction.Hide, policy.OnPhase(LaunchPhase.Monitoring));
        Assert.Equal(TrayWindowAction.None, policy.OnPhase(LaunchPhase.Monitoring));
    }

    [Theory]
    [InlineData(LaunchPhase.Completed)]
    [InlineData(LaunchPhase.Failed)]
    public void Restores_at_session_end_after_an_automatic_hide(LaunchPhase endPhase)
    {
        var policy = new TrayVisibilityPolicy();
        policy.OnPhase(LaunchPhase.Monitoring);
        Assert.Equal(TrayWindowAction.Restore, policy.OnPhase(endPhase));
    }

    [Theory]
    [InlineData(LaunchPhase.Completed)]
    [InlineData(LaunchPhase.Failed)]
    public void Does_not_restore_when_the_window_was_never_hidden(LaunchPhase endPhase)
    {
        var policy = new TrayVisibilityPolicy();
        Assert.Equal(TrayWindowAction.None, policy.OnPhase(endPhase));
    }

    [Fact]
    public void Does_not_restore_after_the_user_showed_the_window_themselves()
    {
        var policy = new TrayVisibilityPolicy();
        policy.OnPhase(LaunchPhase.Monitoring);
        policy.OnManualShow();
        Assert.Equal(TrayWindowAction.None, policy.OnPhase(LaunchPhase.Completed));
    }

    [Fact]
    public void A_new_session_hides_again_after_a_manual_show()
    {
        var policy = new TrayVisibilityPolicy();
        policy.OnPhase(LaunchPhase.Monitoring);
        policy.OnManualShow();
        policy.OnPhase(LaunchPhase.Completed);
        Assert.Equal(TrayWindowAction.Hide, policy.OnPhase(LaunchPhase.Monitoring));
    }
}
