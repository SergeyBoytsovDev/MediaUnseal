using System;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using TrackExtractor.Models;
using TrackExtractor.Services;

namespace TrackExtractor.Views;

public partial class ConversionWindow : Window
{
    private FfmpegConversionCatalog? _catalog;
    private DiagnosticLog? _diagnostics;
    private MediaStreamInfo? _stream;
    private ConversionSettings? _existing;
    private CancellationToken _shutdown;
    private readonly CancellationTokenSource _closed = new();
    private int _encoderRevision;
    private string _kind = "";
    private ConversionFormat? _copyFormat;
    private TrackExportPlan? _copyPlan;
    private ConversionEncoder? _sourceEncoder;
    private string? _sourcePath;
    private ConversionFormat? _activeFormat;
    private readonly System.Collections.Generic.Dictionary<ConversionFormat, ConversionEncoder> _encoderSelections = new();
    private string? _encoderNoteKey;
    private object?[] _encoderNoteArguments = [];
    private static LocalizationManager L => LocalizationManager.Active;
    private const string CompatibleSearch = "Compatible encoders";
    private const string HardwareSearch = "Hardware encoders";
    private static readonly System.Collections.Generic.Dictionary<string, string[]> StandardVideoEncoders = new(StringComparer.Ordinal)
    {
        ["matroska"] = ["libx264", "h264_mf", "libx265", "libsvtav1", "libvpx-vp9", "mpeg4"],
        ["mp4"] = ["libx264", "h264_mf", "libx265", "libsvtav1", "mpeg4"],
        ["mov"] = ["libx264", "h264_mf", "libx265", "mpeg4", "prores_ks", "mjpeg"],
        ["webm"] = ["libvpx-vp9", "libvpx", "libsvtav1"],
        ["avi"] = ["mpeg4", "mjpeg", "libx264", "h264_mf"],
        ["mpegts"] = ["libx264", "h264_mf", "libx265", "mpeg2video"],
        ["mpeg"] = ["mpeg1video", "mpeg2video"],
        ["mxf"] = ["mpeg2video", "libx264", "prores_ks", "dnxhd"],
        ["flv"] = ["libx264", "flv1"],
        ["asf"] = ["msmpeg4", "wmv1", "wmv2"],
        ["ogv"] = ["libtheora", "libvpx"],
        ["3gp"] = ["libx264", "mpeg4", "h263"],
        ["3g2"] = ["libx264", "mpeg4", "h263"],
        ["nut"] = ["libx264", "libx265", "mpeg4", "ffv1", "rawvideo"]
    };
    internal bool AppliedCopy { get; private set; }
    internal ConversionFormat? AppliedCopyFormat { get; private set; }

    public ConversionWindow()
    {
        InitializeComponent();
        CrfBox.ItemsSource = new[] { "18", "20", "23", "28", "32" };
        VideoBitrateBox.ItemsSource = new[] { "1000", "2500", "5000", "8000", "12000", "20000" };
        WidthBox.ItemsSource = new[] { "640", "1280", "1920", "2560", "3840" };
        HeightBox.ItemsSource = new[] { "360", "720", "1080", "1440", "2160" };
        FrameRateBox.ItemsSource = new[] { "24", "25", "30", "50", "60" };
        AudioBitrateBox.ItemsSource = new[] { "96", "128", "192", "256", "320", "640" };
        QualityBox.ItemsSource = new[] { "0", "2", "4", "6", "8", "10" };
        SampleRateBox.ItemsSource = new[] { "22050", "44100", "48000", "96000", "192000" };
        ChannelsBox.ItemsSource = new[] { "1", "2", "6", "8" };
        L.PropertyChanged += Localization_PropertyChanged;
        Closed += (_, _) =>
        {
            _closed.Cancel();
            L.PropertyChanged -= Localization_PropertyChanged;
        };
    }

