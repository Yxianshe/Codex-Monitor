using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace CodexMonitorV2;

/// <summary>
/// Draws the full scene before clipping it to the window. Unlike moving an Image
/// control after UniformToFill has already cropped it, this keeps the real source
/// image available to both the visible background and liquid-glass backdrop capture.
/// </summary>
public sealed class SceneBackdrop : Control
{
    // A small optical overscan keeps both pan axes usable at the UI's 1.00x
    // setting while still guaranteeing that no source edge can enter the window.
    private const double BaseOverscan = 1.10;
    private const double MaxPositionX = 0.35;
    private const double MaxPositionY = 0.28;
    private static readonly LinearGradientBrush NeutralGlassBackdrop = new()
    {
        StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
        EndPoint = new RelativePoint(1, 1, RelativeUnit.Relative),
        GradientStops = new GradientStops
        {
            new(Color.Parse("#E77C8C9D"), 0.00),
            new(Color.Parse("#D858697C"), 0.52),
            new(Color.Parse("#ECA7B2BD"), 1.00)
        }
    };
    private static readonly LinearGradientBrush NeutralGlassSheen = new()
    {
        StartPoint = new RelativePoint(1, 0, RelativeUnit.Relative),
        EndPoint = new RelativePoint(0, 1, RelativeUnit.Relative),
        GradientStops = new GradientStops
        {
            new(Color.Parse("#00FFFFFF"), 0.00),
            new(Color.Parse("#42FFFFFF"), 0.46),
            new(Color.Parse("#08FFFFFF"), 1.00)
        }
    };

    public static readonly StyledProperty<IImage?> SourceProperty =
        AvaloniaProperty.Register<SceneBackdrop, IImage?>(nameof(Source));
    public static readonly StyledProperty<double> ZoomProperty =
        AvaloniaProperty.Register<SceneBackdrop, double>(nameof(Zoom), 1.0);
    public static readonly StyledProperty<double> PositionXProperty =
        AvaloniaProperty.Register<SceneBackdrop, double>(nameof(PositionX));
    public static readonly StyledProperty<double> PositionYProperty =
        AvaloniaProperty.Register<SceneBackdrop, double>(nameof(PositionY));

    static SceneBackdrop() => AffectsRender<SceneBackdrop>(
        SourceProperty,
        ZoomProperty,
        PositionXProperty,
        PositionYProperty);

    public IImage? Source { get => GetValue(SourceProperty); set => SetValue(SourceProperty, value); }
    public double Zoom { get => GetValue(ZoomProperty); set => SetValue(ZoomProperty, value); }
    public double PositionX { get => GetValue(PositionXProperty); set => SetValue(PositionXProperty, value); }
    public double PositionY { get => GetValue(PositionYProperty); set => SetValue(PositionYProperty, value); }

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        IImage? source = Source;
        Size sourceSize = source?.Size ?? default;
        if (Bounds.Width <= 0 || Bounds.Height <= 0)
            return;
        if (source is null || sourceSize.Width <= 0 || sourceSize.Height <= 0)
        {
            context.FillRectangle(NeutralGlassBackdrop, new Rect(0, 0, Bounds.Width, Bounds.Height));
            context.FillRectangle(NeutralGlassSheen, new Rect(0, 0, Bounds.Width, Bounds.Height));
            return;
        }

        double zoom = Math.Clamp(double.IsFinite(Zoom) ? Zoom : 1.0, 1.0, 1.6);
        double coverScale = Math.Max(Bounds.Width / sourceSize.Width, Bounds.Height / sourceSize.Height)
                            * BaseOverscan
                            * zoom;
        double drawWidth = sourceSize.Width * coverScale;
        double drawHeight = sourceSize.Height * coverScale;

        double maxX = Math.Max(0, (drawWidth - Bounds.Width) / 2);
        double maxY = Math.Max(0, (drawHeight - Bounds.Height) / 2);
        double normalizedX = Math.Clamp((double.IsFinite(PositionX) ? PositionX : 0) / MaxPositionX, -1, 1);
        double normalizedY = Math.Clamp((double.IsFinite(PositionY) ? PositionY : 0) / MaxPositionY, -1, 1);
        double offsetX = normalizedX * maxX;
        double offsetY = normalizedY * maxY;

        Rect sourceRect = new(sourceSize);
        Rect destination = new(
            (Bounds.Width - drawWidth) / 2 + offsetX,
            (Bounds.Height - drawHeight) / 2 + offsetY,
            drawWidth,
            drawHeight);
        context.DrawImage(source, sourceRect, destination);
    }
}
