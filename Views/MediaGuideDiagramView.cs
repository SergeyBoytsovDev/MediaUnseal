using System;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Layout;
using Avalonia.Media;
using MediaUnseal.Services;

namespace MediaUnseal.Views;

internal static class MediaGuideDiagramView
{
    internal static Border Create(MediaGuideDiagram diagram)
    {
        var canvas = new Canvas { Width = diagram.Width, Height = diagram.Height };
        foreach (var node in diagram.Nodes.Where(node => node.Kind == "frame"))
            canvas.Children.Add(CreateNode(diagram.Id, node));
        foreach (var link in diagram.Links)
        {
            var from = diagram.Nodes.First(node => node.Id == link.From);
            var to = diagram.Nodes.First(node => node.Id == link.To);
            var start = new Point(from.X + from.Width, from.Y + from.Height / 2);
            var end = new Point(to.X - 7, to.Y + to.Height / 2);
            AddLine(canvas, start, end);
            var angle = Math.Atan2(end.Y - start.Y, end.X - start.X);
            const double length = 7;
            AddLine(canvas, end, new Point(end.X - length * Math.Cos(angle - 0.55), end.Y - length * Math.Sin(angle - 0.55)));
            AddLine(canvas, end, new Point(end.X - length * Math.Cos(angle + 0.55), end.Y - length * Math.Sin(angle + 0.55)));
        }
        foreach (var node in diagram.Nodes.Where(node => node.Kind != "frame"))
            canvas.Children.Add(CreateNode(diagram.Id, node));

        var scroll = new ScrollViewer
        {
            Content = canvas,
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled
        };
        var border = new Border { Child = scroll, Margin = new Thickness(0, 12, 0, 18) };
        border.Classes.Add("guide-diagram");
        return border;
    }

    private static Border CreateNode(string diagramId, MediaGuideDiagramNode node)
    {
        var label = new TextBlock
        {
            Text = node.Label,
            Tag = new MediaGuideDiagramLabel(diagramId, node.Id),
            TextWrapping = TextWrapping.Wrap,
            TextAlignment = TextAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = node.Kind == "frame" ? VerticalAlignment.Top : VerticalAlignment.Center,
            FontWeight = node.Kind == "frame" ? FontWeight.SemiBold : FontWeight.Medium
        };
        label.Classes.Add("guide-label");
        var border = new Border
        {
            Child = label,
            Width = node.Width,
            Height = node.Height,
            Padding = new Thickness(7),
            CornerRadius = new CornerRadius(5)
        };
        border.Classes.Add(node.Kind switch { "frame" => "guide-frame", "note" => "guide-note", _ => "guide-node" });
        Canvas.SetLeft(border, node.X);
        Canvas.SetTop(border, node.Y);
        return border;
    }

    private static void AddLine(Canvas canvas, Point start, Point end)
    {
        var line = new Line { StartPoint = start, EndPoint = end, StrokeThickness = 1.6 };
        line.Classes.Add("guide-arrow");
        canvas.Children.Add(line);
    }
}