    internal ConversionWindow(FfmpegConversionCatalog catalog, DiagnosticLog diagnostics, MediaStreamInfo stream,
        ConversionSettings? existing, CancellationToken shutdown, ConversionFormat? initialFormat = null,
        TrackExportPlan? copyPlan = null, string? sourcePath = null, bool copyRequested = false) : this()
    {
        _catalog = catalog;
        _diagnostics = diagnostics;
        _stream = stream;
        _existing = existing;
        _shutdown = shutdown;
        _kind = stream.VisualKind switch
        {
            VisualKind.Video => "video", VisualKind.Image => "image",
            VisualKind.Animation => "animation", _ => stream.CodecType ?? ""
        };
        RefreshHeading();
        Width = _kind is "subtitle" or "image" ? 570 : 620;
        VideoOptions.IsVisible = _kind is "video" or "animation";
        AudioOptions.IsVisible = _kind == "audio";
        HardwareButton.IsVisible = _kind == "video";
        Grid.SetColumnSpan(MoreEncodersButton, _kind == "video" ? 1 : 2);
        _copyPlan = copyPlan;
        _sourcePath = sourcePath;
        if (copyPlan is not null)
            _copyFormat = new(copyPlan.Muxer, copyPlan.Extension, L.Get("MainOriginalShort"),
                _kind is "video" or "image" or "animation" ? stream.Codec : null,
                _kind == "audio" ? stream.Codec : null,
                _kind == "subtitle" ? stream.Codec : null, Original: true);
        if (_copyFormat is not null)
            _sourceEncoder = catalog.EncodersFor(_kind).Where(IsSourceCodec)
                .Where(e => _kind != "video" || StandardVideoEncoders.TryGetValue(copyPlan!.Muxer, out var names)
                    && names.Contains(e.Name))
                .OrderBy(e => e.Name == stream.Codec ? 0 : 1)
                .ThenBy(e => e.IsHardware).ThenBy(e => e.Name.StartsWith("lib", StringComparison.Ordinal) ? 0 : 1)
                .ThenBy(e => e.Name, StringComparer.Ordinal).FirstOrDefault();
        FormatBox.ItemsSource = _copyFormat is null ? catalog.FormatsFor(_kind) :
            new[] { _copyFormat }.Concat(catalog.FormatsFor(_kind).Where(f =>
                f.Muxer != copyPlan!.Muxer || f.Extension != copyPlan.Extension)).ToArray();
        if (existing is not null)
        {
            CrfBox.Text = existing.Crf?.ToString(CultureInfo.InvariantCulture);
            VideoBitrateBox.Text = existing.BitRateKbps?.ToString(CultureInfo.InvariantCulture);
            AudioBitrateBox.Text = existing.BitRateKbps?.ToString(CultureInfo.InvariantCulture);
            WidthBox.Text = existing.Width?.ToString(CultureInfo.InvariantCulture);
            HeightBox.Text = existing.Height?.ToString(CultureInfo.InvariantCulture);
            FrameRateBox.Text = existing.FrameRate?.ToString(CultureInfo.InvariantCulture);
            SampleRateBox.Text = existing.SampleRate?.ToString(CultureInfo.InvariantCulture);
            ChannelsBox.Text = existing.Channels?.ToString(CultureInfo.InvariantCulture);
            QualityBox.Text = existing.Quality?.ToString(CultureInfo.InvariantCulture);
        }
        var requestedFormat = existing?.Format ?? initialFormat;
        FormatBox.SelectedItem = requestedFormat is { } requested && _copyFormat is { } original
            && requested.Muxer == original.Muxer && requested.Extension == original.Extension
                ? original : requestedFormat ?? _copyFormat;
        if (existing is not null && EncoderBox.ItemsSource is System.Collections.IEnumerable items
            && items.Cast<ConversionEncoder>().Contains(existing.Encoder))
            EncoderBox.SelectedItem = existing.Encoder;
        if (copyRequested && EncoderBox.ItemsSource is System.Collections.IEnumerable copyItems)
            EncoderBox.SelectedItem = copyItems.Cast<ConversionEncoder>().FirstOrDefault(e => e.Name == "copy");
        ForceReencodeBox.IsChecked = existing?.ForceReencode == true;
    }

