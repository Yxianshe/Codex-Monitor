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
        if (source is null || sourceSize.Width <= 0 || sourceSize.Height <= 0 || Bounds.Width <= 0 || Bounds.Height <= 0)
            return;

        double zoom = Math.Clamp(double.IsFinite(Zoom) ? Zoom : 1.0, 1.0, 1.6);
        double coverScale = Math.Max(Bounds.Width / sourceSize.Width, Bounds.Height / sourceSize.Height) * zoom;
        double drawWidth = sourceSize.Width * coverScale;
        double drawHeight = sourceSize.Height * coverScale;

        double desiredX = (double.IsFinite(PositionX) ? PositionX : 0) * Bounds.Width;
        double desiredY = (double.IsFinite(PositionY) ? PositionY : 0) * Bounds.Height;
        double maxX = Math.Max(0, (drawWidth - Bounds.Width) / 2);
        double maxY = Math.Max(0, (drawHeight - Bounds.Height) / 2);
        double offsetX = Math.Clamp(desiredX, -maxX, maxX);
        double offsetY = Math.Clamp(desiredY, -maxY, maxY);

        Rect sourceRect = new(sourceSize);
        Rect destination = new(
            (Bounds.Width - drawWidth) / 2 + offsetX,
            (Bounds.Height - drawHeight) / 2 + offsetY,
            drawWidth,
            drawHeight);
        context.DrawImage(source, sourceRect, destination);
    }
}
