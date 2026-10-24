using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace DynamicIsland;

/// <summary>
/// One band of light that runs across the glass and is gone. The island wears it the single time an opening leaves it
/// taller than it was: the sweep says a pane moved over the screen, which a shape growing on a still desktop does not
/// manage to say by itself. It runs once rather than on a loop because a gleam that repeats is a loading sign.
/// </summary>
public sealed class Gleam : FrameworkElement
{
    const double BandWidth = 130;
    const double Slant = 0.45;

    static readonly TimeSpan RunTime = TimeSpan.FromMilliseconds(760);
    static readonly (double At, double Share)[] Crest = [(0, 0), (0.36, 0.3), (0.5, 1), (0.64, 0.3), (1, 0)];

    readonly LinearGradientBrush _band;
    readonly TranslateTransform _move = new();

    public Gleam()
    {
        _band = new LinearGradientBrush
        {
            MappingMode = BrushMappingMode.Absolute,
            StartPoint = new Point(0, 0),
            EndPoint = new Point(BandWidth, BandWidth * Slant),
            Transform = _move,
        };
        foreach ((double at, double share) in Crest) _band.GradientStops.Add(new GradientStop(Colors.White.WithAlpha(share * 0.42), at));
        Opacity = 0;
    }

    /// <summary>Sends the band across, from the near end to the far one, and leaves the glass alone when it is through.</summary>
    public void Run()
    {
        double w = double.IsNaN(Width) ? ActualWidth : Width;
        if (w <= 0) return;

        // the band leans as it goes, so it has to start and end clear of the pill by as much as its own lean reaches
        double reach = BandWidth + ActualHeight * Slant;
        var sweep = new DoubleAnimation(-reach, w + reach, RunTime)
        {
            EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut },
            FillBehavior = FillBehavior.Stop,
        };
        var fade = new DoubleAnimation(1, 0, RunTime)
        {
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseIn },
            FillBehavior = FillBehavior.Stop,
        };
        fade.Completed += (_, _) => Opacity = 0;
        Opacity = 1;
        _move.BeginAnimation(TranslateTransform.XProperty, sweep);
        BeginAnimation(OpacityProperty, fade);
    }

    protected override void OnRender(DrawingContext dc)
    {
        if (ActualWidth > 0) dc.DrawRectangle(_band, null, new Rect(0, 0, ActualWidth, ActualHeight));
    }
}
