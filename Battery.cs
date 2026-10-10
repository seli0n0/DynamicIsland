using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using static DynamicIsland.Motion;

namespace DynamicIsland;

/// <summary>
/// A phone's charge drawn as a cell: a shell with as much of it filled as the phone has said, in the colour that level
/// calls for, and a bolt cut out of the fill while the phone is on charge. Both the fill and the bolt glide to what
/// they are told rather than jumping — a charge arrives as a run of small steps, and a bar that jumps on each of them
/// reads as a flicker. A phone that has said nothing leaves the shell empty, which is not the same as a phone at zero.
/// </summary>
public sealed class Battery : FrameworkElement
{
    // The cell is drawn at two sizes — a small one in the menu's row and a larger one on the phone's page — so
    // everything about its body is a share of its height rather than a figure of its own: a shell of one thickness
    // reads as a shell at twelve pixels and as a plug at ten.
    const double ContactShare = 0.22; // the nub on the right, standing outside the shell
    const double ShellShare = 0.11, ShellLeast = 0.8;   // how thick the shell's own line is
    const double InsetShare = 0.1, InsetLeast = 0.7;    // the gap left between the shell and the fill
    const double BoltTall = 0.86; // how much of the cell's inside the bolt stands in
    const double BoltFat = 0.5;   // how far the bolt is widened, so the cut reads at this size
    const double BoltGrid = 18;   // the bolt's own height in the 24-unit grid it was drawn on
    const int LowPercent = 20, WaningPercent = 45;

    static readonly Color Full = Colors.White;
    static readonly Color OnCharge = Color.FromRgb(0x30, 0xD1, 0x58);
    static readonly Color Waning = Color.FromRgb(0xFF, 0x9F, 0x0A);
    static readonly Color Critical = Color.FromRgb(0xFF, 0x45, 0x3A);
    static readonly Color ShellColor = Color.FromArgb(0x77, 0xFF, 0xFF, 0xFF);
    static readonly Geometry BoltShape = Geometry.Parse("M13.6,3 L6.2,13.3 H11.3 L10.4,21 L17.8,10.7 H12.7 Z");

    readonly Spring _fill = new(0, 190, 26);
    readonly Spring _bolt = new(0, 420, 26);
    readonly FrameLoop _loop;
    readonly SolidColorBrush _colour = new(Full);
    readonly Brush _shellLine;

    int _level = -1;
    bool _charging;
    Color _tint = Full;

    public Battery()
    {
        _loop = new FrameLoop(Advance);
        var line = new SolidColorBrush(ShellColor);
        line.Freeze();
        _shellLine = line;
        BoltShape.Freeze();
    }

    /// <summary>What the phone last said: a hundredth of a charge, or a level below zero for a phone that has not
    /// spoken. Told whether to glide there, since a page being drawn for the first time must not be seen travelling.</summary>
    public void Set(int level, bool charging, bool animate)
    {
        _level = level < 0 ? -1 : Math.Min(level, 100);
        _charging = charging && _level >= 0;
        double share = _level < 0 ? 0 : _level / 100.0;

        if (animate)
        {
            _fill.Target = share;
            _bolt.Target = _charging ? 1 : 0;
            _loop.Start();
        }
        else
        {
            _fill.Snap(share);
            _bolt.Snap(_charging ? 1 : 0);
        }

        // the colour is only travelled to when the level has crossed into another meaning of its own: a phone being
        // asked again what it holds, and answering the same, is not a phone whose cell has changed
        Color to = Tint();
        if (to != _tint)
        {
            _tint = to;
            if (animate) _colour.BeginAnimation(SolidColorBrush.ColorProperty, new ColorAnimation(to, Ms(260)));
            else
            {
                _colour.BeginAnimation(SolidColorBrush.ColorProperty, null);
                _colour.Color = to;
            }
        }
        InvalidateVisual();
    }

    Color Tint() => _charging ? OnCharge : _level < 0 ? ShellColor
        : _level <= LowPercent ? Critical : _level <= WaningPercent ? Waning : Full;

    bool Advance(double dt)
    {
        bool moving = _fill.Advance(dt);
        moving |= _bolt.Advance(dt);
        InvalidateVisual();
        return moving;
    }

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        if (w < 8 || h < 5) return;

        double contact = h * ContactShare;
        double shell = Math.Max(ShellLeast, h * ShellShare), inset = Math.Max(InsetLeast, h * InsetShare);
        double body = Math.Max(1, w - contact), round = h * 0.34;
        var pen = new Pen(_shellLine, shell) { LineJoin = PenLineJoin.Round };
        dc.DrawRoundedRectangle(null, pen, new Rect(shell / 2, shell / 2, body - shell, h - shell), round, round);
        dc.DrawRoundedRectangle(_shellLine, null,
            new Rect(body + contact * 0.18, h / 2 - contact * 0.44, contact * 0.82, contact * 0.88), contact * 0.34, contact * 0.34);

        double pad = shell + inset, inside = body - pad * 2, tall = h - pad * 2;
        double share = Math.Clamp(_fill.Value, 0, 1);
        if (inside <= 0 || tall <= 0 || share <= 0) return;

        // a charge too thin for its own corners is drawn as a bead rather than as nothing at all, but the bead is only
        // half the cell's height: a floor set at the whole height leaves a phone at a tenth and one at a half alike
        Geometry fill = new RectangleGeometry(new Rect(pad, pad, Math.Max(tall * 0.5, inside * share), tall), tall / 2, tall / 2);
        double bolt = Math.Clamp(_bolt.Value, 0, 1);
        if (bolt > 0.01) fill = Cut(fill, Bolt(body / 2, h / 2, tall * BoltTall, bolt));
        dc.DrawGeometry(_colour, null, fill);
    }

    /// <summary>The bolt, grown from its own grid to the size asked for and stood in the middle of the cell.</summary>
    static Geometry Bolt(double cx, double cy, double tall, double share)
    {
        double k = tall * share / BoltGrid;
        var placed = new TransformGroup();
        placed.Children.Add(new ScaleTransform(k, k));
        placed.Children.Add(new TranslateTransform(cx - 12 * k, cy - 12 * k));

        Geometry bolt = BoltShape.Clone();
        bolt.Transform = placed;
        return Fatten(bolt, BoltFat * share);
    }

    static Geometry Fatten(Geometry shape, double by) => by <= 0 ? shape : Geometry.Combine(shape,
        shape.GetWidenedPathGeometry(new Pen(Brushes.Black, by) { LineJoin = PenLineJoin.Round }, 0.01, ToleranceType.Absolute),
        GeometryCombineMode.Union, null, 0.01, ToleranceType.Absolute);

    static Geometry Cut(Geometry from, Geometry away) =>
        Geometry.Combine(from, away, GeometryCombineMode.Exclude, null, 0.01, ToleranceType.Absolute);
}
