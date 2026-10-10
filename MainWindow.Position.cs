using System.Windows;
using System.Windows.Input;
using System.Windows.Media.Effects;

namespace DynamicIsland;

public partial class MainWindow
{
    const int AlongStep = 8;

    int Along() => Math.Clamp(Settings.Along, -Placement.Travel(_screen, Settings.Edge), Placement.Travel(_screen, Settings.Edge));

    void Place()
    {
        _screen = Placement.Chosen(_hwnd);
        ScreenEdge edge = Settings.Edge;
        _growsUp = Placement.GrowsUp(edge);
        Size box = Placement.BoxDip(edge);
        Width = box.Width;
        Height = box.Height;
        Placement.Move(_hwnd, Placement.WindowPx(_screen, edge, Settings.Anchor, Along()), box, _screen.ScaleX, _screen.ScaleY);
        _glass.Place();

        TurnSpin.CenterX = box.Width / 2;
        TurnSpin.Angle = Placement.Angle(edge);
        Point on = Placement.Anchor(edge, box);
        TurnShift.X = on.X - box.Width / 2;
        TurnShift.Y = on.Y;
        if (Shadow.Effect is DropShadowEffect shade) shade.Direction = Placement.ShadowAway(edge);
        ApplyLook();
    }

    void SetAlong(int px)
    {
        int limit = AlongLimit();
        px = Math.Clamp(px, -limit, limit);
        if (px == Settings.Along) return;
        Settings.Along = px;
        Place();
    }

    /// How far along its edge the island can be pushed on the screen it is on now. Never nothing, since a slider
    /// whose ends meet has nowhere to stand and reads as broken rather than as an island already at its limit.
    int AlongLimit() => Math.Max(AlongStep, Placement.Travel(_screen, Settings.Edge));

    void SetPlace(ScreenEdge edge, ScreenAnchor anchor, int along, int gap, string screen)
    {
        Settings.Edge = edge;
        Settings.Anchor = anchor;
        Settings.Monitor = screen;
        Settings.Along = along;
        Settings.Gap = gap;
        Place();
        UpdatePosition();
    }

    void Edge_Click(object sender, RoutedEventArgs e) => SetEdge((ScreenEdge)StepOption([0, 1, 2, 3], (int)Settings.Edge, 1, true));

    /// The three segments are Center, Start and End in that order, which is how the anchor is numbered too; a press
    /// outside the row asks the segments for the next one along instead, so the row still turns on an ordinary click.
    void Anchor_Click(object sender, RoutedEventArgs e) => SetAnchor((ScreenAnchor)Math.Clamp(AnchorSegments.PickUnderPointer(), 0, 2));

    void Monitor_Click(object sender, RoutedEventArgs e) => SetScreen(1);

    void AlongSlider_Changed(object? sender, EventArgs e) => SetAlong((int)AlongSlider.Value);

    void WorkArea_Click(object sender, RoutedEventArgs e)
    {
        Settings.WorkArea = !Settings.WorkArea;
        Place();
        UpdatePosition();
    }

    void SetEdge(ScreenEdge edge)
    {
        if (edge == Settings.Edge) return;
        Settings.Edge = edge;
        Place();
        UpdatePosition();
    }

    void SetAnchor(ScreenAnchor anchor)
    {
        if (anchor == Settings.Anchor) return;
        Settings.Anchor = anchor;
        Place();
        UpdatePosition();
    }

    void SetScreen(int by)
    {
        ScreenInfo[] all = CachedScreens();
        if (all.Length < 2) return;
        int at = Array.FindIndex(all, s => string.Equals(s.Name, _screen.Name, StringComparison.OrdinalIgnoreCase));
        Settings.Monitor = all[(Math.Max(at, 0) + by + all.Length) % all.Length].Name;
        Place();
        UpdatePosition();
    }

    void SetMonitor(string name)
    {
        if (string.Equals(name, _screen.Name, StringComparison.OrdinalIgnoreCase)) return;
        Settings.Monitor = name;
        Place();
        UpdatePosition();
    }

    ScreenInfo[] CachedScreens()
    {
        if (_screens == null || DateTime.UtcNow - _screensAt > TimeSpan.FromSeconds(2))
        {
            _screens = Placement.Screens();
            _screensAt = DateTime.UtcNow;
        }
        return _screens;
    }

    static ScreenEdge Nearest(Rect bound, double x, double y)
    {
        double left = x - bound.Left, right = bound.Right - x, top = y - bound.Top, bottom = bound.Bottom - y;
        double least = Math.Min(Math.Min(left, right), Math.Min(top, bottom));
        if (least == top) return ScreenEdge.Top;
        if (least == bottom) return ScreenEdge.Bottom;
        return least == left ? ScreenEdge.Left : ScreenEdge.Right;
    }

    void Drag_Click(object sender, RoutedEventArgs e)
    {
        _moving = !_moving;
        Root.Cursor = _moving ? Cursors.SizeAll : null;
        if (_moving) SelectPanel(Panel.None);
        UpdateView();
        UpdateTargets();
        UpdatePosition();
    }

