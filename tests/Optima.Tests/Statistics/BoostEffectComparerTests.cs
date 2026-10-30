using Optima.Core.Boost;
using Optima.Core.Models;
using Optima.Core.Statistics;
using Xunit;

namespace Optima.Tests.Statistics;

public sealed class BoostEffectComparerTests
{
    private static SessionRecord Session(double low, double average, BoostSessionRecord? boost, double minutes = 20, int samples = 100)
        => new()
        {
            ProfileName = "Default",
            GamePackageId = "com.example",
            Duration = TimeSpan.FromMinutes(minutes),
            Stats = new SessionStats { OnePercentLowFps = low, AverageFps = average, SampleCount = samples },
            Boost = boost,
        };

    private static BoostSessionRecord Working(string player = "me") => new() { Player = player, On = [BoostFeatures.Timer], Applied = [BoostFeatures.Timer] };

    private static BoostSessionRecord Off(string player = "me") => new() { Player = player };

    private static BoostEffect Timer(IEnumerable<SessionRecord> sessions, string player = "me")
        => BoostEffectComparer.Compare(sessions, player).Single(e => e.Key == BoostFeatures.Timer);

    [Fact]
    public void ThreeSessionsEachWay_ShowTheMediansAndTheirDifference()
    {
        var effect = Timer(
        [
            Session(90, 200, Working()), Session(100, 210, Working()), Session(200, 400, Working()),
            Session(80, 190, Off()), Session(85, 195, Off()), Session(60, 150, Off()), Session(88, 199, Off()),
        ]);

        Assert.True(effect.HasEnough);
        Assert.Equal(3, effect.SessionsOn);
        Assert.Equal(4, effect.SessionsOff);
        Assert.Equal(100, effect.OnePercentLowOn);
        Assert.Equal(82.5, effect.OnePercentLowOff);
        Assert.Equal(17.5, effect.OnePercentLowDelta);
        Assert.Equal(210, effect.AverageFpsOn);
        Assert.Equal(192.5, effect.AverageFpsOff);
        Assert.Equal(17.5, effect.AverageFpsDelta);
    }

    [Fact]
    public void SessionsUnderEveryKeyThePlayerCarries_AreCountedTogether()
    {
        var sessions = new[]
        {
            Session(90, 200, Working("someone")), Session(100, 210, Working("123")), Session(200, 400, Working("123")),
            Session(80, 190, Off("someone")), Session(85, 195, Off("123")), Session(60, 150, Off("123")),
        };

        var effect = BoostEffectComparer.Compare(sessions, "123", "someone").Single(e => e.Key == BoostFeatures.Timer);

        Assert.Equal(3, effect.SessionsOn);
        Assert.Equal(3, effect.SessionsOff);
        Assert.Equal(2, Timer(sessions, "123").SessionsOn);
    }

    [Fact]
    public void FewerThanThreeOnEitherSide_IsTooFew_AndShowsNoDifference()
    {
        var effect = Timer(
        [
            Session(90, 200, Working()), Session(100, 210, Working()),
            Session(80, 190, Off()), Session(85, 195, Off()), Session(60, 150, Off()),
        ]);

        Assert.False(effect.HasEnough);
        Assert.Null(effect.OnePercentLowDelta);
        Assert.Null(effect.AverageFpsDelta);
    }

    [Fact]
    public void OlderSessionsAnotherPlayerShortSessionsAndOnesWithoutFps_AreLeftOut()
    {
        var effect = Timer(
        [
            Session(90, 200, null),
            Session(90, 200, Working("someone else")),
            Session(90, 200, Working(), minutes: 3),
            Session(90, 200, Working(), samples: 0),
            Session(80, 190, Off()),
        ]);

        Assert.Equal(0, effect.SessionsOn);
        Assert.Equal(1, effect.SessionsOff);
    }

    [Fact]
    public void TheSamePlayerIsMatchedWhateverTheCase()
        => Assert.Equal(1, Timer([Session(90, 200, Working("ME"))]).SessionsOn);

    [Fact]
    public void ASwitchThatWasOnButNeverWorked_CountsAsNeitherOnNorOff()
    {
        var effect = Timer([Session(90, 200, new BoostSessionRecord { Player = "me", On = [BoostFeatures.Timer] })]);

        Assert.Equal(0, effect.SessionsOn);
        Assert.Equal(0, effect.SessionsOff);
        Assert.Null(effect.OnePercentLowOn);
    }

    [Fact]
    public void EverySwitchHasARow_InTheBoostPagesOrder()
        => Assert.Equal(BoostFeatures.All.Select(f => f.Key), BoostEffectComparer.Compare([], "me").Select(e => e.Key));

    [Fact]
    public void TheMedianOfAnOddCountIsTheMiddle_OfAnEvenCountTheMeanOfTheTwo_AndOfNothingNull()
    {
        Assert.Equal(2, BoostEffectComparer.Median([3, 1, 2]));
        Assert.Equal(2.5, BoostEffectComparer.Median([4, 1, 2, 3]));
        Assert.Null(BoostEffectComparer.Median([]));
    }
}
