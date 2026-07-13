using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.VisualTree;

namespace LiquidGlassAvaloniaUI
{
    public sealed class LiquidGlassBackdrop
    {
        private LiquidGlassBackdrop()
        {
        }

        public static readonly AttachedProperty<bool> IsExcludedFromCaptureProperty =
            AvaloniaProperty.RegisterAttached<LiquidGlassBackdrop, Visual, bool>(
                "IsExcludedFromCapture",
                false);

        public static readonly AttachedProperty<bool> IsLiveProperty =
            AvaloniaProperty.RegisterAttached<LiquidGlassBackdrop, Visual, bool>(
                "IsLive",
                true);

        public static bool GetIsExcludedFromCapture(Visual visual)
        {
            if (visual is null)
                throw new ArgumentNullException(nameof(visual));

            return visual.GetValue(IsExcludedFromCaptureProperty);
        }

        public static void SetIsExcludedFromCapture(Visual visual, bool value)
        {
            if (visual is null)
                throw new ArgumentNullException(nameof(visual));

            visual.SetValue(IsExcludedFromCaptureProperty, value);
        }

        public static bool GetIsLive(Visual visual)
        {
            if (visual is null)
                throw new ArgumentNullException(nameof(visual));

            return visual.GetValue(IsLiveProperty);
        }

        public static void SetIsLive(Visual visual, bool value)
        {
            if (visual is null)
                throw new ArgumentNullException(nameof(visual));

            visual.SetValue(IsLiveProperty, value);
        }

        public static void Refresh(Control control)
        {
            if (control is null)
                throw new ArgumentNullException(nameof(control));

            LiquidGlassBackdropProvider.Refresh(control);
        }
    }
}
