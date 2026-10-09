namespace Optima.Core.Theming;

/// <summary>
/// The whole motion vocabulary of the app: four lengths and one curve. Everything that moves uses
/// these, which is what makes a button, a page and the backdrop feel like one hand made them.
/// Templates reach them through the Flow attached properties, code through Motion.
/// </summary>
public static class MotionSpec
{
    /// <summary>Hover, press, a mark appearing: an answer to the pointer.</summary>
    public const int FastMs = 120;

    /// <summary>Something moving or opening: a page, a marker, a popup, a panel.</summary>
    public const int MoveMs = 220;

    /// <summary>A number or a meter going to its new value.</summary>
    public const int DataMs = 400;

    /// <summary>The backdrop only. Long, because it is behind everything and nobody waits for it.</summary>
    public const int AmbientMs = 1600;

    /// <summary>How far apart the sections of a page follow each other in.</summary>
    public const int StaggerMs = 40;

    public static double Clamp(double x) => Math.Max(0, Math.Min(1, x));

    /// <summary>The one curve: fast away, soft landing. 1 - (1 - t)^3.</summary>
    public static double EaseOut(double t)
    {
        var left = 1 - Clamp(t);
        return 1 - left * left * left;
    }

    /// <summary>For the two long set pieces (the dial's spin, the splash): slow at both ends.</summary>
    public static double EaseInOut(double t)
    {
        t = Clamp(t);
        return t < 0.5 ? 4 * t * t * t : 1 - Math.Pow(-2 * t + 2, 3) / 2;
    }
}

/// <summary>
/// A value that goes to wherever it is sent along <see cref="MotionSpec.EaseOut"/>, and can be sent
/// somewhere else on the way: it then sets off again from where it is, never from where it began.
/// That is what lets a marker be redirected mid-glide and a meter follow samples that arrive
/// before the last move has finished, without a jump.
///
/// The clock is given, not read: a control hands in its frame time, a test or a preview hands in
/// whatever time it likes.
/// </summary>
public sealed class Eased
{
    private double _from;
    private double _to;
    private double _startedAt;
    private double _seconds;
    private bool _set;

    /// <summary>Where it is at <paramref name="now"/> (seconds), on its way to <paramref name="target"/>.</summary>
    /// <param name="seconds">How long a move takes. Zero, or less, arrives at once: that is "animations off".</param>
    public double Get(double target, double seconds, double now)
    {
        // The first value is where it starts, not something to travel to from zero.
        if (!_set || seconds <= 0)
        {
            _set = true;
            _from = _to = target;
            _seconds = 0;
            return target;
        }
        if (target != _to)
        {
            _from = At(now);
            _to = target;
            _startedAt = now;
            _seconds = seconds;
        }
        return At(now);
    }

    /// <summary>True while it has not arrived: the owner keeps drawing frames for as long as this holds.</summary>
    public bool Moving(double now) => _set && _seconds > 0 && now - _startedAt < _seconds;

    private double At(double now)
        => _seconds <= 0
            ? _to
            : _from + (_to - _from) * MotionSpec.EaseOut((now - _startedAt) / _seconds);
}
