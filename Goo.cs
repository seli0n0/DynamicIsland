using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace DynamicIsland;

public sealed class Goo : FrameworkElement
{
    const double RimWidth = 1;
    const double TearGap = 7.5;
    const double NeckGrip = 0.5;
    const double NeckHandle = 2.4;
    const double Tolerance = 0.02;
    const byte TintedAlpha = 0x8C;
    const double HazeOpacity = 0.14;
    const byte FrostSolid = byte.MaxValue, FrostThin = 0x30, FrostMilk = 0x22;
    const double SweatBody = 0.25, SweatMilk = 0.9;

    static readonly Color PlainRim = Color.FromArgb(0x20, 0xFF, 0xFF, 0xFF);
    static readonly Color PlainMilk = Color.FromArgb(0, 0xFF, 0xFF, 0xFF);
    static readonly double[] HazeReach = [2, 3, 4];
    static readonly (double At, double Level)[] FlashFrames = [(0, 0), (0.04, 1), (0.13, 0.3), (0.19, 0.9), (0.5, 0), (1, 0)];

    readonly SolidColorBrush _rim = new(PlainRim), _fill = new(Colors.Black), _milk = new(PlainMilk);
    readonly Light _beat = new(), _flash = new();
    readonly Pen _edge;
    readonly FrameLoop _frostLoop;

    Rect _pill = Rect.Empty, _bubble = Rect.Empty;
    double _radius;
    int _strength;
    double _spend, _body = byte.MaxValue, _haze, _sweat;
    Action? _settled;

    public Goo()
    {
        _edge = new Pen(_rim, 2 * RimWidth) { LineJoin = PenLineJoin.Round };
        _frostLoop = new FrameLoop(AdvanceFrost, 1.0 / 120);
    }

    public void Tint(Color? color, Duration time)
    {
        Color to = color is { } c ? Color.FromArgb(TintedAlpha, c.R, c.G, c.B) : PlainRim;
        _rim.BeginAnimation(SolidColorBrush.ColorProperty, new ColorAnimation(to, time));
        var lit = new ColorAnimation(color ?? Colors.White, time);
        _beat.Edge.BeginAnimation(SolidColorBrush.ColorProperty, lit);
        _beat.Mist.BeginAnimation(SolidColorBrush.ColorProperty, lit);
    }

    public void Beat(double level)
    {
        level = Math.Clamp(level, 0, 1);
        _beat.Edge.Opacity = level;
        _beat.Mist.Opacity = level * HazeOpacity;
        if (level == _sweat) return;
        _sweat = level;
        _frostLoop.Start();
    }

    public (Rect Box, double Radius) PillInside => Inside(_pill, _radius);

    public (Rect Box, double Radius) BubbleInside => Inside(_bubble, _bubble.Height / 2);

    /// <summary>
    /// Wear the body to a given frost strength over a given while. The ground and its haze chase the strength frame by
    /// frame rather than by animation, because the bass and the pointer wipe the same two away as they go.
    /// </summary>
    public void Thin(int strength, Duration time, Action thinned)
    {
        _strength = strength;
        _spend = time.HasTimeSpan && time.TimeSpan > TimeSpan.Zero ? byte.MaxValue / time.TimeSpan.TotalSeconds : 0;
        _settled = thinned;
        if (_spend > 0) _frostLoop.Start();
        else
        {
            _body = FrostAlpha(strength);
            _haze = MilkAlpha(strength);
            Settle();
        }
    }

    bool AdvanceFrost(double dt)
    {
        bool moving = Walk(ref _body, FrostAlpha(_strength), _spend, dt) | Walk(ref _haze, MilkAlpha(_strength), _spend, dt);
        Paint(_sweat);
        if (moving) return true;
        Settle();
        return false;
    }

    void Settle()
    {
        Paint(_sweat);
        Action? done = _settled;
        _settled = null;
        done?.Invoke();
    }

