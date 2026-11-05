using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using MediaUnseal.Services;
using MediaUnseal.Models;

namespace MediaUnseal.Views;

internal static class StreamPropertiesWindow
{
    private static LocalizationManager L => LocalizationManager.Active;

    internal static Window Show(Window owner, MediaStreamInfo stream)
    {
        var window = new Window
        {
            Title = L.Get("StreamPropertiesTitle"),
            Width = 650, Height = 620, MinWidth = 480, MinHeight = 360,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background = Resource("PanelBackground"), Foreground = Resource("MainTextBrush")
        };
        var layout = new Grid { RowDefinitions = new RowDefinitions("Auto,*,Auto") };
        var heading = new Border { Padding = new Thickness(16, 14), BorderBrush = Resource("LineBrush"),
            BorderThickness = new Thickness(0, 0, 0, 1) };
        layout.Children.Add(heading);
        var cards = new Grid { Margin = new Thickness(10, 8) };
        var scroll = new ScrollViewer { Content = cards,
            VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
            AllowAutoHide = false };
        Grid.SetRow(scroll, 1);
        layout.Children.Add(scroll);
        var footer = new Border { Padding = new Thickness(16, 10), BorderBrush = Resource("LineBrush"),
            BorderThickness = new Thickness(0, 1, 0, 0) };
        var close = new Button { Content = L.Get("MainClose"), MinWidth = 100,
            HorizontalAlignment = HorizontalAlignment.Right,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center };
        close.Click += (_, _) => window.Close();
        footer.Child = close;
        Grid.SetRow(footer, 2);
        layout.Children.Add(footer);
        window.Content = layout;
        List<Border> sectionCards = [];
        bool? twoColumns = null;
        void ArrangeCards()
        {
            var wide = window.Bounds.Width >= 620;
            if (twoColumns == wide && cards.Children.Count == sectionCards.Count) return;
            twoColumns = wide;
            var columns = wide ? 2 : 1;
            cards.Children.Clear();
            cards.RowDefinitions.Clear();
            cards.ColumnDefinitions = new ColumnDefinitions(wide ? "*,*" : "*");
            for (var i = 0; i < (sectionCards.Count + columns - 1) / columns; i++)
                cards.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            for (var i = 0; i < sectionCards.Count; i++)
            {
                Grid.SetRow(sectionCards[i], i / columns);
                Grid.SetColumn(sectionCards[i], i % columns);
                cards.Children.Add(sectionCards[i]);
            }
        }
        void Refresh()
        {
            window.Title = L.Get("StreamPropertiesTitle");
            heading.Child = BuildHeading(stream);
            sectionCards = BuildCards(stream).ToList();
            cards.Children.Clear();
            twoColumns = null;
            ArrangeCards();
            close.Content = L.Get("MainClose");
        }
        void OnLanguageChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs args)
        {
            if (args.PropertyName != "Item[]") return;
            Refresh();
        }
        L.PropertyChanged += OnLanguageChanged;
        window.Closed += (_, _) => L.PropertyChanged -= OnLanguageChanged;
        window.SizeChanged += (_, _) => ArrangeCards();
        Refresh();
        UiScale.Attach(window);
        window.Show(owner);
        return window;
    }

    private static IBrush? Resource(string key) => Application.Current?.Resources[key] as IBrush;

    private static Control BuildHeading(MediaStreamInfo stream)
    {
        var (icon, title) = stream.VisualKind switch
        {
            VisualKind.Video => ("▣", L.Get("StreamHeaderVideo")),
            VisualKind.Image => ("▧", L.Get(stream.IsCoverArt ? "StreamHeaderCover" : "StreamHeaderImage")),
            VisualKind.Animation => ("◇", L.Get("StreamHeaderAnimation")),
            _ => stream.CodecType switch
            {
                "audio" => ("♫", L.Get("StreamHeaderAudio")),
                "subtitle" => ("≡", L.Get("StreamHeaderSubtitle")),
                "attachment" => ("▤", L.Get("StreamHeaderAttachment")),
                _ => ("•", L.Get("StreamHeaderOther"))
            }
        };
        var heading = new Grid { ColumnDefinitions = new ColumnDefinitions("46,*,Auto"), ColumnSpacing = 9 };
        heading.Children.Add(new TextBlock { Text = icon, FontSize = 34,
            Foreground = Resource("AccentBrush"), VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center });
        var description = new StackPanel { Spacing = 4 };
        description.Children.Add(new TextBlock { Text = L.Get("StreamPropertiesHeading"),
            FontSize = 12, Foreground = Resource("MutedTextBrush") });
        description.Children.Add(new TextBlock { Text = title, FontSize = 22,
            FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.WrapWithOverflow });
        var summary = new List<string>();
        if (stream.Width is { } width && stream.Height is { } height)
            summary.Add(L.Format("StreamResolutionValue", width, height));
        if (stream.CodecType == "audio")
        {
            if (stream.SampleRate is { } sampleRate) summary.Add(L.Format("StreamSampleRateValue", sampleRate));
            if (stream.Channels is { } channels) summary.Add(L.Format("StreamSummaryChannels", channels));
        }
        else if (stream.VisualKind is VisualKind.Video or VisualKind.Animation && stream.FrameRate is { } rate)
            summary.Add(L.Format("StreamFrameRateValue", rate));
        if (stream.Language is { } language)
            summary.Add(language == "und" ? L.Get("StreamUnknownLanguage") : language);
        if (summary.Count > 0) description.Children.Add(new TextBlock
        {
            Text = string.Join(" · ", summary), Foreground = Resource("MutedTextBrush"),
            TextWrapping = TextWrapping.WrapWithOverflow
        });
        Grid.SetColumn(description, 1);
        heading.Children.Add(description);
        if (!string.IsNullOrWhiteSpace(stream.Codec))
        {
            var badge = new Border { Padding = new Thickness(9, 5),
                Margin = new Thickness(8, 0, 0, 0), BorderBrush = Resource("LineBrush"),
                BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(4),
                VerticalAlignment = VerticalAlignment.Center,
                Child = new TextBlock { Text = stream.Codec, MaxWidth = 100,
                    TextWrapping = TextWrapping.WrapWithOverflow, Foreground = Resource("AccentBrush") } };
            Grid.SetColumn(badge, 2);
            heading.Children.Add(badge);
        }
        return heading;
    }

    private static IEnumerable<Border> BuildCards(MediaStreamInfo stream)
    {
        var properties = Properties(stream).ToArray();
        var isVisual = stream.VisualKind is VisualKind.Video or VisualKind.Image or VisualKind.Animation;
        var sections = new[]
        {
            ("basic", "StreamSectionBasic", "▣"),
            ("audio", "StreamSectionAudio", "♫"),
            ("visual", stream.VisualKind is VisualKind.Image or VisualKind.Animation
                ? "StreamSectionImage" : "StreamSectionVideo", "▧"),
            ("timing", "StreamSectionTiming", "◷"),
            ("identity", "StreamSectionIdentification", "#"),
            ("other", "StreamSectionOther", "⋯")
        };
        foreach (var (section, title, icon) in sections)
        {
            var values = properties.Where(item => SectionFor(item.Label, stream, isVisual) == section).ToArray();
            if (values.Length == 0) continue;
            var body = new StackPanel { Spacing = 6 };
            body.Children.Add(new TextBlock { Text = icon + "  " + L.Get(title),
                FontSize = 14, FontWeight = FontWeight.SemiBold,
                Foreground = Resource("AccentBrush"), Margin = new Thickness(0, 0, 0, 4) });
            foreach (var (label, value) in values)
            {
                var pair = new Grid { ColumnDefinitions = new ColumnDefinitions("118,*"), ColumnSpacing = 7,
                    MinHeight = 20 };
                pair.Children.Add(new TextBlock { Text = label + ":",
                    Foreground = Resource("MutedTextBrush"), TextWrapping = TextWrapping.WrapWithOverflow });
                var content = new TextBlock { Text = value, TextWrapping = TextWrapping.WrapWithOverflow };
                Grid.SetColumn(content, 1);
                pair.Children.Add(content);
                body.Children.Add(pair);
            }
            yield return new Border { Child = body, Background = Resource("PanelAltBackground"),
                BorderBrush = Resource("LineBrush"), BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(5), Padding = new Thickness(11),
                Margin = new Thickness(4, 4, 4, 7) };
        }
    }

    private static string SectionFor(string label, MediaStreamInfo stream, bool isVisual)
    {
        if (label == L.Get("MainColumnType") || label == L.Get("StreamSourceIndex")
            || label == L.Get("MainColumnCodec") || label == L.Get("StreamCodecLongName")
            || label == L.Get("MainColumnLanguage") || label == L.Get("StreamTitle")) return "basic";
        if (label == L.Get("StreamCodecTagString") || label == L.Get("StreamCodecTag")) return "identity";
        if (label == L.Get("StreamDuration") || label == L.Get("StreamFrameRate")
            || label == L.Get("StreamFrameCount") || label is "time_base" or "start_time"
                or "avg_frame_rate" or "r_frame_rate") return "timing";
        if (stream.CodecType == "audio" && (label == L.Get("MainColumnBitrate")
            || label == L.Get("StreamSampleRate") || label == L.Get("ConversionChannels")
            || label == L.Get("StreamChannelLayout") || label == L.Get("StreamSampleFormat")
            || label is "bits_per_sample" or "bits_per_raw_sample")) return "audio";
        if (isVisual && (label == L.Get("MainColumnBitrate") || label == L.Get("StreamResolution")
            || label == L.Get("StreamProfile") || label == L.Get("StreamPixelFormat")
            || label == L.Get("StreamImageFormat") || label == L.Get("StreamLevel")
            || label == L.Get("StreamDisplayAspectRatio") || label == L.Get("StreamSampleAspectRatio")
            || label == L.Get("StreamColorRange") || label == L.Get("StreamColorSpace")
            || label == L.Get("StreamColorTransfer") || label == L.Get("StreamColorPrimaries")
            || label is "field_order" or "chroma_location")) return "visual";
        return "other";
    }

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
