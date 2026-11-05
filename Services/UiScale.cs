using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace TrackExtractor.Services;

internal static class UiScale
{
    private static readonly Dictionary<Window, double> Active = [];

    internal static double Current(Window window) => Active.GetValueOrDefault(window, 1);

    internal static double Automatic(int width, int height)
    {
        var ratio = Math.Min(width / 2560d, height / 1440d);
        return Math.Clamp(0.6 + 0.4 * ratio, 0.75, 1.25);
    }

    internal static void Attach(Window window)
    {
        if (Application.Current is not TrackExtractor.App app || window.Content is not Control content)
            return;
        var transform = new ScaleTransform(1, 1);
        window.Content = null;
        window.Content = new LayoutTransformControl { LayoutTransform = transform, Child = content };
        var appearance = app.Appearance;
        var baseMinimumWidth = window.MinWidth;
        var baseMinimumHeight = window.MinHeight;
        Active[window] = 1;

        void Apply(object? sender, EventArgs args)
        {
            if (!window.IsVisible) return;
            var screen = window.Screens.ScreenFromWindow(window) ?? window.Screens.Primary;
            if (screen is null) return;
            var dpi = Math.Max(1, screen.Scaling);
            var availableWidth = screen.WorkingArea.Width / dpi;
            var availableHeight = screen.WorkingArea.Height / dpi;
            var requested = appearance.Preferences.InterfaceScale == 0
                ? Automatic(screen.WorkingArea.Width, screen.WorkingArea.Height)
                : appearance.Preferences.InterfaceScale / 100d;
            var scale = Math.Min(requested,
                Math.Min((availableWidth - 16) / Math.Max(1, baseMinimumWidth),
                    (availableHeight - 16) / Math.Max(1, baseMinimumHeight)));
            scale = Math.Clamp(scale, 0.5, 1.25);
            var previous = Active[window];
            if (Math.Abs(previous - scale) >= 0.001)
            {
                Active[window] = scale;
                transform.ScaleX = transform.ScaleY = scale;
                window.MinWidth = baseMinimumWidth * scale;
                window.MinHeight = baseMinimumHeight * scale;
                window.Width *= scale / previous;
                if (window.SizeToContent == SizeToContent.Manual)
                    window.Height *= scale / previous;
            }
            window.Width = Math.Min(window.Width, availableWidth - 16);
            if (window.SizeToContent == SizeToContent.Manual)
                window.Height = Math.Min(window.Height, availableHeight - 16);
            var area = screen.WorkingArea;
            var widthPixels = (int)Math.Ceiling(window.Width * dpi);
            var effectiveHeight = double.IsFinite(window.Height) ? window.Height : window.Bounds.Height;
            var heightPixels = (int)Math.Ceiling(effectiveHeight * dpi);
            var position = window.Position;
            var x = Math.Clamp(position.X, area.X, Math.Max(area.X, area.Right - widthPixels));
            var y = Math.Clamp(position.Y, area.Y, Math.Max(area.Y, area.Bottom - heightPixels));
            if (x != position.X || y != position.Y)
                window.Position = new PixelPoint(x, y);
        }

        var opened = false;
        window.Opened += (_, _) =>
        {
            opened = true;
            window.Screens.Changed += Apply;
            Apply(null, EventArgs.Empty);
        };
        window.PositionChanged += Apply;
        appearance.ScaleChanged += Apply;
        window.Closed += (_, _) =>
        {
            if (opened) window.Screens.Changed -= Apply;
            window.PositionChanged -= Apply;
            appearance.ScaleChanged -= Apply;
            Active.Remove(window);
        };
    }
}
