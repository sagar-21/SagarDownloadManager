using System.Windows;
using System.Windows.Media;

namespace DM.App.Controls;

/// <summary>
/// Rolling 60-sample speed graph drawn entirely via DrawingContext — no XAML, no layout pass.
/// Brushes and pen are cached as frozen statics so OnRender does zero GC allocations.
/// AffectsRender on the Speed DP pushes a sample into the ring buffer and queues a repaint.
/// </summary>
public sealed class SpeedGraph : FrameworkElement
{
    private const int Capacity = 60;
    private readonly double[] _ring = new double[Capacity];
    private int _head;
    private int _count;

    // ── Frozen render resources — allocated once at class load, never again ────

    private static readonly SolidColorBrush s_bgBrush;
    private static readonly LinearGradientBrush s_fillBrush;
    private static readonly Pen s_linePen;

    static SpeedGraph()
    {
        s_bgBrush = new SolidColorBrush(Color.FromArgb(20, 255, 255, 255));
        s_bgBrush.Freeze();

        s_fillBrush = new LinearGradientBrush(
            Color.FromArgb(60, 100, 149, 237),
            Color.FromArgb(5,  100, 149, 237),
            new Point(0, 0), new Point(0, 1));
        s_fillBrush.Freeze();

        var strokeBrush = new SolidColorBrush(Color.FromArgb(200, 100, 149, 237));
        strokeBrush.Freeze();
        s_linePen = new Pen(strokeBrush, 1.5);
        s_linePen.Freeze();
    }

    // ── Speed dependency property ──────────────────────────────────────────

    public static readonly DependencyProperty SpeedProperty =
        DependencyProperty.Register(
            nameof(Speed), typeof(double), typeof(SpeedGraph),
            new FrameworkPropertyMetadata(
                0.0,
                FrameworkPropertyMetadataOptions.AffectsRender,
                static (d, e) => ((SpeedGraph)d).PushSample((double)e.NewValue)));

    public double Speed
    {
        get => (double)GetValue(SpeedProperty);
        set => SetValue(SpeedProperty, value);
    }

    private void PushSample(double speed)
    {
        _ring[_head % Capacity] = speed;
        _head++;
        if (_count < Capacity) _count++;
        // AffectsRender triggers InvalidateVisual automatically.
    }

    // ── Rendering — zero heap allocations per call ────────────────────────

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        if (w <= 0 || h <= 0 || _count < 2) return;

        dc.DrawRectangle(s_bgBrush, null, new Rect(0, 0, w, h));

        // Max value for Y scale — floor at 1 to avoid divide-by-zero
        double max = 1.0;
        for (int i = 0; i < _count; i++)
        {
            double v = _ring[(_head - _count + i + Capacity) % Capacity];
            if (v > max) max = v;
        }

        // Gradient fill below line (closed figure)
        double x0 = 0, y0 = SampleY(_ring[(_head - _count + Capacity) % Capacity], max, h);
        var fillGeo = new StreamGeometry();
        using (var ctx = fillGeo.Open())
        {
            ctx.BeginFigure(new Point(x0, h), true, true);
            ctx.LineTo(new Point(x0, y0), true, false);
            for (int i = 1; i < _count; i++)
            {
                double v = _ring[(_head - _count + i + Capacity) % Capacity];
                double x = (i / (double)(_count - 1)) * w;
                ctx.LineTo(new Point(x, SampleY(v, max, h)), true, false);
            }
            ctx.LineTo(new Point(w, h), true, false);
        }
        fillGeo.Freeze();
        dc.DrawGeometry(s_fillBrush, null, fillGeo);

        // Speed line
        var lineGeo = new StreamGeometry();
        using (var ctx = lineGeo.Open())
        {
            ctx.BeginFigure(new Point(x0, y0), false, false);
            for (int i = 1; i < _count; i++)
            {
                double v = _ring[(_head - _count + i + Capacity) % Capacity];
                double x = (i / (double)(_count - 1)) * w;
                ctx.LineTo(new Point(x, SampleY(v, max, h)), true, false);
            }
        }
        lineGeo.Freeze();
        dc.DrawGeometry(null, s_linePen, lineGeo);
    }

    private static double SampleY(double v, double max, double h) =>
        h - (v / max) * (h - 2) - 1;
}
