using System.Windows;
using System.Windows.Media;

namespace DynamicIsland;

static class Squircle
{
    const double Smoothing = 0.6;

    public static Geometry Of(Rect rect, double radius) => Of(rect, radius, 0);

    /// <summary>
    /// The outline, with the two sides free to be rounded differently. The pair of corners on the edge the island
    /// stands against keeps <paramref name="radius"/>; the pair on the side it hangs from opens to that much again
    /// plus <paramref name="bulge"/> of it, so the pill reads as a drop — tight where it touches the screen, round
    /// where the glass falls away. A short pill has no room left over for a wider curve, so it stays the capsule it
    /// always was; the rounding shows itself on the tall pages, where it is also the one thing that gives the bottom
    /// a weight the top, held by the edge, does not need. <paramref name="edgeAtBottom"/> moves the tight pair down,
    /// which is how an island hung under the bottom edge of the screen is drawn.
    /// </summary>
    public static Geometry Of(Rect rect, double radius, double bulge, bool edgeAtBottom = false)
    {
        double room = Math.Min(rect.Width, rect.Height) / 2;
        double tight = Math.Min(radius, room);
        if (tight <= 0.01) return new RectangleGeometry(rect);
        double loose = Loose(rect, tight, bulge);
        double top = edgeAtBottom ? loose : tight, bottom = edgeAtBottom ? tight : loose;

        var start = new Point(rect.Left + rect.Width / 2, rect.Top);
        var outline = new StreamGeometry();
        using (StreamGeometryContext g = outline.Open())
        {
            g.BeginFigure(start, true, true);
            Corner(new Point(rect.Right, rect.Top), new Vector(1, 0), new Vector(0, 1), top);
            Corner(new Point(rect.Right, rect.Bottom), new Vector(0, 1), new Vector(-1, 0), bottom);
            Corner(new Point(rect.Left, rect.Bottom), new Vector(-1, 0), new Vector(0, -1), bottom);
            Corner(new Point(rect.Left, rect.Top), new Vector(0, -1), new Vector(1, 0), top);
            g.LineTo(start, true, false);

            void Corner(Point corner, Vector along, Vector turn, double r)
            {
                Point At(Point p, double x, double y) => p + along * x + turn * y;

                double smooth = Math.Clamp(room / r - 1, 0, Smoothing);
                double reach = Math.Min((1 + smooth) * r, room);
                double sweep = Math.PI / 2 * (1 - smooth);
                double arc = Math.Sin(sweep / 2) * r * Math.Sqrt(2);
                double lean = Math.PI / 4 * smooth;
                double c = r * Math.Tan(lean / 2) * Math.Cos(lean), d = c * Math.Tan(lean);
                double b = (reach - arc - c - d) / 3, a = 2 * b;

                Point from = corner - along * reach;
                g.LineTo(from, true, false);
                Point p1 = At(from, a + b + c, d), p2 = At(p1, arc, arc);
                if (smooth > 0.001) g.BezierTo(At(from, a, 0), At(from, a + b, 0), p1, true, true);
                g.ArcTo(p2, new Size(r, r), 0, false, SweepDirection.Clockwise, true, true);
                if (smooth > 0.001) g.BezierTo(At(p2, d, c), At(p2, d, b + c), At(p2, d, a + b + c), true, true);
            }
        }
        outline.Freeze();
        return outline;
    }

    /// <summary>How wide the far corners of <see cref="Of(Rect,double,double,bool)"/> come to. A plain rounded
    /// rectangle standing in for the outline — the frost pane, which knows one radius — has to reach this far to
    /// stay inside the body rather than spill past its roundest corners.</summary>
    public static double Loose(Rect rect, double radius, double bulge)
    {
        double room = Math.Min(rect.Width, rect.Height) / 2, tight = Math.Min(radius, room);
        return Math.Min(room, tight * (1 + Math.Max(bulge, 0)));
    }
}
