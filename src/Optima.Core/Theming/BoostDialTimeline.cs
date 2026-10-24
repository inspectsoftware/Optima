using static Optima.Core.Theming.MotionSpec;

namespace Optima.Core.Theming;

public enum BoostDialMode
{
    Off,
    Enabling,
    On,
    Disabling,
}

/// <summary>What the nameplate on the hub reads.</summary>
public enum BoostDialLabel
{
    Boost,
    Igniting,
    Engaged,
    Cooling,
}

/// <summary>
/// One moment of the BOOST gear machine. <see cref="Drive"/> is the drive gear's angle in degrees,
/// clockwise; the six small gears always turn at -3 times it, which is what keeps their teeth in mesh.
/// <see cref="Heat"/> is the furnace behind the grille, <see cref="Lit"/> how far the plates, their
/// seams and the nameplate have gone from cold scrap to glowing, <see cref="Flare"/> the flash as the
/// train locks. The shake is in the dial's 400-unit square. <see cref="ParticleMs"/> is the clock the
/// sparks and embers run on, which carries on without a jump from the spin-up into the steady run.
/// Every level is 0 to 1.
/// </summary>
public readonly record struct BoostDialFrame(
    double Drive,
    double Heat,
    double Lit,
    double Aura,
    double Sparks,
    double Embers,
    double Flare,
    double ShakeX,
    double ShakeY,
    double ParticleMs,
    BoostDialLabel Label,
    double LabelOpacity);

/// <summary>
/// The BOOST dial's animation as a pure function of time, so the control only draws and the timings
/// can be tested. The gears land on whole turns at the end of every run, and every turn of the drive
/// gear is three of a small one, which is why the end of a run is indistinguishable from rest.
/// </summary>
public static class BoostDialTimeline
{
    public const double EnableMs = 2400;
    public const double DisableMs = 900;

    /// <summary>One turn of the drive gear while engaged; the small gears turn once every third of it.</summary>
    public const double SteadyTurnMs = 12000;

    public const int Gears = 6;
    public const int Plates = 9;

    /// <summary>
    /// The machine at <paramref name="t"/> milliseconds into <paramref name="mode"/>. A cool-down
    /// starts from wherever the steady run was, so it also needs <paramref name="ranMs"/>: how long
    /// the machine had been engaged when it was switched off.
    /// </summary>
    public static BoostDialFrame At(BoostDialMode mode, double t, double ranMs = 0)
    {
        switch (mode)
        {
            case BoostDialMode.On:
                return new BoostDialFrame(
                    SteadyDrive(t), SteadyHeat(t), 1, SteadyAura(t), 1, 1, 0, 0, 0, EnableMs + t, BoostDialLabel.Engaged, 1);

            case BoostDialMode.Enabling:
            {
                // Three turns, slow at both ends, so the train comes down onto its tooth grid as it locks.
                var drive = 1080 * EaseInOut(t / EnableMs);
                var flare = Math.Sin(Math.PI * Clamp((t - 2100) / 300));
                var heat = 0.75 * EaseInOut(t / 2100) + 0.25 * EaseOut((t - 2150) / 250);
                var aura = 0.8 * EaseInOut((t - 300) / 1800) + 0.2 * EaseOut((t - 2150) / 250);

                // A rattle while the plates clamp and the train winds up, and one jolt as it locks.
                var (shakeX, shakeY) = Rattle(t, 1.1 * (1 - Clamp((t - 1200) / 600)) + 1.4 * flare);

                BoostDialLabel label;
                double labelOpacity;
                if (t < 120)
                {
                    label = BoostDialLabel.Boost;
                    labelOpacity = 1 - Clamp(t / 120);
                }
                else if (t < 2200)
                {
                    label = BoostDialLabel.Igniting;
                    labelOpacity = Math.Min(Clamp((t - 120) / 150), 1 - Clamp((t - 2080) / 120));
                }
                else
                {
                    label = BoostDialLabel.Engaged;
                    labelOpacity = Clamp((t - 2200) / 150);
                }
                return new BoostDialFrame(
                    drive, heat, EaseOut((t - 150) / 600), aura, Clamp((t - 600) / 300), Clamp((t - 2150) / 250),
                    flare, shakeX, shakeY, t, label, labelOpacity);
            }

            case BoostDialMode.Disabling:
            {
                // One turn back past the last whole turn, so it always lands where rest is.
                var from = SteadyDrive(ranMs);
                var drive = from - (from + 360) * EaseOut(t / DisableMs);

                BoostDialLabel label;
                double labelOpacity;
                if (t < 120)
                {
                    label = BoostDialLabel.Engaged;
                    labelOpacity = 1 - Clamp(t / 120);
                }
                else if (t < 720)
                {
                    label = BoostDialLabel.Cooling;
                    labelOpacity = Math.Min(Clamp((t - 120) / 120), 1 - Clamp((t - 600) / 120));
                }
                else
                {
                    label = BoostDialLabel.Boost;
                    labelOpacity = Clamp((t - 720) / 150);
                }
                return new BoostDialFrame(
                    drive,
                    SteadyHeat(ranMs) * (1 - EaseOut(t / 700)),
                    1 - EaseOut(t / 500),
                    SteadyAura(ranMs) * (1 - EaseOut(t / 500)),
                    1 - Clamp(t / 200),
                    1 - Clamp(t / 400),
                    0, 0, 0, EnableMs + ranMs + t, label, labelOpacity);
            }

            default:
                return new BoostDialFrame(0, 0, 0, 0, 0, 0, 0, 0, 0, 0, BoostDialLabel.Boost, 1);
        }
    }