    void PlaceReset_Click(object sender, RoutedEventArgs e) => SetPlace(ScreenEdge.Top, ScreenAnchor.Center, 0, 8, "");

    void MoveTo(Point at)
    {
        ScreenInfo screen = Placement.Chosen(_hwnd);
        Rect bound = Settings.WorkArea ? screen.Work : screen.Px;
        ScreenEdge edge = Nearest(bound, at.X, at.Y);
        bool side = !Placement.Horizontal(edge);
        double scale = side ? screen.ScaleY : screen.ScaleX;

        double fromEdge = edge switch
        {
            ScreenEdge.Top => at.Y - bound.Top,
            ScreenEdge.Bottom => bound.Bottom - at.Y,
            ScreenEdge.Left => at.X - bound.Left,
            _ => bound.Right - at.X,
        };
        PillShape d = ShapeOf(_view);
        int gap = (int)Math.Clamp(fromEdge / scale - d.Height * _userScale.Value / 2, 0, Settings.MaxGap);

        double alongPointer = side ? at.Y : at.X, from = side ? bound.Top : bound.Left;
        double centre = (alongPointer - from) / scale, span = (side ? bound.Height : bound.Width) / scale;
        double rest = Placement.Rest(Settings.Anchor, d.Width * _userScale.Value, _userScale.Value);
        int along = Settings.Anchor switch
        {
            ScreenAnchor.Start => (int)Math.Round(centre - rest),
            ScreenAnchor.End => (int)Math.Round(span - Placement.Along + rest - centre),
            _ => (int)Math.Round(centre - span / 2),
        };

        if (edge == Settings.Edge && gap == Settings.Gap && along == Settings.Along && screen.Name == Settings.Monitor) return;
        Settings.Monitor = screen.Name;
        SetPlace(edge, Settings.Anchor, Math.Clamp(along, -Placement.Travel(screen, edge), Placement.Travel(screen, edge)), gap, screen.Name);
    }

    static string EdgeName(ScreenEdge edge) => edge switch
    {
        ScreenEdge.Bottom => "низу",
        ScreenEdge.Left => "слева",
        ScreenEdge.Right => "справа",
        _ => "верху",
    };

    /// The same edge named the way a caption wants it: «верх», not «верху», since the caption stands on its own
    /// rather than finishing a sentence.
    static string EdgeShort(ScreenEdge edge) => edge switch
    {
        ScreenEdge.Bottom => "Низ",
        ScreenEdge.Left => "Левый край",
        ScreenEdge.Right => "Правый край",
        _ => "Верх",
    };

    static string AnchorName(ScreenEdge edge, ScreenAnchor anchor) => anchor switch
    {
        ScreenAnchor.Free => "свободно",
        ScreenAnchor.Center => "центр",
        _ => edge switch
        {
            ScreenEdge.Top or ScreenEdge.Bottom => anchor == ScreenAnchor.Start ? "влево" : "вправо",
            _ => anchor == ScreenAnchor.Start ? "вверх" : "вниз",
        },
    };

    /// Which way round the three anchors read: along a top or a bottom the island travels sideways, along a side it
    /// travels up and down, and the segments have to say so or «слева» on a left edge means nothing.
    static string AnchorLabels(ScreenEdge edge) =>
        Placement.Horizontal(edge) ? "Центр|Слева|Справа" : "Центр|Сверху|Снизу";

    void UpdatePosition()
    {
        ScreenInfo[] all = CachedScreens();
        int at = Math.Max(0, Array.FindIndex(all, s => string.Equals(s.Name, _screen.Name, StringComparison.OrdinalIgnoreCase)));
        EdgeText.Text = EdgeName(Settings.Edge);
        PlaceText.Text = EdgeShort(Settings.Edge) + " · " + AnchorName(Settings.Edge, Settings.Anchor);
        MonitorText.Text = all.Length > 1 ? $"{at + 1}·{Math.Round(_screen.ScaleX * 100)}%" : $"{Math.Round(_screen.ScaleX * 100)}%";
        AlongText.Text = (Settings.Along > 0 ? "+" : "") + Settings.Along + " px";
        if (AnchorSegments.Labels != AnchorLabels(Settings.Edge)) AnchorSegments.Labels = AnchorLabels(Settings.Edge);
        AnchorSegments.Set(Math.Clamp((int)Settings.Anchor, 0, 2), LookView.IsVisible);
        int limit = AlongLimit();
        AlongSlider.Minimum = -limit;
        AlongSlider.Maximum = limit;
        AlongSlider.Step = AlongStep;
        AlongSlider.Set(Math.Clamp(Settings.Along, -limit, limit), LookView.IsVisible);
        WorkSwitch.Set(Settings.WorkArea, LookView.IsVisible);
        DragText.Text = _moving ? "Тяните остров" : "Переместить мышью";
        DragText.Foreground = _moving ? _orange : _dim;
    }
}
