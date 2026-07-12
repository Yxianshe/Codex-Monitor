using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace CodexMonitorV2;

public sealed class RingGauge : Control
{
    public static readonly StyledProperty<double> ValueProperty =
        AvaloniaProperty.Register<RingGauge, double>(nameof(Value));
    public static readonly StyledProperty<IBrush?> StrokeProperty =
        AvaloniaProperty.Register<RingGauge, IBrush?>(nameof(Stroke));
    public static readonly StyledProperty<IBrush?> TrackProperty =
        AvaloniaProperty.Register<RingGauge, IBrush?>(nameof(Track));

    static RingGauge() => AffectsRender<RingGauge>(ValueProperty, StrokeProperty, TrackProperty);

    public double Value { get => GetValue(ValueProperty); set => SetValue(ValueProperty, value); }
    public IBrush? Stroke { get => GetValue(StrokeProperty); set => SetValue(StrokeProperty, value); }
    public IBrush? Track { get => GetValue(TrackProperty); set => SetValue(TrackProperty, value); }

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        double thickness = 5.5;
        double radius = Math.Max(0, Math.Min(Bounds.Width, Bounds.Height) / 2 - thickness);
        Point center = Bounds.Center;
        context.DrawEllipse(null, new Pen(Track ?? Brushes.White, thickness), center, radius, radius);

        double value = Math.Clamp(Value, 0, 100);
        if (value <= 0.01) return;
        double angle = Math.Min(359.9, 360 * value / 100);
        double radians = (angle - 90) * Math.PI / 180;
        Point start = new(center.X, center.Y - radius);
        Point end = new(center.X + radius * Math.Cos(radians), center.Y + radius * Math.Sin(radians));
        StreamGeometry geometry = new();
        using (StreamGeometryContext g = geometry.Open())
        {
            g.BeginFigure(start, false);
            g.ArcTo(end, new Size(radius, radius), 0, angle > 180, SweepDirection.Clockwise);
        }
        context.DrawGeometry(null, new Pen(Stroke ?? Brushes.White, thickness, lineCap: PenLineCap.Round), geometry);
    }
}
