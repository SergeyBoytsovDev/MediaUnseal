using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using TrackExtractor.Services;
using TrackExtractor.Models;

namespace TrackExtractor.Views;

internal static class StreamPropertiesWindow
{
    private static LocalizationManager L => LocalizationManager.Active;

    internal static Window Show(Window owner, MediaStreamInfo stream)
    {
        var window = new Window
        {
            Title = L.Format("StreamPropertiesTitle", stream.Index?.ToString(CultureInfo.InvariantCulture) ?? "—"),
            Width = 570, Height = 610, MinWidth = 390, MinHeight = 300,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background = Resource("PanelBackground"), Foreground = Resource("MainTextBrush")
        };
        var rows = new StackPanel { Spacing = 0, Margin = new Thickness(15) };
        void Refresh()
        {
            window.Title = L.Format("StreamPropertiesTitle", stream.Index?.ToString(CultureInfo.InvariantCulture) ?? "—");
            rows.Children.Clear();
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
        }
        Refresh();
        var close = new Button { Content = L.Get("MainClose"), Width = 100,
            HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(12) };
        close.Click += (_, _) => window.Close();
        void OnLanguageChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs args)
        {
            if (args.PropertyName != "Item[]") return;
            Refresh();
            close.Content = L.Get("MainClose");
        }
        L.PropertyChanged += OnLanguageChanged;
        window.Closed += (_, _) => L.PropertyChanged -= OnLanguageChanged;
        var layout = new DockPanel();
        DockPanel.SetDock(close, Dock.Bottom);
        layout.Children.Add(close);
        layout.Children.Add(new ScrollViewer { Content = rows,
            VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
            AllowAutoHide = false });
        window.Content = layout;
        window.Show(owner);
        return window;
    }

    private static IBrush? Resource(string key) => Application.Current?.Resources[key] as IBrush;

    internal static IEnumerable<(string Label, string Value)> Properties(MediaStreamInfo stream)
    {
        yield return (L.Get("MainColumnType"), stream.VisualKind switch
        {
            VisualKind.Video => L.Get("MainTypeVideo"), VisualKind.Image => L.Get("MainTypeImage"),
            VisualKind.Animation => L.Get("MainTypeAnimation"),
            _ => stream.CodecType switch
            {
                "audio" => L.Get("MainTypeAudio"), "subtitle" => L.Get("MainTypeSubtitle"),
                "attachment" => L.Get("MainTypeAttachment"), null => L.Get("StreamNotSpecified"),
                _ => stream.CodecType
            }
        });
        if (stream.Index is { } index) yield return (L.Get("StreamSourceIndex"), index.ToString(CultureInfo.InvariantCulture));
        if (stream.Codec is { } codec) yield return (L.Get("MainColumnCodec"), codec);
        if (stream.Language is { } language) yield return (L.Get("MainColumnLanguage"), language == "und" ? L.Get("StreamUnknownLanguage") : language);
        if (stream.Title is { } title) yield return (L.Get("StreamTitle"), title);
        if (stream.Duration is { } duration) yield return (L.Get("StreamDuration"), duration.ToString());
        if (stream.BitRate is { } bitRate) yield return (L.Get("MainColumnBitrate"), L.Format("StreamBitRateValue", bitRate));
        if (stream.Width is { } width && stream.Height is { } height)
            yield return (L.Get("StreamResolution"), L.Format("StreamResolutionValue", width, height));
        if (stream.FrameRate is { } rate) yield return (L.Get("StreamFrameRate"), L.Format("StreamFrameRateValue", rate));
        if (stream.Profile is { } profile) yield return (L.Get("StreamProfile"), profile);
        if (stream.PixelFormat is { } pixelFormat) yield return (L.Get("StreamPixelFormat"), pixelFormat);
        if (stream.SampleRate is { } sampleRate) yield return (L.Get("StreamSampleRate"), L.Format("StreamSampleRateValue", sampleRate));
        if (stream.Channels is { } channels) yield return (L.Get("ConversionChannels"), channels.ToString(CultureInfo.InvariantCulture));
        if (stream.ChannelLayout is { } layout) yield return (L.Get("StreamChannelLayout"), layout);
        if (stream.FrameCount is { } count) yield return (L.Get("StreamFrameCount"), L.Format("StreamFrameCountValue", count));
        if (stream.ImageFormat is { } format) yield return (L.Get("StreamImageFormat"), format);
        foreach (var (key, value) in stream.ExtraProperties ?? new Dictionary<string, string>())
            yield return (ExtraLabel(key), value);
        foreach (var (key, value) in stream.Tags.OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase))
            if (key is not "language" and not "title") yield return (L.Format("StreamTag", key), value);
        foreach (var (key, value) in stream.Disposition.OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase))
            if (value == 1 || (value == 0 && key is ("default" or "forced")))
                yield return (L.Format("StreamFlag", key), L.Get(value == 1 ? "StreamYes" : "StreamNo"));
    }

    private static string ExtraLabel(string key) => key switch
    {
        "codec_long_name" => L.Get("StreamCodecLongName"), "codec_tag_string" => L.Get("StreamCodecTagString"),
        "codec_tag" => L.Get("StreamCodecTag"), "level" => L.Get("StreamLevel"), "sample_fmt" => L.Get("StreamSampleFormat"),
        "display_aspect_ratio" => L.Get("StreamDisplayAspectRatio"), "sample_aspect_ratio" => L.Get("StreamSampleAspectRatio"),
        "color_range" => L.Get("StreamColorRange"), "color_space" => L.Get("StreamColorSpace"),
        "color_transfer" => L.Get("StreamColorTransfer"), "color_primaries" => L.Get("StreamColorPrimaries"),
        _ => key
    };
}