    void Paint(double sweat)
    {
        double share = Math.Clamp(sweat, 0, 1);
        _fill.Color = Color.FromArgb((byte)Math.Round(_body * (1 - share * SweatBody)), 0, 0, 0);
        _milk.Color = Color.FromArgb((byte)Math.Round(_haze * (1 - share * SweatMilk)), 0xFF, 0xFF, 0xFF);
    }

    static bool Walk(ref double value, double to, double rate, double dt)
    {
        if (value == to) return false;
        double step = rate * dt;
        value = Math.Abs(to - value) <= step ? to : value + Math.Sign(to - value) * step;
        return true;
    }

    /// <summary>
    /// How much haze the frost leaves in the ground it thins: a pane that lets the blurred world through also whitens a
    /// little where it meets the light, and that whiteness is most of what reads as frost rather than as a window.
    /// </summary>
    public static byte MilkAlpha(int strength) =>
        (byte)Math.Round(FrostMilk * Math.Clamp(strength, 0, 100) / 100.0);

    /// <summary>
    /// How solid the body's ground is at a given frost strength: the slider's ends are the island standing nearly
    /// opaque over what lies behind it and the same ground worn so thin that the frosted blur of that shows through
    /// it whole. A strength of none is the switch off — a body no thinner than the island wears without glass.
    /// </summary>
    public static byte FrostAlpha(int strength) =>
        (byte)Math.Round(FrostSolid - (FrostSolid - FrostThin) * Math.Clamp(strength, 0, 100) / 100.0);

    public void StartFlashing(Color color, TimeSpan round)
    {
        _flash.Edge.Color = _flash.Mist.Color = color;
        _flash.Edge.BeginAnimation(Brush.OpacityProperty, Flashes(1));
        _flash.Mist.BeginAnimation(Brush.OpacityProperty, Flashes(HazeOpacity));

        DoubleAnimationUsingKeyFrames Flashes(double share)
        {
            var flashes = new DoubleAnimationUsingKeyFrames { Duration = round, RepeatBehavior = RepeatBehavior.Forever, FillBehavior = FillBehavior.Stop };
            foreach ((double at, double level) in FlashFrames)
                flashes.KeyFrames.Add(new LinearDoubleKeyFrame(level * share, KeyTime.FromPercent(at)));
            return flashes;
        }
    }

    public void StopFlashing()
    {
        _flash.Edge.BeginAnimation(Brush.OpacityProperty, null);
        _flash.Mist.BeginAnimation(Brush.OpacityProperty, null);
    }

    public void SetShape(Rect pill, double radius, Rect bubble)
    {
        if (pill == _pill && radius == _radius && bubble == _bubble) return;
        _pill = pill;
        _radius = radius;
        _bubble = bubble;
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext dc)
    {
        if (_pill.IsEmpty) return;
        Geometry pill = Outline(_pill, _radius);
        if (_bubble.IsEmpty)
        {
            DrawBody(dc, pill);
            return;
        }

        Geometry bubble = Outline(_bubble, _bubble.Height / 2);
        Geometry? neck = CreateNeck();
        if (neck == null && !_pill.IntersectsWith(_bubble))
        {
            DrawBody(dc, pill);
            DrawBody(dc, bubble);
            return;
        }

        Geometry body = Union(pill, bubble);
        DrawBody(dc, neck != null ? Union(body, neck) : body);
    }

    public static Geometry Union(Geometry a, Geometry b) =>
        Geometry.Combine(a, b, GeometryCombineMode.Union, null, Tolerance, ToleranceType.Absolute);

    void DrawBody(DrawingContext dc, Geometry body)
    {
        Rect around = body.Bounds;
        around.Inflate(2 * RimWidth, 2 * RimWidth);
        var outside = new GeometryGroup { FillRule = FillRule.EvenOdd };
        outside.Children.Add(new RectangleGeometry(around));
        outside.Children.Add(body);

        dc.PushClip(outside);
        dc.DrawGeometry(null, _edge, body);
        dc.DrawGeometry(null, _beat.Line, body);
        dc.DrawGeometry(null, _flash.Line, body);
        dc.Pop();

        dc.DrawGeometry(_fill, null, body);
        dc.DrawGeometry(_milk, null, body);
        foreach (Pen mist in _beat.Mists) dc.DrawGeometry(null, mist, body);
        foreach (Pen mist in _flash.Mists) dc.DrawGeometry(null, mist, body);
    }

