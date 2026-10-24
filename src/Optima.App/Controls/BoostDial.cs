using System.Diagnostics;
using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using Optima.App.Services;
using Optima.Core.Theming;

namespace Optima.App.Controls;

/// <summary>
/// The BOOST master switch, drawn as a machine: a steel drive gear turning six small gears, one per
/// feature, inside a ring of scrap-metal plates, with a furnace behind the hub. A click fires it up:
/// the plates clamp onto the rim, the train spins up and locks, the ticked gears turn brass and throw
/// sparks. Everything is drawn here on a 400-unit square and scaled to the control;
/// <see cref="BoostDialTimeline"/> decides what each moment looks like. The ignition only plays for
/// a click, never for a value that arrives from settings, and nothing moves while motion is off (the
/// game is running, or Windows animations are disabled).
/// </summary>
public sealed class BoostDial : FrameworkElement
{
    public static readonly DependencyProperty IsOnProperty = DependencyProperty.Register(
        nameof(IsOn), typeof(bool), typeof(BoostDial),
        new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender, OnIsOnChanged));

    public static readonly DependencyProperty FeatureCountProperty = DependencyProperty.Register(
        nameof(FeatureCount), typeof(int), typeof(BoostDial),
        new FrameworkPropertyMetadata(5, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>
    /// Which of the six features are ticked, one bit each in the page's order: priority guard,
    /// memory cleaner, timer resolution, background programs, core parking, graphics adapter.
    /// </summary>
    public static readonly DependencyProperty ArmedMaskProperty = DependencyProperty.Register(
        nameof(ArmedMask), typeof(int), typeof(BoostDial),
        new FrameworkPropertyMetadata(0b11_1111, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty CommandProperty = DependencyProperty.Register(
        nameof(Command), typeof(ICommand), typeof(BoostDial), new PropertyMetadata(null));

    private const double Unit = 400;
    private const double Deg = Math.PI / 180;
    private static readonly Point Center = new(200, 200);

    // The train. The pitch circles are 91.35 and 30.05 across; the centres sit a little further
    // apart than that so the teeth clear, and 36 teeth against 12 is the 3:1 the timeline turns them at.
    private const double GearDistance = 122.7;
    private static readonly Geometry DriveGear = Gear(36, 85.6, 97.1, 58, 8, 7, 72);
    private static readonly Geometry SmallGear = Gear(12, 24.3, 35.8, 4, 4, 4.5, 15);
    private static readonly Geometry HexNut = Lines([.. Enumerable.Range(0, 6).Select(i => Polar(default, 5, i * 60 * Deg))], closed: true);

    /// <summary>
    /// Gear k sits at -90 + 60k degrees, clockwise from the top. A drive tooth points straight at
    /// each of them, so each one starts turned to face it with a gap: half a tooth (15 degrees) off
    /// pointing back at the centre.
    /// </summary>
    private static readonly (Point Centre, double Angle)[] Train =
        [.. Enumerable.Range(0, BoostDialTimeline.Gears).Select(k => -90.0 + 60 * k)
            .Select(a => (Polar(Center, GearDistance, a * Deg), a + 165))];

    /// <summary>How far a plate sits out from its place on the rim while the machine is cold.</summary>
    private const double PlateLoose = 7;
    private static readonly Plate[] Armour = BuildArmour();
    private static readonly Geometry Ticks = BuildTicks();
    private static readonly Geometry Grille = BuildGrille();

    private static readonly Point Across = new(1, 1);
    private static readonly Point Down = new(0, 1);

    // Brushed metal: the bands of a lit cylinder, laid diagonally across each part. They turn with
    // the gears, so a turning gear catches the light.
    private static readonly Brush Steel = Metal(Across, (0xDDE2E7, 0), (0x7C848D, 0.28), (0xC3C9CF, 0.5), (0x565C63, 0.74), (0xA3AAB1, 1));
    private static readonly Brush Brass = Metal(Across, (0xFCE9AE, 0), (0xBF8A2E, 0.3), (0xF2C66C, 0.52), (0x85561A, 0.76), (0xD9A441, 1));
    private static readonly Brush Rust = Metal(Across, (0x7A5C4B, 0), (0x3E302A, 0.35), (0x634A3B, 0.55), (0x2C221E, 0.8), (0x50403A, 1));
    private static readonly Brush Gunmetal = Metal(Across, (0x6A7078, 0), (0x2D3136, 0.4), (0x4E545B, 0.62), (0x1C1F23, 1));
    private static readonly Brush Scrap = Metal(Across, (0x4D4640, 0), (0x26221F, 0.4), (0x574A3F, 0.6), (0x1F1C1A, 1));
    private static readonly Brush NameplateFill = Metal(Down, (0xE2E6EA, 0), (0x9AA1A8, 0.48), (0x6E757C, 0.52), (0xB9BFC5, 1));

    private static readonly Brush BackplateFill = Round(0.5, 0.4, 0.78, (0x17191C, 1, 0), (0x0B0C0E, 1, 0.7));
    private static readonly Brush BackplateDepth = Round(0.5, 0.5, 0.5, (0, 0, 0.74), (0, 0.55, 0.93), (0, 0.85, 1));
    private static readonly Brush BackplateShadow = Round(0.5, 0.5, 0.5, (0, 0.6, 0.8), (0, 0, 1));
    private static readonly Brush HubFill = Round(0.35, 0.28, 0.97, (0xD3D8DD, 1, 0), (0x737A81, 1, 0.42), (0x2F3338, 1, 0.82));
    private static readonly Brush HubShadow = Round(0.5, 0.5, 0.5, (0, 0.75, 0.72), (0, 0, 1));
    private static readonly Brush RivetFill = Round(0.35, 0.3, 0.955, (0xF0F3F5, 1, 0), (0x6B7279, 1, 0.6), (0x2A2D31, 1, 1));
    private static readonly Brush ScrewFill = Round(0.35, 0.3, 0.955, (0xFFFFFF, 1, 0), (0x6B7279, 1, 0.6), (0x2A2D31, 1, 1));
    private static readonly Brush FurnaceHeat = Round(0.5, 0.62, 0.796, (0xFFF4D6, 1, 0), (0xFFB347, 1, 0.28), (0xFF6A1A, 1, 0.56), (0x5A1A00, 1, 0.84));
    private static readonly Brush WhiteHot = Round(0.5, 0.55, 0.6, (0xFFFFFF, 0.95, 0), (0xFFE9B8, 0.6, 0.45), (0xFFB347, 0, 1));
    private static readonly Brush FurnaceDepth = Round(0.5, 0.5, 0.5, (0, 0, 0.68), (0, 0.85, 1));
    private static readonly Brush AuraFill = Round(0.5, 0.5, 0.5, (0xFF8C28, 0.4, 0), (0xE8B45A, 0.12, 0.62), (0xE8B45A, 0, 0.99));

    private static readonly Color Ink = Rgb(0x0A0A0A);
    private static readonly Color Amber = Rgb(0xFF8C28);
    private static readonly Color Ember = Rgb(0xFFB347);
    private static readonly Brush FurnaceBack = Solid(Rgb(0x120804), 1);
    private static readonly Brush GrilleFill = Solid(Rgb(0x0E0E0E), 0.92);
    private static readonly Brush EmberCore = Solid(Ember, 1);
    private static readonly Brush EmberHalo = Solid(Rgb(0xFF7A1A), 0.35);
    private static readonly Brush StampShade = Solid(Colors.White, 0.55);
    private static readonly Brush StampInk = Solid(Rgb(0x2A2D31), 1);
    private static readonly Brush MoltenShade = Solid(Rgb(0x3A1A00), 1);
    private static readonly Brush Molten = Solid(Rgb(0xFF8A1A), 1);
    private static readonly Brush Chalk = Solid(Rgb(0xF2E6D2), 1);
    private static readonly Brush ChalkShade = Solid(Colors.Black, 0.85);

    private static readonly Pen BackplateEdge = Frozen(new Pen(Solid(Rgb(0x23262A), 1), 1));
    private static readonly Pen PlateEdge = Outline(1.4);
    private static readonly Pen SmallGearEdge = Outline(1.1);
    private static readonly Pen DriveGearEdge = Outline(1.3);
    private static readonly Pen HubEdge = Outline(2);
    private static readonly Pen RivetEdge = Outline(1);
    private static readonly Pen NutEdge = Outline(0.8);
    private static readonly Pen CrackPen = Outline(1.2, round: true);
    private static readonly Pen ColdSeam = Outline(2, round: true, Rgb(0x2E2A27));
    private static readonly Pen HotSeam = Outline(2, round: true, Ember);
    private static readonly Pen TickPen = Frozen(new Pen(Solid(Rgb(0xDCE2E8), 0.22), 1.6));
    private static readonly Pen SmallGearRing = Frozen(new Pen(Solid(Colors.White, 0.16), 1));
    private static readonly Pen DriveGearBand = Frozen(new Pen(Solid(Colors.White, 0.2), 1));
    private static readonly Pen DriveGearGroove = Frozen(new Pen(Solid(Colors.Black, 0.6), 2));
    private static readonly Pen HubRimLight = Frozen(new Pen(Fade(Colors.White, 0.45), 1.2));
    private static readonly Pen NameplateLight = Frozen(new Pen(Solid(Colors.White, 0.6), 1));
    private static readonly Pen[] SeamGlow = [Frozen(new Pen(Solid(Ember, 0.1), 10)), Frozen(new Pen(Solid(Ember, 0.22), 5))];
    private static readonly Pen[] MoltenGlow = [Frozen(new Pen(Solid(Rgb(0xFF781E), 0.18), 7)), Frozen(new Pen(Solid(Rgb(0xFF781E), 0.35), 3))];
    // Sparks fly across bright steel as much as dark plate, so they carry their own orange halo.
    private static readonly Pen[] SparkGlow = [Streak(Rgb(0xFF4A00), 0.35, 7), Streak(Rgb(0xFF7A10), 0.85, 3.6), Streak(Rgb(0xFFF1D0), 1, 1.5)];
    private static readonly Brush SparkTip = Solid(Colors.White, 1);

    // Soft edges: wide faint outlines and the shape itself under the part stand in for a blur,
    // which would cost a bitmap effect on every frame.
    private static readonly Pen[] ShadowBlur = Blur(Colors.Black, 7, 0.07, 6);
    private static readonly Brush ShadowCore = Solid(Colors.Black, 0.55);
    private static readonly Pen[] GlowBlur = Blur(Amber, 10, 0.05, 7);
    private static readonly Brush GlowCore = Solid(Amber, 0.3);

    // A gear blurred is round, so its shadow and glow are discs that fade out past the teeth: no
    // outline of a turning gear to stroke again on every frame.
    private static readonly Brush SmallGearShadow = Falloff(Colors.Black, 0.7, 33.0 / 42);
    private static readonly Brush SmallGearGlow = Falloff(Amber, 0.6, 34.0 / 46);
    private static readonly Brush DriveGearShadow = Falloff(Colors.Black, 0.8, 94.0 / 108);
    private static readonly Brush DriveGearGlow = Falloff(Amber, 0.4, 95.0 / 110);

    private static readonly Rect NameplateRect = new(140, 184, 120, 32);
    private static readonly Geometry NameplateShape = Frozen(new RectangleGeometry(NameplateRect, 3, 3));

    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly Eased _hover = new();
    private BoostDialMode _mode;
    private double _modeStartedMs;
    private double _heldMs;
    // How long the machine had been engaged when it was switched off: the cool-down starts from there.
    private double _ranMs;
    private bool _animateNextChange;
    private bool _hooked;

    // The lettering is laid out once per label and drawn every frame; rebuilt when the window lands on another DPI.
    private double _textDpi;
    private readonly Geometry?[] _titles = new Geometry?[4];
    private readonly Geometry?[] _subs = new Geometry?[4];

    public BoostDial()
    {
        Focusable = true;
        Cursor = Cursors.Hand;
        FocusVisualStyle = null;
        Loaded += (_, _) =>
        {
            Motion.Changed -= Hook;
            Motion.Changed += Hook;
            Hook();
        };
        Unloaded += (_, _) =>
        {
            Motion.Changed -= Hook;
            Unhook();
        };
        IsVisibleChanged += (_, _) =>
        {
            if (IsVisible)
            {
                Hook();
            }
            else
            {
                Unhook();
            }
        };
    }

    /// <summary>Whether Boost is on. Bound one way from the view model; a click asks <see cref="Command"/> to change it.</summary>
    public bool IsOn
    {
        get => (bool)GetValue(IsOnProperty);
        set => SetValue(IsOnProperty, value);
    }

    /// <summary>How many features the switch arms. Not drawn any more: each ticked feature is a gear (<see cref="ArmedMask"/>).</summary>
    public int FeatureCount
    {
        get => (int)GetValue(FeatureCountProperty);
        set => SetValue(FeatureCountProperty, value);
    }

    public int ArmedMask
    {
        get => (int)GetValue(ArmedMaskProperty);
        set => SetValue(ArmedMaskProperty, value);
    }

    public ICommand? Command
    {
        get => (ICommand?)GetValue(CommandProperty);
        set => SetValue(CommandProperty, value);
    }

    /// <summary>Draws one fixed moment of the current mode instead of the running clock; for rendering stills of the beats.</summary>
    internal double? FrozenAtMs { get; set; }

    /// <summary>How far into the current mode the machine is. While the frame hook is let go, it holds where it was.</summary>
    private double ModeMs => _hooked ? _clock.Elapsed.TotalMilliseconds - _modeStartedMs : _heldMs;

    private static void OnIsOnChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var dial = (BoostDial)d;
        var on = (bool)e.NewValue;
        // Only a hooked dial plays the run: nothing else would advance it, and a click is ignored
        // for as long as one is in flight.
        if (dial._animateNextChange && dial._hooked && Motion.Enabled)
        {
            // The cool-down picks the gears up wherever the steady run had them.
            dial._ranMs = dial._mode == BoostDialMode.On ? dial.ModeMs : 0;
            dial._mode = on ? BoostDialMode.Enabling : BoostDialMode.Disabling;
        }
        else
        {
            dial._mode = on ? BoostDialMode.On : BoostDialMode.Off;
        }
        dial._modeStartedMs = dial._clock.Elapsed.TotalMilliseconds;
        dial._heldMs = 0;
        dial._animateNextChange = false;
        dial.InvalidateVisual();
    }

    /// <summary>
    /// Hooked only while motion is on. A subscriber keeps the frame tick running, and with BOOST
    /// open the dial was the one thing ticking every frame of a window that was unfocused,
    /// minimized or behind a game.
    /// </summary>
    private void Hook()
    {
        if (!_hooked && IsLoaded && IsVisible && Motion.Enabled)
        {
            // The clock carries on from where it was let go, so the gears turn on instead of jumping.
            _modeStartedMs = _clock.Elapsed.TotalMilliseconds - _heldMs;
            CompositionTarget.Rendering += OnRendering;
            _hooked = true;
        }
    }

    private void Unhook()
    {
        if (!_hooked)
        {
            return;
        }
        _heldMs = ModeMs;
        CompositionTarget.Rendering -= OnRendering;
        _hooked = false;
        // The hook only comes back with motion on, so a run in flight ends where it was going
        // instead of waiting for a frame that may never come.
        if (_mode is BoostDialMode.Enabling or BoostDialMode.Disabling)
        {
            _mode = _mode == BoostDialMode.Enabling ? BoostDialMode.On : BoostDialMode.Off;
            _heldMs = 0;
        }
        InvalidateVisual();
    }

    private void OnRendering(object? sender, EventArgs e)
    {
        // With motion off nothing here moves: one still frame, and the frame tick is let go until
        // motion is back.
        if (!Motion.Enabled)
        {
            Unhook();
            return;
        }
        if (_mode is BoostDialMode.Enabling or BoostDialMode.Disabling)
        {
            var length = _mode == BoostDialMode.Enabling ? BoostDialTimeline.EnableMs : BoostDialTimeline.DisableMs;
            if (ModeMs >= length)
            {
                _mode = _mode == BoostDialMode.Enabling ? BoostDialMode.On : BoostDialMode.Off;
                // The steady run starts from the moment the ignition ended, not from this frame, so
                // the gears carry straight on from where the ignition left them.
                _modeStartedMs += length;
            }
        }
        // Something is always turning, flickering or settling, so every frame is drawn.
        InvalidateVisual();
    }

    private void Toggle()
    {
        if (_mode is BoostDialMode.Enabling or BoostDialMode.Disabling)
        {
            return;
        }
        if (Command is { } command && command.CanExecute(null))
        {
            _animateNextChange = true;
            command.Execute(null);
        }
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        Focus();
        Toggle();
        e.Handled = true;
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Key is Key.Space or Key.Enter)
        {
            Toggle();
            e.Handled = true;
        }
    }

    protected override void OnMouseEnter(MouseEventArgs e)
    {
        base.OnMouseEnter(e);
        InvalidateVisual();
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        base.OnMouseLeave(e);
        InvalidateVisual();
    }

    protected override void OnIsKeyboardFocusedChanged(DependencyPropertyChangedEventArgs e)
    {
        base.OnIsKeyboardFocusedChanged(e);
        InvalidateVisual();
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var side = Math.Min(
            double.IsInfinity(availableSize.Width) ? 200 : availableSize.Width,
            double.IsInfinity(availableSize.Height) ? 200 : availableSize.Height);
        return new Size(side, side);
    }

    protected override void OnRender(DrawingContext dc)
    {
        var side = Math.Min(ActualWidth, ActualHeight);
        if (side <= 0)
        {
            return;
        }

        var t = FrozenAtMs ?? ModeMs;
        var frame = BoostDialTimeline.At(_mode, t, _ranMs);
        // Pointing at a cold machine lights its pilot flame.
        var hover = _hover.Get(IsMouseOver ? 1 : 0, Motion.Seconds(MotionSpec.MoveMs), _clock.Elapsed.TotalSeconds);
        var pilot = 0.22 * hover * (1 - frame.Lit);

        var scale = side / Unit;
        dc.PushTransform(new MatrixTransform(
            scale, 0, 0, scale, (ActualWidth - side) / 2 + frame.ShakeX * scale, (ActualHeight - side) / 2 + frame.ShakeY * scale));

        // The whole square takes the click, including the gaps between the plates.
        dc.DrawEllipse(Brushes.Transparent, null, Center, 200, 200);

        var aura = Math.Min(1, frame.Aura + 0.5 * frame.Flare + pilot);
        if (aura > 0.004)
        {
            dc.PushOpacity(aura);
            dc.DrawEllipse(AuraFill, null, Center, 240, 240);
            dc.Pop();
        }

        dc.DrawEllipse(BackplateShadow, null, new Point(200, 210), 190, 190);
        dc.DrawEllipse(BackplateFill, BackplateEdge, Center, 158, 158);
        dc.DrawEllipse(BackplateDepth, null, Center, 158, 158);
        dc.DrawGeometry(null, TickPen, Ticks);

        DrawArmour(dc, t, frame);
        DrawTrain(dc, t, frame);
        DrawSparks(dc, t, frame);
        DrawHub(dc, frame, Math.Max(frame.Heat, pilot));
        DrawNameplate(dc, frame);
        DrawEmbers(dc, frame);

        if (IsKeyboardFocused)
        {
            var text = TryFindResource("Brush.Text") as Brush ?? Brushes.White;
            dc.DrawEllipse(null, new Pen(text, 2) { DashStyle = new DashStyle([2, 3], 0) }, Center, 161, 161);
        }
        dc.Pop();
    }

    /// <summary>
    /// The nine plates round the rim. Cold they sit loose, straight out from their places; firing
    /// up clamps them on, and the seams along their inner edges glow.
    /// </summary>
    private void DrawArmour(DrawingContext dc, double t, BoostDialFrame frame)
    {
        Span<Vector> shift = stackalloc Vector[Armour.Length];
        for (var k = 0; k < Armour.Length; k++)
        {
            shift[k] = Armour[k].Out * (PlateLoose * BoostDialTimeline.PlateOut(_mode, t, k));
        }

        // Every shadow first, so no plate's shadow falls across its neighbour.
        for (var k = 0; k < Armour.Length; k++)
        {
            dc.PushTransform(new TranslateTransform(shift[k].X, shift[k].Y + 4));
            Halo(dc, Armour[k].Shape, ShadowBlur, ShadowCore, 1 - 0.6 * frame.Lit);
            dc.Pop();
            dc.PushTransform(new TranslateTransform(shift[k].X, shift[k].Y));
            Halo(dc, Armour[k].Shape, GlowBlur, GlowCore, 0.55 * frame.Lit + 0.6 * frame.Flare);
            dc.Pop();
        }

        for (var k = 0; k < Armour.Length; k++)
        {
            var plate = Armour[k];
            dc.PushTransform(new TranslateTransform(shift[k].X, shift[k].Y));
            dc.DrawGeometry(Scrap, null, plate.Shape);
            if (frame.Lit > 0.004)
            {
                dc.PushOpacity(frame.Lit);
                dc.DrawGeometry(Gunmetal, null, plate.Shape);
                dc.Pop();
            }
            dc.DrawGeometry(null, PlateEdge, plate.Shape);
            dc.DrawGeometry(null, ColdSeam, plate.Seam);
            if (frame.Lit > 0.004)
            {
                dc.PushOpacity(frame.Lit);
                foreach (var pen in SeamGlow)
                {
                    dc.DrawGeometry(null, pen, plate.Seam);
                }
                dc.DrawGeometry(null, HotSeam, plate.Seam);
                dc.Pop();
            }
            dc.DrawGeometry(null, CrackPen, plate.Crack);
            dc.DrawEllipse(Steel, RivetEdge, plate.RivetA, 3.4, 3.4);
            dc.DrawEllipse(Steel, RivetEdge, plate.RivetB, 3.4, 3.4);
            dc.Pop();
        }
    }

    /// <summary>
    /// The six small gears and the drive gear on top of them. A ticked feature's gear is bright
    /// steel, going brass as the heat reaches it; an unticked one is rust and stays cold.
    /// </summary>
    private void DrawTrain(DrawingContext dc, double t, BoostDialFrame frame)
    {
        var armed = ArmedMask;
        var small = -3 * frame.Drive;

        for (var k = 0; k < Train.Length; k++)
        {
            var hot = (armed >> k & 1) != 0 ? BoostDialTimeline.GearHeat(_mode, t, k) : 0;
            Disc(dc, SmallGearShadow, Train[k].Centre + new Vector(0, 3), 42, 1 - hot);
            Disc(dc, SmallGearGlow, Train[k].Centre, 46, hot);
        }

        for (var k = 0; k < Train.Length; k++)
        {
            var ticked = (armed >> k & 1) != 0;
            var hot = ticked ? BoostDialTimeline.GearHeat(_mode, t, k) : 0;
            dc.PushTransform(Place(Train[k].Centre, Train[k].Angle + small));
            dc.DrawGeometry(ticked ? Steel : Rust, null, SmallGear);
            if (hot > 0.004)
            {
                dc.PushOpacity(hot);
                dc.DrawGeometry(Brass, null, SmallGear);
                dc.Pop();
            }
            dc.DrawGeometry(null, SmallGearEdge, SmallGear);
            dc.DrawEllipse(null, SmallGearRing, default, 21, 21);
            dc.DrawEllipse(Gunmetal, RivetEdge, default, 9.5, 9.5);
            dc.DrawGeometry(Steel, NutEdge, HexNut);
            dc.Pop();
        }

        Disc(dc, DriveGearShadow, Center + new Vector(0, 6), 108, 1 - 0.7 * frame.Lit);
        Disc(dc, DriveGearGlow, Center, 110, frame.Lit + frame.Flare);
        dc.PushTransform(Place(Center, frame.Drive));
        dc.DrawGeometry(Steel, DriveGearEdge, DriveGear);
        dc.DrawEllipse(null, DriveGearBand, default, 82, 82);
        dc.DrawEllipse(null, DriveGearGroove, default, 62, 62);
        dc.Pop();
    }

    /// <summary>
    /// Sparks thrown off along the drive gear's turn at every mesh where a ticked gear is hot.
    /// Each is drawn as a short streak from where it was a moment ago, which is what makes it read
    /// as flying rather than as a dot.
    /// </summary>
    private void DrawSparks(DrawingContext dc, double t, BoostDialFrame frame)
    {
        if (frame.Sparks <= 0.004)
        {
            return;
        }
        var armed = ArmedMask;
        for (var k = 0; k < Train.Length; k++)
        {
            var hot = (armed >> k & 1) != 0 ? BoostDialTimeline.GearHeat(_mode, t, k) : 0;
            if (hot <= 0.004)
            {
                continue;
            }
            var angle = (-90 + 60 * k) * Deg;
            var radial = new Vector(Math.Cos(angle), Math.Sin(angle));
            var along = new Vector(-radial.Y, radial.X);
            var mesh = Center + radial * 92;
            for (var j = 0; j < 3; j++)
            {
                var life = Phase(frame.ParticleMs - 210 * j - 70 * k, 550 + 150 * j);
                if (life is not { } phase)
                {
                    continue;
                }
                // Thrown on along the drive gear's turn and out past the small gear's teeth, into the dark.
                var flight = along * (30 + 14 * j) + radial * (10 + 7 * j) + new Vector(0, -4 * j);
                var head = mesh + flight * MotionSpec.EaseOut(phase);
                var tail = mesh + flight * MotionSpec.EaseOut(phase - 0.12);
                dc.PushOpacity(frame.Sparks * hot * (1 - phase));
                foreach (var pen in SparkGlow)
                {
                    dc.DrawLine(pen, tail, head);
                }
                dc.DrawEllipse(SparkTip, null, head, 1.2, 1.2);
                dc.Pop();
            }
        }
    }

    private void DrawHub(DrawingContext dc, BoostDialFrame frame, double heat)
    {
        dc.DrawEllipse(HubShadow, null, new Point(200, 206), 78, 78);
        dc.DrawEllipse(HubFill, HubEdge, Center, 61, 61);
        dc.DrawEllipse(null, HubRimLight, new Point(200, 200.5), 59, 59);

        // The furnace window behind its grille.
        dc.DrawEllipse(FurnaceBack, null, Center, 43, 43);
        if (heat > 0.004)
        {
            dc.PushOpacity(heat);
            dc.DrawEllipse(FurnaceHeat, null, Center, 43, 43);
            dc.Pop();
        }
        if (frame.Flare > 0.004)
        {
            dc.PushOpacity(frame.Flare);
            dc.DrawEllipse(WhiteHot, null, Center, 43, 43);
            dc.Pop();
        }
        dc.DrawGeometry(GrilleFill, null, Grille);
        dc.DrawEllipse(FurnaceDepth, HubEdge, Center, 44, 44);

        for (var i = 0; i < 8; i++)
        {
            var rivet = Polar(Center, 52.5, (i * 45 + 22.5) * Deg);
            dc.DrawEllipse(ShadowCore, null, rivet + new Vector(0, 1), 3.6, 3.6);
            dc.DrawEllipse(RivetFill, null, rivet, 3.5, 3.5);
        }
    }

    /// <summary>The steel nameplate across the furnace, its letters stamped in cold and running molten hot.</summary>
    private void DrawNameplate(DrawingContext dc, BoostDialFrame frame)
    {
        dc.PushTransform(new TranslateTransform(0, 3));
        Halo(dc, NameplateShape, ShadowBlur, ShadowCore, 0.8);
        dc.Pop();
        dc.DrawRoundedRectangle(NameplateFill, RivetEdge, NameplateRect, 3, 3);
        dc.DrawLine(NameplateLight, new Point(142, 185.5), new Point(258, 185.5));
        dc.DrawEllipse(ScrewFill, null, new Point(149, 200), 3, 3);
        dc.DrawEllipse(ScrewFill, null, new Point(251, 200), 3, 3);

        var (title, sub) = Lettering(frame.Label);
        dc.PushOpacity(frame.LabelOpacity);
        if (frame.Lit < 0.996)
        {
            dc.PushOpacity(1 - frame.Lit);
            dc.PushTransform(new TranslateTransform(0, 1));
            dc.DrawGeometry(StampShade, null, title);
            dc.Pop();
            dc.DrawGeometry(StampInk, null, title);
            dc.Pop();
        }
        if (frame.Lit > 0.004)
        {
            dc.PushOpacity(frame.Lit);
            foreach (var pen in MoltenGlow)
            {
                dc.DrawGeometry(null, pen, title);
            }
            dc.PushTransform(new TranslateTransform(0, 1));
            dc.DrawGeometry(MoltenShade, null, title);
            dc.Pop();
            dc.DrawGeometry(Molten, null, title);
            dc.Pop();
        }
        dc.PushTransform(new TranslateTransform(0, 1));
        dc.DrawGeometry(ChalkShade, null, sub);
        dc.Pop();
        dc.DrawGeometry(Chalk, null, sub);
        dc.Pop();
    }

    /// <summary>Embers drifting up from the furnace while the machine runs.</summary>
    private static void DrawEmbers(DrawingContext dc, BoostDialFrame frame)
    {
        if (frame.Embers <= 0.004)
        {
            return;
        }
        for (var i = 0; i < 14; i++)
        {
            if (Phase(frame.ParticleMs - 310 * i, 2200 + i % 4 * 400) is not { } phase)
            {
                continue;
            }
            var rise = MotionSpec.EaseOut(phase);
            var size = i % 3 == 0 ? 4 : 2.5;
            var at = new Point(96 + i * 53 % 210 + size / 2 + (i % 5 - 2) * 9 * rise, 250 + i * 29 % 80 + size / 2 - 110 * rise);
            var glow = phase < 0.15 ? phase / 0.15 : 1 - (phase - 0.15) / 0.85;
            dc.PushOpacity(frame.Embers * glow);
            dc.DrawEllipse(EmberHalo, null, at, size * 1.4, size * 1.4);
            dc.DrawEllipse(EmberCore, null, at, size / 2, size / 2);
            dc.Pop();
        }
    }

    /// <summary>Where a looping particle is in its life (0 to 1), or nothing before its first launch.</summary>
    private static double? Phase(double ms, double period) => ms < 0 ? null : ms % period / period;

    /// <summary>A soft edge round a shape, drawn under it; only the part outside the shape shows.</summary>
    private static void Halo(DrawingContext dc, Geometry shape, Pen[] blur, Brush core, double level)
    {
        if (level <= 0.004)
        {
            return;
        }
        dc.PushOpacity(Math.Min(1, level));
        foreach (var pen in blur)
        {
            dc.DrawGeometry(null, pen, shape);
        }
        dc.DrawGeometry(core, null, shape);
        dc.Pop();
    }

    private static void Disc(DrawingContext dc, Brush fill, Point centre, double radius, double level)
    {
        if (level <= 0.004)
        {
            return;
        }
        dc.PushOpacity(Math.Min(1, level));
        dc.DrawEllipse(fill, null, centre, radius, radius);
        dc.Pop();
    }

    private static MatrixTransform Place(Point centre, double degrees)
    {
        var matrix = Matrix.Identity;
        matrix.Rotate(degrees);
        matrix.Translate(centre.X, centre.Y);
        return new MatrixTransform(matrix);
    }

    private (Geometry Title, Geometry Sub) Lettering(BoostDialLabel label)
    {
        var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        if (dpi != _textDpi)
        {
            _textDpi = dpi;
            Array.Clear(_titles);
            Array.Clear(_subs);
        }
        var i = (int)label;
        if (_titles[i] is { } laidTitle && _subs[i] is { } laidSub)
        {
            return (laidTitle, laidSub);
        }
        var body = TryFindResource("Font.Body") as FontFamily ?? new FontFamily("Segoe UI");
        var mono = TryFindResource("Font.Mono") as FontFamily ?? new FontFamily("Consolas");
        var (word, line) = label switch
        {
            BoostDialLabel.Igniting => ("IGNITING", "spinning up"),
            BoostDialLabel.Engaged => ("ENGAGED", "click to stop"),
            BoostDialLabel.Cooling => ("COOLING", "putting it back"),
            _ => ("BOOST", "click to engage"),
        };
        var title = _titles[i] = Spaced(word, new Typeface(body, FontStyles.Normal, FontWeights.Bold, FontStretches.Normal), 17, 1.7, 200, middle: true, dpi);
        var sub = _subs[i] = Spaced(line, new Typeface(mono, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal), 9.5, 0.38, 226, middle: false, dpi);
        return (title, sub);
    }

    /// <summary>
    /// A line of letters with extra space between them, centred on the dial: its line box centred on
    /// <paramref name="y"/> when <paramref name="middle"/>, otherwise topped at it.
    /// </summary>
    private static Geometry Spaced(string text, Typeface face, double size, double spacing, double y, bool middle, double dpi)
    {
        var letters = text.Select(c => new FormattedText(
            c.ToString(), CultureInfo.InvariantCulture, FlowDirection.LeftToRight, face, size, Brushes.White, dpi)).ToList();
        var width = letters.Sum(l => l.WidthIncludingTrailingWhitespace) + spacing * (letters.Count - 1);
        var top = middle ? y - letters[0].Height / 2 : y;
        var group = new GeometryGroup { FillRule = FillRule.Nonzero };
        var x = Center.X - width / 2;
        foreach (var letter in letters)
        {
            group.Children.Add(letter.BuildGeometry(new Point(x, top)));
            x += letter.WidthIncludingTrailingWhitespace + spacing;
        }
        group.Freeze();
        return group;
    }

    private sealed record Plate(Geometry Shape, Geometry Seam, Geometry Crack, Point RivetA, Point RivetB, Vector Out);

    /// <summary>
    /// Nine jagged plates between radius 164 and about 198, a little apart from each other, with a
    /// smooth inner edge against the rim, two rivets and a crack each.
    /// </summary>
    private static Plate[] BuildArmour()
    {
        double[] spans = [44, 36, 40, 38, 46, 34, 42, 40, 40];
        int[] jag = [0, 7, 2, 10, 4, 8, 1, 6];
        var plates = new Plate[spans.Length];
        var start = -104.0;
        for (var k = 0; k < spans.Length; k++)
        {
            var a0 = (start + 2.2) * Deg;
            var a1 = (start + spans[k] - 2.2) * Deg;
            start += spans[k];
            var inner = Enumerable.Range(0, 7).Select(i => Polar(Center, 164, a0 + (a1 - a0) * i / 6)).ToArray();
            var outer = Enumerable.Range(0, 8).Reverse().Select(i => Polar(Center, 198 - jag[(i + k) % 8], a0 + (a1 - a0) * i / 7));
            var crack = a0 + (a1 - a0) * (k * 3 % 5 + 1) / 7;
            var mid = (a0 + a1) / 2;
            plates[k] = new Plate(
                Lines([.. inner, .. outer], closed: true),
                Lines(inner, closed: false),
                Lines([Polar(Center, 196, crack), Polar(Center, 186, crack + 0.03), Polar(Center, 180, crack - 0.01)], closed: false),
                Polar(Center, 180, a0 + 0.07),
                Polar(Center, 180, a1 - 0.07),
                new Vector(Math.Cos(mid), Math.Sin(mid)));
        }
        return plates;
    }

    /// <summary>A spur gear round the origin with a hub hole and lightening holes, filled even-odd so the holes are holes.</summary>
    private static Geometry Gear(int teeth, double root, double tip, double hub, int holes, double holeRadius, double holesAt)
    {
        var step = 2 * Math.PI / teeth;
        var outline = new List<Point>(teeth * 5);
        for (var i = 0; i < teeth; i++)
        {
            var a = i * step;
            outline.Add(Polar(default, root, a - step * 0.5));
            outline.Add(Polar(default, root, a - step * 0.27));
            outline.Add(Polar(default, tip, a - step * 0.13));
            outline.Add(Polar(default, tip, a + step * 0.13));
            outline.Add(Polar(default, root, a + step * 0.27));
        }
        var geometry = new StreamGeometry { FillRule = FillRule.EvenOdd };
        using (var context = geometry.Open())
        {
            context.BeginFigure(outline[0], isFilled: true, isClosed: true);
            context.PolyLineTo(outline.Skip(1).ToList(), isStroked: true, isSmoothJoin: false);
            Circle(context, default, hub);
            for (var i = 0; i < holes; i++)
            {
                Circle(context, Polar(default, holesAt, (i + 0.5) * 2 * Math.PI / holes), holeRadius);
            }
        }
        geometry.Freeze();
        return geometry;
    }

    /// <summary>Sixty fine ticks round the backplate's edge, one every six degrees.</summary>
    private static Geometry BuildTicks()
    {
        var geometry = new StreamGeometry();
        using (var context = geometry.Open())
        {
            for (var i = 0; i < 60; i++)
            {
                var a = (i * 6 + 0.3) * Deg;
                context.BeginFigure(Polar(Center, 150, a), isFilled: false, isClosed: false);
                context.LineTo(Polar(Center, 156, a), isStroked: true, isSmoothJoin: false);
            }
        }
        geometry.Freeze();
        return geometry;
    }

    /// <summary>Bars three units thick every ten, counted up from the bottom of the furnace window and cut to its circle.</summary>
    private static Geometry BuildGrille()
    {
        var bars = new GeometryGroup();
        for (var y = 243.0; y > 157; y -= 10)
        {
            bars.Children.Add(new RectangleGeometry(new Rect(157, y - 3, 86, 3)));
        }
        var grille = Geometry.Combine(bars, new EllipseGeometry(Center, 43, 43), GeometryCombineMode.Intersect, null);
        grille.Freeze();
        return grille;
    }

    private static Geometry Lines(IReadOnlyList<Point> points, bool closed)
    {
        var geometry = new StreamGeometry();
        using (var context = geometry.Open())
        {
            context.BeginFigure(points[0], isFilled: closed, isClosed: closed);
            context.PolyLineTo(points.Skip(1).ToList(), isStroked: true, isSmoothJoin: false);
        }
        geometry.Freeze();
        return geometry;
    }

    private static void Circle(StreamGeometryContext context, Point centre, double radius)
    {
        var size = new Size(radius, radius);
        context.BeginFigure(new Point(centre.X + radius, centre.Y), isFilled: true, isClosed: true);
        context.ArcTo(new Point(centre.X - radius, centre.Y), size, 0, false, SweepDirection.Clockwise, isStroked: true, isSmoothJoin: false);
        context.ArcTo(new Point(centre.X + radius, centre.Y), size, 0, false, SweepDirection.Clockwise, isStroked: true, isSmoothJoin: false);
    }

    private static Point Polar(Point centre, double radius, double radians)
        => new(centre.X + radius * Math.Cos(radians), centre.Y + radius * Math.Sin(radians));

    private static Color Rgb(int rgb) => Color.FromRgb((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb);

    /// <summary>A linear gradient across a part's bounds, from its top left corner to <paramref name="end"/>.</summary>
    private static Brush Metal(Point end, params (int Rgb, double Offset)[] stops)
    {
        var brush = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = end };
        foreach (var (rgb, offset) in stops)
        {
            brush.GradientStops.Add(new GradientStop(Rgb(rgb), offset));
        }
        brush.Freeze();
        return brush;
    }

    /// <summary>A radial gradient across a part's bounds, centred at (x, y) and reaching <paramref name="radius"/> of them.</summary>
    private static Brush Round(double x, double y, double radius, params (int Rgb, double Alpha, double Offset)[] stops)
    {
        var brush = new RadialGradientBrush
        {
            Center = new Point(x, y),
            GradientOrigin = new Point(x, y),
            RadiusX = radius,
            RadiusY = radius,
        };
        foreach (var (rgb, alpha, offset) in stops)
        {
            var color = Rgb(rgb);
            color.A = (byte)Math.Round(alpha * 255);
            brush.GradientStops.Add(new GradientStop(color, offset));
        }
        brush.Freeze();
        return brush;
    }

    /// <summary>Top to bottom from <paramref name="color"/> to nothing, over the top quarter: light caught on an upper edge.</summary>
    private static Brush Fade(Color color, double opacity)
    {
        var brush = new LinearGradientBrush(color, Color.FromArgb(0, color.R, color.G, color.B), new Point(0, 0), new Point(0, 0.3))
        {
            Opacity = opacity,
        };
        brush.Freeze();
        return brush;
    }

    /// <summary>Outlines from wide to narrow, each faint: stacked, they fade out from the edge like a blur.</summary>
    private static Pen[] Blur(Color color, double reach, double alpha, int steps)
        => [.. Enumerable.Range(0, steps).Select(i => Frozen(new Pen(Solid(color, alpha), 2 * reach * (steps - i) / steps) { LineJoin = PenLineJoin.Round }))];

    /// <summary>A disc of <paramref name="color"/>, solid out to <paramref name="inner"/> of its radius and fading to nothing at the rim.</summary>
    private static Brush Falloff(Color color, double alpha, double inner)
    {
        var span = 1 - inner;
        var brush = new RadialGradientBrush();
        foreach (var (share, offset) in new[] { (1.0, 0.0), (1.0, inner), (0.45, inner + span * 0.35), (0.12, inner + span * 0.7), (0.0, 1.0) })
        {
            brush.GradientStops.Add(new GradientStop(Color.FromArgb((byte)Math.Round(alpha * share * 255), color.R, color.G, color.B), offset));
        }
        brush.Freeze();
        return brush;
    }

    private static Pen Streak(Color color, double opacity, double width)
        => Frozen(new Pen(Solid(color, opacity), width) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round });

    private static Pen Outline(double width, bool round = false, Color? color = null)
    {
        var cap = round ? PenLineCap.Round : PenLineCap.Flat;
        return Frozen(new Pen(Solid(color ?? Ink, 1), width) { LineJoin = PenLineJoin.Round, StartLineCap = cap, EndLineCap = cap });
    }

    private static T Frozen<T>(T freezable) where T : Freezable
    {
        freezable.Freeze();
        return freezable;
    }

    private static SolidColorBrush Solid(Color color, double opacity)
    {
        var brush = new SolidColorBrush(color) { Opacity = Math.Clamp(opacity, 0, 1) };
        brush.Freeze();
        return brush;
    }
}
