using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using static DynamicIsland.Motion;

namespace DynamicIsland;

/// <summary>
/// A page of rows that carries more of them than the pill may show. Its caption stays pinned while the rows stand in a
/// canvas that a border clips, and the wheel travels them under the caption; the fade at each end is the one the shelf
/// wears, since a row cut off by the pill's edge reads as a row that is not there.
///
/// Some of the rows are sections that open under their own line. That is how the look page keeps a choice out of sight
/// until the owner asks for it — and how the smartphone page keeps its code, and everything that goes outward, off the
/// page until it is wanted.
/// </summary>
sealed class PinnedPage(FrameworkElement head, Panel body, TranslateTransform move, GradientStop edgeTop,
    GradientStop edgeBottom, FrameworkElement host, double most, PinnedSection[] sections, Action wake)
{
    /// How far a row has to be out of sight before its end is fully faded, in the shelf's own measure.
    const double EdgeFade = 16;

    /// A turn of the wheel travels this much of a page, which is about the height of one of its rows.
    const double ScrollStep = 72;

    /// A page's rows end where the pill does, so the last of them needs the same room a caption does.
    public const double Pad = 6;

    /// How far the rows have been travelled. One of the island's springs, so a turn of the wheel is eased and a page
    /// flicked to its end settles rather than stops.
    public Spring Travel { get; } = new(0, 260, 30);

    /// What the pinned caption takes, measured rather than written down: the rows are fitted against what is left of
    /// the page under it.
    public double Head { get; private set; }

    /// How tall the rows are in all, the pinned caption included, before <paramref name="most"/> cuts them off.
    public double Rows { get; private set; }

    /// The section standing open under its row, if one does.
    public Border? Open { get; private set; }

    /// Whether a section — the rows that open under their line — belongs to this page. A section measures itself as it
    /// opens, and the page has to be refitted every step of the way, so the island asks the pages which owns the one
    /// that just changed rather than keeping a second list of them.
    public bool Holds(Border tiles) => Array.Exists(sections, section => section.Tiles == tiles);

    /// How much of the page the pill shows: the rows, unless they ask for more than a page of this kind may take.
    double Shown => Math.Min(Rows, most);

    /// How much of the rows hangs past the pill's edge and has to be scrolled to.
    public double Overflow => Math.Max(0, Rows - Shown);

    /// The page's own rows, so the island can tell which of its pages a section belongs to.
    public FrameworkElement Host => host;

    /// A page is entered from its beginning, wherever the rows were left last time.
    public void Enter()
    {
        Travel.Snap(0);
        Apply();
    }

    /// Fit the page to the rows it holds: the height the pill takes, and a scroll that must not be left over the end.
    public void Fit()
    {
        body.Measure(new Size(host.Width, double.PositiveInfinity));
        head.Measure(new Size(host.Width, double.PositiveInfinity));
        Head = head.DesiredSize.Height + head.Margin.Top + head.Margin.Bottom;
        Rows = Math.Ceiling(body.DesiredSize.Height) + body.Margin.Top + Pad + Head;
        host.Height = Shown;
        Clamp();
    }

    /// Rows that are no longer there to be scrolled to must not be scrolled to: a section closing takes the page back
    /// under the cut, and a scroll left over from when it was taller would show nothing but its own end.
    public void Clamp()
    {
        double over = Overflow;
        Travel.Target = Math.Clamp(Travel.Target, 0, over);
        if (Travel.Value <= over) return;
        Travel.Value = over;
        Apply();
    }

    public void Scroll(int rows)
    {
        Travel.Target = Math.Clamp(Travel.Target + rows * ScrollStep, 0, Overflow);
        wake();
    }

    public void Apply()
    {
        double scrolled = Travel.Value;
        move.Y = -scrolled;
        edgeTop.Color = Fade(scrolled);
        edgeBottom.Color = Fade(Overflow - scrolled);
    }

    static Color Fade(double past) =>
        Color.FromArgb((byte)Math.Round(255 * (1 - Math.Clamp(past / EdgeFade, 0, 1))), 0, 0, 0);

    /// Open a section under its row, closing the one that stood open before it.
    public void Toggle(Border tiles)
    {
        Open = tiles == Open ? null : tiles;
        foreach (PinnedSection section in sections) Slide(section, section.Tiles == Open);
        if (Open is { } opened) Reveal(opened);
    }

    void Slide(PinnedSection section, bool open)
    {
        Border tiles = section.Tiles;
        if (!open && tiles.Visibility != Visibility.Visible) return;

        tiles.Visibility = Visibility.Visible;
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        var slide = new DoubleAnimation(open ? RoomFor(tiles) : 0, Ms(open ? 320 : 240)) { EasingFunction = ease };
        slide.Completed += (_, _) =>
        {
            if (tiles != Open) tiles.Visibility = Visibility.Collapsed;
        };
        tiles.BeginAnimation(Border.HeightProperty, slide);
        tiles.Child!.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(open ? 1 : 0, Ms(open ? 260 : 160)));
        section.Chevron.RenderTransform!.BeginAnimation(RotateTransform.AngleProperty,
            new DoubleAnimation(open ? 90 : 0, Ms(240)) { EasingFunction = ease });
    }

    /// How much room a section takes once it is open: what its own contents ask for, their margins included. Measured
    /// rather than written down, since a section holds rows whose words arrive later than the page was first fitted,
    /// and a height that was guessed would cut the last of them off.
    public double RoomFor(Border tiles)
    {
        if (tiles.Child is not FrameworkElement child) return 0;
        child.Measure(new Size(Math.Max(0, host.Width - body.Margin.Left - body.Margin.Right), double.PositiveInfinity));
        return Math.Ceiling(child.DesiredSize.Height) + child.Margin.Top + child.Margin.Bottom;
    }

    double HeightOf(FrameworkElement child, Border opened) =>
        child == opened ? RoomFor(opened)
            : child is Border other && Array.Exists(sections, section => section.Tiles == other) ? 0
            : child.DesiredSize.Height;

    /// Where a section stands among the page's rows and how tall those rows are in all with that one section open,
    /// which is the two numbers a scroll to bring it into sight is made of.
    (double Top, double Rows) SpanOf(Border opened)
    {
        double top = 0, at = 0;
        foreach (UIElement entry in body.Children)
        {
            if (entry is not FrameworkElement child) continue;
            if (child == opened) top = at + child.Margin.Top;
            at += HeightOf(child, opened) + child.Margin.Top + child.Margin.Bottom;
        }
        return (top, at);
    }

    /// A section that opens below the cut the page stops at would open out of sight, under the pill's own edge, so the
    /// rows are travelled just far enough for it to be read — and no further, since a page that jumps further than it
    /// must loses the row that was just pressed.
    void Reveal(Border tiles)
    {
        (double top, double rows) = SpanOf(tiles);
        double want = Head + rows + Pad, shown = Math.Min(want, most);
        Travel.Target = Math.Clamp(top + RoomFor(tiles) + Pad - (shown - Head), 0, Math.Max(0, want - shown));
        wake();
    }
}

/// A section of a pinned page: the rows that open under their line, and the sign on that line which turns to say so.
sealed record PinnedSection(Border Tiles, Icon Chevron);
