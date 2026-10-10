using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;

namespace DynamicIsland;

public sealed class RowList : StackPanel
{
    const double Radius = 12;
    const double HoverOpacity = 0.12, PressOpacity = 0.21;
    const double PressInsetX = 5, PressInsetY = 2;
    const double AppearSquish = 0.6;
    const double HiddenBelow = 0.05;

    readonly RectangleGeometry _shape = new() { RadiusX = Radius, RadiusY = Radius };
    readonly SolidColorBrush _fill = new(Colors.White) { Opacity = 0 };

    readonly Spring _top = new(0), _bottom = new(0);
    readonly Spring _shown = new(0), _squish = new(0);
    readonly FrameLoop _loop;
    bool _lit, _pressed;

    public RowList()
    {
        _loop = new FrameLoop(Advance);
        AddHandler(Mouse.MouseMoveEvent, new MouseEventHandler((_, _) => UpdateHighlight()), true);
        AddHandler(Mouse.MouseDownEvent, new MouseButtonEventHandler((_, _) => UpdateHighlight()), true);
        AddHandler(Mouse.MouseUpEvent, new MouseButtonEventHandler((_, _) => UpdateHighlight()), true);
        AddHandler(Mouse.LostMouseCaptureEvent, new MouseEventHandler((_, _) => UpdateHighlight()), true);
        MouseEnter += (_, _) => UpdateHighlight();
        MouseLeave += (_, _) => UpdateHighlight();
        IsVisibleChanged += (_, _) =>
        {
            if (!IsVisible) Reset();
        };
    }

    /// <summary>
    /// A list is measured and arranged against the height the page has left for it, and the look page cuts its list
    /// short to scroll it. Against that height a stack would measure the rows past the cut with no room and stop
    /// arranging them at all, so they would fold away instead of waiting below to be scrolled up into sight. The list
    /// therefore lays its rows out from what each of them wants, one under the other, whatever height it was itself
    /// given, and hands back that given height: the rows are moved by the scroll and never squeezed by it, and a row
    /// stretched into the room under the cut cannot swallow the page the way an unlimited height would.
    /// </summary>
    protected override Size MeasureOverride(Size constraint) =>
        base.MeasureOverride(new Size(constraint.Width, double.PositiveInfinity));

    /// <summary>
    /// A row's measured size already carries its own margin, and the row is inset by that margin again as it is
    /// arranged — so a list that added the margin to the running total a second time stood every row further down and
    /// narrower across than its own measure had promised, and a page fitted to that measure cut the last row of the
    /// list under its own edge. The slot a row is given is therefore its measured size, whole, with the margins left
    /// to the row; only a row whose measure has not caught up with its own animation is arranged at the size it last
    /// wore, which is the content the margin sits outside of.
    /// </summary>
    protected override Size ArrangeOverride(Size arrangeBounds)
    {
        bool vertical = Orientation == Orientation.Vertical;
        double along = 0;
        foreach (UIElement child in InternalChildren)
        {
            if (child.Visibility == Visibility.Collapsed) continue;
            FrameworkElement row = (FrameworkElement)child;
            Thickness margin = row.Margin;
            double slot = vertical
                ? Math.Max(child.DesiredSize.Height, row.ActualHeight + margin.Top + margin.Bottom)
                : Math.Max(child.DesiredSize.Width, row.ActualWidth + margin.Left + margin.Right);
            slot = Math.Max(0, slot);
            child.Arrange(vertical
                ? new Rect(0, along, arrangeBounds.Width, slot)
                : new Rect(along, 0, slot, arrangeBounds.Height));
            along += slot;
        }
        return arrangeBounds;
    }

    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);
        dc.DrawGeometry(_fill, null, _shape);
    }

    void UpdateHighlight()
    {
        ButtonBase? row = InternalChildren.OfType<ButtonBase>().FirstOrDefault(button => button.IsMouseOver);
        if (row != null) MoveTo(row);

        bool lit = row != null, pressed = row is { IsPressed: true };
        if (lit != _lit) _shown.Tune(lit ? 420 : 90, lit ? 41 : 19);
        if (pressed != _pressed) _squish.Tune(pressed ? 700 : 380, pressed ? 44 : 16);
        _lit = lit;
        _pressed = pressed;
        _shown.Target = lit ? 1 : 0;
        _squish.Target = pressed ? 1 : 0;
        _loop.Start();
    }

    void MoveTo(ButtonBase row)
    {
        double top = VisualTreeHelper.GetOffset(row).Y, bottom = top + row.RenderSize.Height;
        if (_shown.Value < HiddenBelow)
        {
            _top.Snap(top);
            _bottom.Snap(bottom);
            _squish.Value = AppearSquish;
            return;
        }

        if (top != _top.Target)
        {
            bool down = top > _top.Target;
            (down ? _bottom : _top).Tune(560, 36);
            (down ? _top : _bottom).Tune(230, 26);
        }
        _top.Target = top;
        _bottom.Target = bottom;
    }

    void Reset()
    {
        _lit = _pressed = false;
        _shown.Snap(0);
        _squish.Snap(0);
        ApplyHighlight();
    }

    bool Advance(double dt)
    {
        bool moving = _top.Advance(dt);
        moving |= _bottom.Advance(dt);
        moving |= _shown.Advance(dt);
        moving |= _squish.Advance(dt);
        ApplyHighlight();
        return moving;
    }

    void ApplyHighlight()
    {
        double x = PressInsetX * _squish.Value, y = PressInsetY * _squish.Value;
        _shape.Rect = new Rect(x, _top.Value + y,
            Math.Max(ActualWidth - x * 2, 0), Math.Max(_bottom.Value - _top.Value - y * 2, 0));
        _fill.Opacity = Math.Clamp(_shown.Value, 0, 1)
            * (HoverOpacity + (PressOpacity - HoverOpacity) * Math.Clamp(_squish.Value, 0, 1));
    }
}
