namespace Optima.Core.Theming;

public enum BoostDialMode
{
    Off,
    Enabling,
    On,
    Disabling,
}

public enum BoostDialLabel
{
    Idle,
    Arming,
    Enabled,
}

/// <summary>
/// One moment of the BOOST dial. <see cref="Detach"/> is how far the ring segments are off the disc
/// (0 home, 1 fully out, slightly negative during the snap-back overshoot); the rotations are in
/// degrees; <see cref="Gradient"/> is how far gold has turned into the violet-to-green gradient.
/// </summary>
public readonly record struct BoostDialFrame(
    double Detach,
    double OuterRotation,
    double InnerRotation,
    double Progress,
    double Gradient,
    double Dip,
    double PulseScale,
    double PulseOpacity,
    BoostDialLabel Label,
    double LabelOpacity);

/// <summary>
/// The BOOST dial's animation as a pure function of time, so the control only draws and the
/// timings can be tested. The ring is six segments in two sets of three; both rotations end on a
/// multiple of 120 degrees, which is why the end of a run is indistinguishable from rest.
/// </summary>
public static class BoostDialTimeline
{
    public const double EnableMs = 2700;
    public const double DisableMs = 600;

    public static BoostDialFrame At(BoostDialMode mode, double t)
    {
        switch (mode)
        {
            case BoostDialMode.On:
                return new BoostDialFrame(0, 0, 0, 1, 1, 0, 1, 0, BoostDialLabel.Enabled, 1);

            case BoostDialMode.Enabling:
            {
                var dip = t < 300 ? Math.Sin(Math.PI * Clamp(t / 300)) : 0;
                var detach = EaseOut(Clamp((t - 150) / 450)) * (1 - Back(Clamp((t - 2000) / 400)));
                var spin = EaseInOut(Clamp((t - 600) / 1800));
                var progress = Clamp((t - 600) / 1400);
                var gradient = progress * progress * (3 - 2 * progress);
                var pulse = Clamp((t - 2000) / 700);
                var pulseOpacity = t >= 2000 ? 0.7 * (1 - pulse) : 0;

                BoostDialLabel label;
                double labelOpacity;
                if (t < 600)
                {
                    label = BoostDialLabel.Idle;
                    labelOpacity = 1 - Clamp((t - 450) / 150);
                }
                else if (t < 2200)
                {
                    label = BoostDialLabel.Arming;
                    labelOpacity = Math.Min(Clamp((t - 600) / 150), 1 - Clamp((t - 2050) / 150));
                }
                else
                {
                    label = BoostDialLabel.Enabled;
                    labelOpacity = Clamp((t - 2200) / 250);
                }
                return new BoostDialFrame(
                    detach, 1080 * spin, -720 * spin, progress, gradient, dip, 1 + 0.5 * pulse, pulseOpacity, label, labelOpacity);
            }

            case BoostDialMode.Disabling:
            {
                var u = EaseInOut(Clamp(t / DisableMs));
                var showEnabled = t < 300;
                return new BoostDialFrame(
                    Math.Sin(Math.PI * u) * 0.35, -120 * u, 120 * u, 1 - u, 1 - u, 0, 1, 0,
                    showEnabled ? BoostDialLabel.Enabled : BoostDialLabel.Idle,
                    showEnabled ? 1 - Clamp(t / 250) : Clamp((t - 300) / 250));
            }

            default:
                return new BoostDialFrame(0, 0, 0, 0, 0, 0, 1, 0, BoostDialLabel.Idle, 1);
        }
    }

    private static double Clamp(double x) => Math.Max(0, Math.Min(1, x));

    private static double EaseOut(double x) => 1 - Math.Pow(1 - x, 3);

    private static double EaseInOut(double x) => x < 0.5 ? 4 * x * x * x : 1 - Math.Pow(-2 * x + 2, 3) / 2;

    /// <summary>Ease-out with overshoot: passes 1 and settles back, which is the snap of the segments locking home.</summary>
    private static double Back(double x) => 1 + 2.70158 * Math.Pow(x - 1, 3) + 1.70158 * Math.Pow(x - 1, 2);
}