    private void RefreshHeading()
    {
        if (_stream is null) return;
        StreamHeading.Text = L.Get(_kind switch
        {
            "video" => "ConversionHeadingVideo",
            "image" => "ConversionHeadingImage",
            "animation" => "ConversionHeadingAnimation",
            "audio" => "ConversionHeadingAudio",
            _ => "ConversionHeadingSubtitles"
        });
        VisualOutputHint.IsVisible = _kind is "image" or "animation";
        VisualOutputHint.Text = _kind == "image" ? L.Get("ConversionImageOutputsHint")
            : _kind == "animation" ? L.Get("ConversionAnimationOutputsHint") : "";
    }

    private void SetEncoderNote(string key, params object?[] arguments)
    {
        _encoderNoteKey = key;
        _encoderNoteArguments = arguments;
        EncoderNote.Text = L.Format(key, arguments);
    }

    private void SetValidationMessage(string key, params object?[] arguments)
    {
        SetEncoderNote(key, arguments);
    }

    private void SetValidationText(string message)
    {
        _encoderNoteKey = null;
        EncoderNote.Text = message;
    }

    private void Localization_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != "Item[]") return;
        RefreshHeading();
        if (_encoderNoteKey is not null) EncoderNote.Text = L.Format(_encoderNoteKey, _encoderNoteArguments);
        if (_copyFormat is null || FormatBox.ItemsSource is not System.Collections.IEnumerable source) return;
        var format = FormatBox.SelectedItem;
        var encoder = EncoderBox.SelectedItem;
        var forceReencode = ForceReencodeBox.IsChecked;
        var oldCopy = _copyFormat;
        _copyFormat = oldCopy with { Description = L.Get("MainOriginalShort") };
        if (_encoderSelections.Remove(oldCopy, out var selectedEncoder))
            _encoderSelections[_copyFormat] = selectedEncoder;
        if (ReferenceEquals(_activeFormat, oldCopy)) _activeFormat = _copyFormat;
        FormatBox.ItemsSource = new[] { _copyFormat }.Concat(source.Cast<ConversionFormat>().Skip(1)).ToArray();
        FormatBox.SelectedItem = ReferenceEquals(format, oldCopy) ? _copyFormat : format;
        if (encoder is not null && EncoderBox.ItemsSource is System.Collections.IEnumerable items
            && items.Cast<ConversionEncoder>().Contains(encoder)) EncoderBox.SelectedItem = encoder;
        ForceReencodeBox.IsChecked = forceReencode;
    }

    private ConversionFormat? EffectiveFormat(ConversionFormat format) =>
        ReferenceEquals(format, _copyFormat)
            ? _catalog?.FindFormat(format.Muxer, format.Extension, _kind) : format;

    private ConversionEncoder CopyEncoder()
    {
        var codec = _copyFormat?.Muxer == "lrc" && _stream?.Codec == "text"
            ? L.Get("ConversionOriginalTextStream")
            : _stream?.ImageFormat == "WebP" ? "WebP" : _stream?.Codec?.ToUpperInvariant() ?? "?";
        if (_stream?.Profile is { Length: > 0 } profile && _stream.ImageFormat != "WebP")
            codec += " " + profile;
        return new("copy", _stream?.Codec ?? "copy",
            _kind is "image" or "animation" ? "video" : _kind,
            L.Format("ConversionOriginalCopyCodec", codec));
    }

    private bool IsSourceCodec(ConversionEncoder encoder) =>
        string.Equals(encoder.Codec, _stream?.Codec, StringComparison.OrdinalIgnoreCase)
        || _stream?.Codec == "ssa" && encoder.Codec == "ass"
        || _stream?.Codec == "srt" && encoder.Codec == "subrip"
        || _stream?.ImageFormat == "WebP" && encoder.Codec == "webp";

    private bool HasRequestedParameters() => new[] { CrfBox, VideoBitrateBox, AudioBitrateBox,
        WidthBox, HeightBox, FrameRateBox, QualityBox, SampleRateBox, ChannelsBox }
        .Any(box => box.IsEnabled && !string.IsNullOrWhiteSpace(box.Text));

    private void RefreshEncoderMode()
    {
        var source = ReferenceEquals(FormatBox.SelectedItem, _copyFormat);
        var encoder = EncoderBox.SelectedItem as ConversionEncoder;
        var copying = encoder?.Name == "copy";
        VideoOptions.IsEnabled = !copying;
        AudioOptions.IsEnabled = !copying;
        ForceReencodeBox.IsVisible = encoder is not null && encoder.Name != "copy"
            && (_kind == "video" && IsSourceCodec(encoder)
                || source && encoder == _sourceEncoder);
        if (!ForceReencodeBox.IsVisible) ForceReencodeBox.IsChecked = false;
    }

    private void Format_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_catalog is null || FormatBox.SelectedItem is not ConversionFormat format) return;
        if (_activeFormat is { } previous && EncoderBox.SelectedItem is ConversionEncoder chosen
            && chosen.Name != "copy") _encoderSelections[previous] = chosen;
        _activeFormat = format;
        var source = ReferenceEquals(format, _copyFormat);
        var target = EffectiveFormat(format);
        EncoderBox.IsEnabled = true;
        MoreEncodersButton.IsEnabled = target is not null && !(source && _kind == "subtitle");
        HardwareButton.IsEnabled = target is not null;
        EncoderBox.ItemsSource = AvailableEncoders(format, target);
        var available = ((ConversionEncoder[])EncoderBox.ItemsSource).ToArray();
        var suggested = target is null ? null : _catalog.SuggestedEncoder(target, _kind);
        EncoderBox.SelectedItem = _encoderSelections.TryGetValue(format, out var selected)
            && available.Contains(selected) ? selected
            : _existing is { } existing && existing.Format == target
                && available.Contains(existing.Encoder) ? existing.Encoder
            : source ? available[0] : _kind == "video" ? available.FirstOrDefault()
            : suggested is not null && available.Contains(suggested)
                ? suggested : available.FirstOrDefault();
        RefreshEncoderMode();
        SetEncoderNote(EncoderBox.SelectedItem is ConversionEncoder selectedEncoder
            && (selectedEncoder.Name == "copy" || source && selectedEncoder == _sourceEncoder)
            ? "ConversionCopyNote" : "ConversionOtherEncodersHint");
    }

    internal static ConversionEncoder[] InitialEncoders(FfmpegConversionCatalog catalog,
        ConversionFormat format, string kind, ConversionSettings? existing)
    {
        if (kind == "video" && StandardVideoEncoders.TryGetValue(format.Muxer, out var standard))
        {
            var installed = catalog.EncodersFor(kind);
            var defaults = standard.Select(name => installed.FirstOrDefault(e => e.Name == name))
                .OfType<ConversionEncoder>();
            var savedVideo = existing?.Format == format && catalog.Contains(existing)
                && FfmpegConversionCatalog.IsEncoderSuitable(format, existing.Encoder)
                    ? existing.Encoder : null;
            return defaults.Concat(catalog.ConfirmedAdditionalFor(format, kind))
                .Concat(catalog.ConfirmedHardwareFor(format))
                .Concat(savedVideo is null ? [] : [savedVideo]).Distinct().ToArray();
        }
        var recommended = catalog.SuggestedEncoder(format, kind);
        var primary = catalog.EncodersFor(kind)
            .Where(e => !e.IsHardware && e.Codec == format.DefaultCodec(kind))
            .OrderBy(e => e.Name, StringComparer.Ordinal).ToArray();
        var cafPcm = kind == "audio" && format.Muxer == "caf"
            ? catalog.EncodersFor(kind).Where(e => e.Name == "pcm_s16be").ToArray()
            : [];
        var saved = existing?.Format == format && catalog.Contains(existing)
            && FfmpegConversionCatalog.IsEncoderSuitable(format, existing.Encoder)
            && (kind != "subtitle" || catalog.EncodersFor(kind).Contains(existing.Encoder))
                ? existing.Encoder : null;
        var additional = catalog.ConfirmedAdditionalFor(format, kind)
            .Where(e => kind != "subtitle" || catalog.EncodersFor(kind).Contains(e))
            .Concat(saved is { IsHardware: false } ? [saved] : [])
            .OrderBy(e => e.Name, StringComparer.Ordinal);
        System.Collections.Generic.IEnumerable<ConversionEncoder> hardware =
            (kind == "video" ? catalog.ConfirmedHardwareFor(format) : [])
            .Concat(saved is { IsHardware: true } ? [saved] : [])
            .OrderBy(e => e.Name, StringComparer.Ordinal);
        return (recommended is null ? [] : new[] { recommended })
            .Concat(cafPcm).Concat(primary).Concat(additional).Concat(hardware).Distinct().ToArray();
    }

    private ConversionEncoder[] AvailableEncoders(ConversionFormat selected, ConversionFormat? target)
    {
        var listed = target is null || _catalog is null ? []
            : InitialEncoders(_catalog, target, _kind, _existing);
        if (_kind == "video" && target is not null)
            listed = PrioritizeVideoEncoders(listed, target, _stream?.Codec);
        if (ReferenceEquals(selected, _copyFormat))
        {
            var original = _sourceEncoder ?? CopyEncoder();
            return _kind == "subtitle" ? [original] : [original, .. listed.Where(e => e != original)];
        }
        return _kind == "audio" && _sourcePath is not null
            ? [.. listed, CopyEncoder()] : listed;
    }

    internal static ConversionEncoder[] PrioritizeVideoEncoders(ConversionEncoder[] listed,
        ConversionFormat format, string? sourceCodec)
    {
        if (sourceCodec is null || !StandardVideoEncoders.TryGetValue(format.Muxer, out var standard)
            || !listed.Any(e => standard.Contains(e.Name)
                && string.Equals(e.Codec, sourceCodec, StringComparison.OrdinalIgnoreCase)))
            return listed;
        return listed.Where(e => standard.Contains(e.Name)
                && string.Equals(e.Codec, sourceCodec, StringComparison.OrdinalIgnoreCase))
            .Concat(listed.Where(e => !standard.Contains(e.Name)
                || !string.Equals(e.Codec, sourceCodec, StringComparison.OrdinalIgnoreCase)))
            .Distinct().ToArray();
    }

    private void EncoderLabel_Loaded(object? sender, RoutedEventArgs e)
    {
        if (sender is TextBlock { DataContext: ConversionEncoder encoder } label)
            label.Text = encoder == _sourceEncoder && ReferenceEquals(FormatBox.SelectedItem, _copyFormat)
                ? L.Format("ConversionSourceEncoder", encoder.Display) : encoder.Display;
    }

    private void HardwareDivider_Loaded(object? sender, RoutedEventArgs e)
    {
        if (sender is not Border divider || divider.DataContext is not ConversionEncoder encoder
            || !encoder.IsHardware || divider.FindAncestorOfType<ComboBoxItem>() is null
            || EncoderBox.ItemsSource is not System.Collections.IEnumerable items) return;
        divider.IsVisible = items.Cast<ConversionEncoder>().FirstOrDefault(item => item.IsHardware) == encoder;
    }

    private void FormatDivider_Loaded(object? sender, RoutedEventArgs e)
    {
        if (sender is not Border divider || divider.DataContext is not ConversionFormat format
            || divider.FindAncestorOfType<ComboBoxItem>() is null
            || FormatBox.ItemsSource is not System.Collections.IEnumerable source) return;
        var formats = source.Cast<ConversionFormat>().Where(f => !ReferenceEquals(f, _copyFormat)).ToArray();
        var secondary = _kind is "video" or "animation"
            && FfmpegConversionCatalog.Category(format) != _kind;
        divider.IsVisible = secondary && formats.FirstOrDefault(f =>
            FfmpegConversionCatalog.Category(f) != _kind) == format;
    }

    private async void MoreEncoders_Click(object? sender, RoutedEventArgs e)
    {
        if (_catalog is null || FormatBox.SelectedItem is not ConversionFormat selected
            || EffectiveFormat(selected) is not { } format) return;
        MoreEncodersButton.IsEnabled = false;
        SetEncoderNote("ConversionCheckingAdditional");
        RecordSearch(CompatibleSearch, "Compatible encoder search started.",
            $"format {format.Muxer}; type {_kind}", userMessageKey: "LogCompatibleSearchStarted");
        try
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(_shutdown, _closed.Token);
            var working = new System.Collections.Generic.List<ConversionEncoder>();
            foreach (var encoder in _catalog.EncodersFor(_kind).Where(e => !e.IsHardware))
            {
                linked.Token.ThrowIfCancellationRequested();
                var result = await _catalog.CheckCombinationAsync(format, encoder, linked.Token,
                    diagnostics: details => _diagnostics?.Record(DiagnosticLevel.Info,
                        CompatibleSearch, "FFmpeg response during encoder check.", details));
                if (result.Compatible) working.Add(encoder);
                _diagnostics?.Record(DiagnosticLevel.Info, CompatibleSearch,
                    $"{encoder.Name}: check {(result.Compatible ? "passed" : "failed")}.",
                    result.Compatible ? null : result.InfrastructureFailure
                        ? "Encoder verification could not run." : "Encoder/container combination was rejected.");
            }
            _catalog.RememberConfirmedAdditional(format, _kind, working);
            if (!ReferenceEquals(FormatBox.SelectedItem, selected))
            {
                RecordSearch(CompatibleSearch, "Search result not applied: output format changed.",
                    level: DiagnosticLevel.Warn, userMessageKey: "LogSearchStale");
                return;
            }
            var current = EncoderBox.SelectedItem;
            EncoderBox.ItemsSource = AvailableEncoders(selected, format);
            EncoderBox.SelectedItem = current;
            RefreshEncoderMode();
            SetEncoderNote("ConversionConfirmedCompatibleCount", working.Count);
            RecordSearch(CompatibleSearch, working.Count == 0
                ? "No compatible encoders found."
                : $"Found {working.Count} compatible encoders.",
                userMessageKey: working.Count == 0 ? "LogCompatibleNotFound" : "LogCompatibleFoundCount",
                userMessageArgs: working.Count == 0 ? null : [working.Count]);
        }
        catch (OperationCanceledException)
        {
            SetEncoderNote("ConversionAdditionalCancelled");
            RecordSearch(CompatibleSearch, "Compatible encoder search cancelled.", level: DiagnosticLevel.Warn,
                userMessageKey: "LogCompatibleSearchCancelled");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or Win32Exception
            or TimeoutException or InvalidDataException)
        {
            SetEncoderNote("ConversionAdditionalFailed");
            RecordSearch(CompatibleSearch, "Compatible encoder search failed.",
                ex.Message, DiagnosticLevel.Error, "LogCompatibleSearchFailed");
        }
        finally
        {
            MoreEncodersButton.IsEnabled = FormatBox.SelectedItem is ConversionFormat current
                && EffectiveFormat(current) is not null
                && !(ReferenceEquals(current, _copyFormat) && _kind == "subtitle");
        }
    }

    private void RecordSearch(string operation, string message, string? details = null,
        DiagnosticLevel level = DiagnosticLevel.Info, string? userMessageKey = null,
        object?[]? userMessageArgs = null) =>
        _diagnostics?.Record(level, operation, "Search: " + message, details,
            userMessageKey, userMessageArgs);

    private async void Encoder_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_catalog is null || EncoderBox.SelectedItem is not ConversionEncoder encoder) return;
        RefreshEncoderMode();
        if (encoder.Name == "copy" || ReferenceEquals(FormatBox.SelectedItem, _copyFormat)
            && encoder == _sourceEncoder)
        { SetEncoderNote("ConversionCopyNote"); CrfBox.IsEnabled = false; return; }
        SetEncoderNote("ConversionOtherEncodersHint");
        if (_kind is not ("video" or "animation")) return;
        var revision = ++_encoderRevision;
        CrfBox.IsEnabled = false;
        try
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(_shutdown, _closed.Token);
            var supports = await _catalog.SupportsCrfAsync(encoder, linked.Token);
            if (revision == _encoderRevision && !_closed.IsCancellationRequested)
            {
                CrfBox.IsEnabled = supports;
                if (!supports) CrfBox.Text = "";
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException or UnauthorizedAccessException
            or Win32Exception or TimeoutException or InvalidDataException) { }
    }

    private async void Hardware_Click(object? sender, RoutedEventArgs e)
    {
        if (_catalog is null || FormatBox.SelectedItem is not ConversionFormat selected
            || EffectiveFormat(selected) is not { } format) return;
        HardwareButton.IsEnabled = false;
        SetEncoderNote("ConversionCheckingHardware");
        RecordSearch(HardwareSearch, "Hardware encoder search started.", $"format {format.Muxer}",
            userMessageKey: "LogHardwareSearchStarted");
        try
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(_shutdown, _closed.Token);
            var working = new System.Collections.Generic.List<ConversionEncoder>();
            var searchFailed = false;
            foreach (var encoder in _catalog.EncodersFor("video").Where(e => e.IsHardware))
            {
                linked.Token.ThrowIfCancellationRequested();
                var result = await _catalog.CheckCombinationAsync(format, encoder, linked.Token,
                    diagnostics: details => _diagnostics?.Record(DiagnosticLevel.Info,
                        HardwareSearch, "FFmpeg response during encoder check.", details));
                if (result.Compatible)
                {
                    working.Add(encoder);
                    _diagnostics?.Record(DiagnosticLevel.Info, HardwareSearch,
                        $"{encoder.Name}: check passed.");
                }
                else
                {
                    searchFailed |= result.InfrastructureFailure;
                    _diagnostics?.Record(DiagnosticLevel.Info, HardwareSearch,
                        $"{encoder.Name}: check failed.", result.InfrastructureFailure
                            ? "Encoder verification could not run." : "Encoder/container combination was rejected.");
                }
            }
            _catalog.RememberConfirmedHardware(format, working);
            if (!ReferenceEquals(FormatBox.SelectedItem, selected))
            {
                RecordSearch(HardwareSearch, "Search result not applied: output format changed.",
                    level: DiagnosticLevel.Warn, userMessageKey: "LogSearchStale");
                return;
            }
            var current = EncoderBox.SelectedItem;
            EncoderBox.ItemsSource = AvailableEncoders(selected, format);
            EncoderBox.SelectedItem = current;
            RefreshEncoderMode();
            if (searchFailed) SetEncoderNote("ConversionHardwareSearchFailedDetails");
            else if (working.Count > 0) SetEncoderNote("ConversionHardwareFoundCount", working.Count);
            else SetEncoderNote("ConversionHardwareNotFound");
            RecordSearch(HardwareSearch, searchFailed
                ? "Hardware encoder search failed."
                : working.Count > 0 ? $"Found {working.Count} available hardware encoders."
                : "No available hardware encoders found.",
                level: searchFailed ? DiagnosticLevel.Error : DiagnosticLevel.Info,
                userMessageKey: searchFailed ? "LogHardwareSearchFailed" : working.Count > 0
                    ? "ConversionHardwareFoundCount" : "ConversionHardwareNotFound",
                userMessageArgs: searchFailed || working.Count == 0 ? null : [working.Count]);
        }
        catch (OperationCanceledException)
        {
            SetEncoderNote("ConversionHardwareCancelled");
            RecordSearch(HardwareSearch, "Hardware encoder search cancelled.", level: DiagnosticLevel.Warn,
                userMessageKey: "LogHardwareSearchCancelled");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or Win32Exception
            or TimeoutException or InvalidDataException)
        {
            RecordSearch(HardwareSearch, "Hardware encoder search failed.",
                ex.Message, DiagnosticLevel.Error, "LogHardwareSearchFailed");
            SetEncoderNote("ConversionHardwareFailed");
        }
        finally { HardwareButton.IsEnabled = true; }
    }

    private async void Save_Click(object? sender, RoutedEventArgs e)
    {
        var source = ReferenceEquals(FormatBox.SelectedItem, _copyFormat);
        var selectedEncoder = EncoderBox.SelectedItem as ConversionEncoder;
        if (source && _copyPlan is not null && selectedEncoder is not null
            && (selectedEncoder.Name == "copy" || selectedEncoder == _sourceEncoder
                && !HasRequestedParameters() && ForceReencodeBox.IsChecked != true))
        {
            AppliedCopy = true;
            Close(null);
            return;
        }
        if (!source && _kind == "audio" && FormatBox.SelectedItem is ConversionFormat copyFormat
            && selectedEncoder?.Name == "copy" && _catalog is not null && _stream?.Index is { } copyIndex
            && _sourcePath is not null)
        {
            SaveButton.IsEnabled = false;
            try
            {
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(_shutdown, _closed.Token);
                if (!await _catalog.CheckCopyAsync(_sourcePath, copyIndex, copyFormat, linked.Token))
                { SetValidationMessage("ConversionCopyIncompatible"); return; }
                AppliedCopyFormat = copyFormat;
                Close(null);
            }
            catch (OperationCanceledException) { SetValidationMessage("ConversionCheckCancelled"); }
            finally { SaveButton.IsEnabled = true; }
            return;
        }
        if (_catalog is null || _stream is null || FormatBox.SelectedItem is not ConversionFormat selected
            || EffectiveFormat(selected) is not { } format
            || selectedEncoder is not { } encoder || encoder.Name == "copy")
        { SetValidationMessage("ConversionSelectFormatAndEncoder"); return; }
        try
        {
            int? Number(ComboBox box) => string.IsNullOrWhiteSpace(box.Text) ? null
                : int.Parse(box.Text, NumberStyles.None, CultureInfo.InvariantCulture);
            double? Decimal(ComboBox box) => string.IsNullOrWhiteSpace(box.Text) ? null
                : double.Parse(box.Text, NumberStyles.Float, CultureInfo.CurrentCulture);
            var options = new ConversionSettings(format, encoder,
                _kind is "video" or "animation" && CrfBox.IsEnabled ? Number(CrfBox) : null,
                _kind is "video" or "animation" ? Number(VideoBitrateBox) : _kind == "audio" ? Number(AudioBitrateBox) : null,
                _kind is "video" or "animation" ? Number(WidthBox) : null,
                _kind is "video" or "animation" ? Number(HeightBox) : null,
                _kind is "video" or "animation" ? Decimal(FrameRateBox) : null,
                _kind == "audio" ? Number(SampleRateBox) : null,
                _kind == "audio" ? Number(ChannelsBox) : null,
                _kind == "audio" ? Number(QualityBox) : null,
                ForceReencodeBox.IsChecked == true);
            if (options.Validate(_stream) is { } error)
            { SetValidationText(error); return; }
            SaveButton.IsEnabled = false;
            SetValidationMessage("ConversionCheckingCompatibility");
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(_shutdown, _closed.Token);
            var check = await _catalog.CheckCombinationAsync(format, encoder, linked.Token, options);
            if (!check.Compatible) { SetValidationText(check.Reason); return; }
            Close(options);
        }
        catch (FormatException) { SetValidationMessage("ConversionNumbersOrEmpty"); }
        catch (OverflowException) { SetValidationMessage("ConversionNumberTooLarge"); }
        catch (OperationCanceledException) { SetValidationMessage("ConversionCheckCancelled"); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or Win32Exception
            or TimeoutException or InvalidDataException)
        { SetValidationMessage("ConversionCheckParametersFailed"); }
        finally { SaveButton.IsEnabled = true; }
    }

    private void Cancel_Click(object? sender, RoutedEventArgs e) => Close(null);
}
