using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace MediaUnseal.Services;

internal static class UiScale
{
    private static readonly Dictionary<Window, double> Active = [];

    internal static double Current(Window window) => Active.GetValueOrDefault(window, 1);

    internal static double Automatic(int width, int height)
    {
        var ratio = Math.Min(width / 2560d, height / 1440d);
        return Math.Clamp(0.6 + 0.4 * ratio, 0.75, 1.25);
    }

    internal static void Attach(Window window, bool constrainPosition = true)
    {
        if (Application.Current is not MediaUnseal.App app || window.Content is not Control content)
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
        }

        void EnsureAccessible()
        {
            if (!constrainPosition || window.WindowState != WindowState.Normal) return;
            var position = window.Position;
            foreach (var screen in window.Screens.All)
            {
                var width = (int)Math.Ceiling(window.Bounds.Width * screen.Scaling);
                var height = (int)Math.Ceiling(window.Bounds.Height * screen.Scaling);
                var area = screen.WorkingArea;
                if ((long)position.X < area.Right && (long)position.X + width > area.X &&
                    (long)position.Y < area.Bottom && (long)position.Y + height > area.Y)
                    return;
            }
            var primary = window.Screens.Primary;
            if (primary is null) return;
            var target = primary.WorkingArea;
            var targetWidth = (int)Math.Ceiling(window.Bounds.Width * primary.Scaling);
            var targetHeight = (int)Math.Ceiling(window.Bounds.Height * primary.Scaling);
            window.Position = new PixelPoint(target.X + Math.Max(0, (target.Width - targetWidth) / 2),
                target.Y + Math.Max(0, (target.Height - targetHeight) / 2));
        }

        void ScreensChanged(object? sender, EventArgs args)
        {
            Apply(sender, args);
            EnsureAccessible();
        }

        var opened = false;
        window.Opened += (_, _) =>
        {
            opened = true;
            window.Screens.Changed += ScreensChanged;
            Apply(null, EventArgs.Empty);
            EnsureAccessible();
        };
        window.PositionChanged += Apply;
        appearance.ScaleChanged += Apply;
        window.Closed += (_, _) =>
        {
            if (opened) window.Screens.Changed -= ScreensChanged;
            window.PositionChanged -= Apply;
            appearance.ScaleChanged -= Apply;
            Active.Remove(window);
        };
    }
}
