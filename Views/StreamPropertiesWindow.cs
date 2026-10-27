using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using TrackExtractor.Models;

namespace TrackExtractor.Views;

internal static class StreamPropertiesWindow
{
    internal static void Show(Window owner, MediaStreamInfo stream)
    {
        var window = new Window
        {
            Title = $"Свойства потока #{stream.Index?.ToString(CultureInfo.InvariantCulture) ?? "—"}",
            Width = 570, Height = 610, MinWidth = 390, MinHeight = 300,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background = Resource("PanelBackground"), Foreground = Resource("MainTextBrush")
        };
        var rows = new StackPanel { Spacing = 0, Margin = new Thickness(15) };
        foreach (var (label, value) in Properties(stream))
        {
            var row = new Grid { ColumnDefinitions = new ColumnDefinitions("170,*"), MinHeight = 29 };
            row.Children.Add(new TextBlock { Text = label, Foreground = Resource("MutedTextBrush"),
                TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 3, 10, 3) });
            var content = new TextBlock { Text = value, TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 3) };
            Grid.SetColumn(content, 1);
            row.Children.Add(content);
            rows.Children.Add(row);
        }
        var close = new Button { Content = "Закрыть", Width = 100,
            HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(12) };
        close.Click += (_, _) => window.Close();
        var layout = new DockPanel();
        DockPanel.SetDock(close, Dock.Bottom);
        layout.Children.Add(close);
        layout.Children.Add(new ScrollViewer { Content = rows,
            VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
            AllowAutoHide = false });
        window.Content = layout;
        window.Show(owner);
    }

    private static IBrush? Resource(string key) => Application.Current?.Resources[key] as IBrush;

    internal static IEnumerable<(string Label, string Value)> Properties(MediaStreamInfo stream)
    {
        yield return ("Тип", stream.VisualKind switch
        {
            VisualKind.Video => "Видео", VisualKind.Image => "Изображение",
            VisualKind.Animation => "Анимация", _ => stream.CodecType ?? "Не указан"
        });
        if (stream.Index is { } index) yield return ("Исходный индекс", index.ToString(CultureInfo.InvariantCulture));
        if (stream.Codec is { } codec) yield return ("Кодек", codec);
        if (stream.Language is { } language) yield return ("Язык", language);
        if (stream.Title is { } title) yield return ("Название", title);
        if (stream.Duration is { } duration) yield return ("Длительность", duration.ToString());
        if (stream.BitRate is { } bitRate) yield return ("Битрейт", $"{bitRate:N0} бит/с");
        if (stream.Width is { } width && stream.Height is { } height)
            yield return ("Разрешение", $"{width} × {height}");
        if (stream.FrameRate is { } rate) yield return ("Частота кадров", $"{rate:0.###} fps");
        if (stream.Profile is { } profile) yield return ("Профиль", profile);
        if (stream.PixelFormat is { } pixelFormat) yield return ("Пиксельный формат", pixelFormat);
        if (stream.SampleRate is { } sampleRate) yield return ("Частота дискретизации", $"{sampleRate:N0} Гц");
        if (stream.Channels is { } channels) yield return ("Каналы", channels.ToString(CultureInfo.InvariantCulture));
        if (stream.ChannelLayout is { } layout) yield return ("Расположение каналов", layout);
        if (stream.FrameCount is { } count) yield return ("Кадров", count.ToString("N0"));
        if (stream.ImageFormat is { } format) yield return ("Формат изображения", format);
        foreach (var (key, value) in stream.ExtraProperties ?? new Dictionary<string, string>())
            yield return (ExtraLabel(key), value);
        foreach (var (key, value) in stream.Tags.OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase))
            if (key is not "language" and not "title") yield return ($"Тег: {key}", value);
        foreach (var (key, value) in stream.Disposition.OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase))
            if (value == 1 || (value == 0 && key is ("default" or "forced")))
                yield return ($"Флаг: {key}", value == 1 ? "да" : "нет");
    }

    private static string ExtraLabel(string key) => key switch
    {
        "codec_long_name" => "Полное имя кодека", "codec_tag_string" => "Идентификатор кодека",
        "codec_tag" => "Тег кодека", "level" => "Уровень", "sample_fmt" => "Формат сэмплов",
        "display_aspect_ratio" => "Соотношение сторон", "sample_aspect_ratio" => "Соотношение пикселя",
        "color_range" => "Диапазон цвета", "color_space" => "Цветовое пространство",
        "color_transfer" => "Передаточная характеристика", "color_primaries" => "Основные цвета",
        _ => key
    };
}
