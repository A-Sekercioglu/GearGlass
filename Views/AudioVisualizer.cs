using System.Windows;
using System.Windows.Media;
using GearGlass.ViewModels;

namespace GearGlass.Views;

/// <summary>
/// Draws a <see cref="VisualizerSnapshot"/> as a single sine wave, a bar chart or a
/// line chart. Holds no animation state: everything comes from the bound snapshot.
/// </summary>
public sealed class AudioVisualizer : FrameworkElement
{
    public static readonly DependencyProperty SnapshotProperty = DependencyProperty.Register(
        nameof(Snapshot),
        typeof(VisualizerSnapshot),
        typeof(AudioVisualizer),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    private const int SinePoints = 96;
    private const double BarGap = 2;

    private static readonly Color Gold = Color.FromRgb(0xE8, 0xBE, 0x78);
    private static readonly Color Copper = Color.FromRgb(0xB7, 0x83, 0x4A);
    private static readonly Color Rust = Color.FromRgb(0x91, 0x4C, 0x40);

    private static readonly Pen WavePen = Freeze(new Pen(new SolidColorBrush(Gold), 1.6)
    {
        StartLineCap = PenLineCap.Round,
        EndLineCap = PenLineCap.Round,
        LineJoin = PenLineJoin.Round
    });

    private static readonly Pen GlowPen = Freeze(new Pen(new SolidColorBrush(Color.FromArgb(0x40, 0xE8, 0xBE, 0x78)), 4.5)
    {
        StartLineCap = PenLineCap.Round,
        EndLineCap = PenLineCap.Round,
        LineJoin = PenLineJoin.Round
    });

    private static readonly Pen BaselinePen = Freeze(new Pen(new SolidColorBrush(Color.FromArgb(0x38, 0xAB, 0x80, 0x50)), 0.8));

    private static readonly Brush BarBrush = Freeze(new LinearGradientBrush(
        new GradientStopCollection
        {
            new(Rust, 0),
            new(Copper, 0.55),
            new(Gold, 1)
        },
        new Point(0, 1),
        new Point(0, 0)));

    private static readonly Brush AreaBrush = Freeze(new LinearGradientBrush(
        Color.FromArgb(0x70, 0xB7, 0x83, 0x4A),
        Color.FromArgb(0x08, 0xB7, 0x83, 0x4A),
        90));

    public VisualizerSnapshot? Snapshot
    {
        get => (VisualizerSnapshot?)GetValue(SnapshotProperty);
        set => SetValue(SnapshotProperty, value);
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        var width = ActualWidth;
        var height = ActualHeight;
        var snapshot = Snapshot;
        if (snapshot is null || width < 4 || height < 4)
        {
            return;
        }

        // Beat flash: the whole drawing briefly brightens.
        drawingContext.PushOpacity(0.78 + 0.22 * snapshot.Pulse);
        switch (snapshot.Mode)
        {
            case VisualizerMode.Bars:
                DrawBars(drawingContext, snapshot, width, height);
                break;
            case VisualizerMode.Line:
                DrawLine(drawingContext, snapshot, width, height);
                break;
            default:
                DrawSine(drawingContext, snapshot, width, height);
                break;
        }

        drawingContext.Pop();
    }

    private static void DrawSine(DrawingContext dc, VisualizerSnapshot s, double width, double height)
    {
        var middle = height / 2;
        dc.DrawLine(BaselinePen, new Point(0, middle), new Point(width, middle));

        var geometry = new StreamGeometry();
        using (var context = geometry.Open())
        {
            for (var i = 0; i <= SinePoints; i++)
            {
                var x = (double)i / SinePoints;
                // Taper the ends so the wave grows out of the baseline.
                var envelope = Math.Sin(Math.PI * x);
                var y = middle - Math.Sin(2 * Math.PI * s.Cycles * x + s.Phase) * s.Amplitude * envelope * (middle - 1);
                var point = new Point(x * width, y);
                if (i == 0)
                {
                    context.BeginFigure(point, false, false);
                }
                else
                {
                    context.LineTo(point, true, true);
                }
            }
        }

        geometry.Freeze();
        dc.DrawGeometry(null, GlowPen, geometry);
        dc.DrawGeometry(null, WavePen, geometry);
    }

    private static void DrawBars(DrawingContext dc, VisualizerSnapshot s, double width, double height)
    {
        var count = s.Values.Length;
        if (count == 0)
        {
            return;
        }

        var barWidth = Math.Max(1, (width - BarGap * (count - 1)) / count);
        for (var i = 0; i < count; i++)
        {
            var barHeight = Math.Max(1.5, s.Values[i] * height);
            var x = i * (barWidth + BarGap);
            dc.DrawRoundedRectangle(
                BarBrush,
                null,
                new Rect(x, height - barHeight, barWidth, barHeight),
                1.2,
                1.2);
        }
    }

    private static void DrawLine(DrawingContext dc, VisualizerSnapshot s, double width, double height)
    {
        var count = s.Values.Length;
        if (count < 2)
        {
            return;
        }

        var points = new Point[count];
        for (var i = 0; i < count; i++)
        {
            var y = height - 1 - s.Values[i] * (height - 2);
            points[i] = new Point(width * i / (count - 1), y);
        }

        var line = BuildSmoothCurve(points, closed: false, height);
        var area = BuildSmoothCurve(points, closed: true, height);
        dc.DrawGeometry(AreaBrush, null, area);
        dc.DrawGeometry(null, GlowPen, line);
        dc.DrawGeometry(null, WavePen, line);
    }

    /// <summary>Catmull-Rom spline through the points, converted to cubic Béziers.</summary>
    private static StreamGeometry BuildSmoothCurve(Point[] points, bool closed, double height)
    {
        var geometry = new StreamGeometry();
        using (var context = geometry.Open())
        {
            if (closed)
            {
                context.BeginFigure(new Point(points[0].X, height), true, true);
                context.LineTo(points[0], false, false);
            }
            else
            {
                context.BeginFigure(points[0], false, false);
            }

            for (var i = 0; i < points.Length - 1; i++)
            {
                var p0 = points[Math.Max(0, i - 1)];
                var p1 = points[i];
                var p2 = points[i + 1];
                var p3 = points[Math.Min(points.Length - 1, i + 2)];
                var c1 = new Point(p1.X + (p2.X - p0.X) / 6, Clamp(p1.Y + (p2.Y - p0.Y) / 6, height));
                var c2 = new Point(p2.X - (p3.X - p1.X) / 6, Clamp(p2.Y - (p3.Y - p1.Y) / 6, height));
                context.BezierTo(c1, c2, p2, !closed, true);
            }

            if (closed)
            {
                context.LineTo(new Point(points[^1].X, height), false, false);
            }
        }

        geometry.Freeze();
        return geometry;
    }

    private static double Clamp(double y, double height) => Math.Clamp(y, 0, height);

    private static T Freeze<T>(T freezable) where T : Freezable
    {
        freezable.Freeze();
        return freezable;
    }
}