    sealed class Light
    {
        public readonly SolidColorBrush Edge = new(Colors.White) { Opacity = 0 }, Mist = new(Colors.White) { Opacity = 0 };
        public readonly Pen Line;
        public readonly Pen[] Mists;

        public Light()
        {
            Line = new Pen(Edge, 2 * RimWidth) { LineJoin = PenLineJoin.Round };
            Mists = HazeReach.Select(reach => new Pen(Mist, 2 * reach) { LineJoin = PenLineJoin.Round }).ToArray();
        }
    }

    static Geometry Outline(Rect rect, double radius)
    {
        (Rect box, double r) = Inside(rect, radius);
        return Squircle.Of(box, r);
    }

    static (Rect Box, double Radius) Inside(Rect rect, double radius)
    {
        if (rect.IsEmpty) return (rect, 0);
        rect.Inflate(-Math.Min(RimWidth, rect.Width / 2), -Math.Min(RimWidth, rect.Height / 2));
        return (rect, Math.Max(radius - RimWidth, 0));
    }

    Geometry? CreateNeck()
    {
        var c1 = new Point(_pill.Right - _radius, _pill.Top + _radius);
        var c2 = new Point(_bubble.Left + _bubble.Height / 2, _bubble.Top + _bubble.Height / 2);
        return c2.X > c1.X ? Neck(c1, _radius - RimWidth, c2, _bubble.Height / 2 - RimWidth, TearGap) : null;
    }

    public static Geometry? Neck(Point c1, double r1, Point c2, double r2, double tearGap)
    {
        Vector between = c2 - c1;
        double d = between.Length, gap = d - r1 - r2;
        if (r1 <= 0 || r2 <= 0 || d <= Math.Abs(r1 - r2) || gap >= tearGap) return null;

        double u1 = 0, u2 = 0;
        if (gap < 0)
        {
            u1 = Math.Acos(Math.Clamp((r1 * r1 + d * d - r2 * r2) / (2 * r1 * d), -1, 1));
            u2 = Math.Acos(Math.Clamp((r2 * r2 + d * d - r1 * r1) / (2 * r2 * d), -1, 1));
        }

        double grip = NeckGrip * (1 - Math.Clamp(gap / tearGap, 0, 1));
        double axis = Math.Atan2(between.Y, between.X), wide = Math.Acos((r1 - r2) / d);
        double a1 = axis + u1 + (wide - u1) * grip, a2 = axis - u1 - (wide - u1) * grip;
        double a3 = axis + Math.PI - u2 - (Math.PI - u2 - wide) * grip, a4 = axis - Math.PI + u2 + (Math.PI - u2 - wide) * grip;
        Point p1 = PointOnCircle(c1, a1, r1), p2 = PointOnCircle(c1, a2, r1), p3 = PointOnCircle(c2, a3, r2), p4 = PointOnCircle(c2, a4, r2);

        double reach = Math.Min(grip * NeckHandle, (p1 - p3).Length / (r1 + r2)) * Math.Min(1, 2 * d / (r1 + r2));
        const double Quarter = Math.PI / 2;
        var neck = new StreamGeometry();
        using (StreamGeometryContext g = neck.Open())
        {
            g.BeginFigure(p1, true, true);
            g.BezierTo(PointOnCircle(p1, a1 - Quarter, r1 * reach), PointOnCircle(p3, a3 + Quarter, r2 * reach), p3, true, true);
            g.LineTo(p4, true, true);
            g.BezierTo(PointOnCircle(p4, a4 - Quarter, r2 * reach), PointOnCircle(p2, a2 + Quarter, r1 * reach), p2, true, true);
        }
        return neck;
    }

    static Point PointOnCircle(Point center, double angle, double radius) =>
        new(center.X + radius * Math.Cos(angle), center.Y + radius * Math.Sin(angle));
}