    /// <summary>
    /// How far scrap plate <paramref name="plate"/> sits out from the rim: 1 loosened, 0 clamped on
    /// (a little below 0 as it slams in). They clamp one after another and come loose in reverse.
    /// </summary>
    public static double PlateOut(BoostDialMode mode, double t, int plate) => mode switch
    {
        BoostDialMode.On => 0,
        BoostDialMode.Enabling => 1 - BackOut((t - 60 * plate) / 550),
        BoostDialMode.Disabling => Cube(Clamp((t - 40 * (Plates - 1 - plate)) / 560)),
        _ => 1,
    };

    /// <summary>
    /// How far small gear <paramref name="gear"/> has gone from steel to hot brass, if its feature is
    /// ticked. The heat runs round the ring from the top on the way up and drains back the other way.
    /// </summary>
    public static double GearHeat(BoostDialMode mode, double t, int gear) => mode switch
    {
        BoostDialMode.On => 1,
        BoostDialMode.Enabling => EaseOut((t - 250 - 260 * gear) / 300),
        BoostDialMode.Disabling => 1 - EaseOut((t - 60 * (Gears - 1 - gear)) / 500),
        _ => 0,
    };

    private static double SteadyDrive(double t) => 360 * t / SteadyTurnMs % 360;

    /// <summary>The furnace flickers under full heat; both waves start at their top, where the spin-up leaves it.</summary>
    private static double SteadyHeat(double t)
        => 1 - 0.16 * (0.6 * (1 - Math.Cos(2 * Math.PI * t / 2600)) / 2 + 0.4 * (1 - Math.Cos(2 * Math.PI * t / 730)) / 2);

    private static double SteadyAura(double t) => 0.8 + 0.2 * Math.Cos(2 * Math.PI * t / 3400);

    private static (double X, double Y) Rattle(double t, double amplitude)
        => (amplitude * Math.Sin(2 * Math.PI * t / 120), amplitude * Math.Sin(2 * Math.PI * t / 87 + 1.1));

    /// <summary>Past the target by about a tenth and back: a plate slamming home.</summary>
    private static double BackOut(double x)
    {
        var u = Clamp(x) - 1;
        return 1 + 2.70158 * u * u * u + 1.70158 * u * u;
    }

    private static double Cube(double x) => x * x * x;
}
