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
    private string? _encoderNoteKey;
    private object?[] _encoderNoteArguments = [];
    private string? _validationKey;
    private object?[] _validationArguments = [];
    private static LocalizationManager L => LocalizationManager.Active;
    private const string CompatibleSearch = "Compatible encoders";
    private const string HardwareSearch = "Hardware encoders";
    internal bool AppliedCopy { get; private set; }

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
        TrackExportPlan? copyPlan = null) : this()
    {
        _catalog = catalog;
        _diagnostics = diagnostics;
        _stream = stream;
        _existing = existing;
        _shutdown = shutdown;
        _kind = stream.VisualKind == VisualKind.Video ? "video" : stream.CodecType ?? "";
        RefreshHeading();
        Width = _kind == "subtitle" ? 570 : 620;
        VideoOptions.IsVisible = _kind == "video";
        AudioOptions.IsVisible = _kind == "audio";
        HardwareButton.IsVisible = _kind == "video";
        Grid.SetColumnSpan(MoreEncodersButton, _kind == "video" ? 1 : 2);
        _copyPlan = copyPlan;
        if (copyPlan is not null)
            _copyFormat = new(copyPlan.Muxer, copyPlan.Extension, L.Get("MainOriginalNoTranscode"),
                _kind == "video" ? stream.Codec : null, _kind == "audio" ? stream.Codec : null,
                _kind == "subtitle" ? stream.Codec : null);
        FormatBox.ItemsSource = _copyFormat is null ? catalog.FormatsFor(_kind) :
            new[] { _copyFormat }.Concat(catalog.FormatsFor(_kind)).ToArray();
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
        FormatBox.SelectedItem = existing?.Format ?? initialFormat ?? _copyFormat;
        if (!ReferenceEquals(FormatBox.SelectedItem, _copyFormat))
            EncoderBox.SelectedItem = existing?.Encoder ??
                (FormatBox.SelectedItem is ConversionFormat format ? catalog.SuggestedEncoder(format, _kind) : null);
    }

    private void RefreshHeading()
    {
        if (_stream is null) return;
        StreamHeading.Text = L.Get(_kind switch
        {
            "video" => "ConversionHeadingVideo",
            "audio" => "ConversionHeadingAudio",
            _ => "ConversionHeadingSubtitles"
        });
    }

    private void SetEncoderNote(string key, params object?[] arguments)
    {
        _encoderNoteKey = key;
        _encoderNoteArguments = arguments;
        EncoderNote.Text = L.Format(key, arguments);
    }

    private void SetValidationMessage(string key, params object?[] arguments)
    {
        _validationKey = key;
        _validationArguments = arguments;
        ValidationMessage.Text = L.Format(key, arguments);
    }

    private void Localization_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != "Item[]") return;
        RefreshHeading();
        if (_encoderNoteKey is not null) EncoderNote.Text = L.Format(_encoderNoteKey, _encoderNoteArguments);
        if (_validationKey is not null) ValidationMessage.Text = L.Format(_validationKey, _validationArguments);
        if (_copyFormat is null || FormatBox.ItemsSource is not System.Collections.IEnumerable source) return;
        var format = FormatBox.SelectedItem;
        var encoder = EncoderBox.SelectedItem;
        var oldCopy = _copyFormat;
        _copyFormat = oldCopy with { Description = L.Get("MainOriginalNoTranscode") };
        FormatBox.ItemsSource = new[] { _copyFormat }.Concat(source.Cast<ConversionFormat>().Skip(1)).ToArray();
        FormatBox.SelectedItem = ReferenceEquals(format, oldCopy) ? _copyFormat : format;
        if (!ReferenceEquals(format, oldCopy) && encoder is not null) EncoderBox.SelectedItem = encoder;
    }

    private void Format_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_catalog is null || FormatBox.SelectedItem is not ConversionFormat format) return;
        var copying = ReferenceEquals(format, _copyFormat);
        EncoderBox.IsEnabled = !copying;
        MoreEncodersButton.IsEnabled = !copying;
        HardwareButton.IsEnabled = !copying;
        VideoOptions.IsEnabled = !copying;
        AudioOptions.IsEnabled = !copying;
        if (copying)
        {
            EncoderBox.ItemsSource = new[] { new ConversionEncoder("copy", _stream?.Codec ?? "copy", _kind,
                L.Get("ConversionCopyEncoderDescription")) };
            EncoderBox.SelectedIndex = 0;
            SetEncoderNote("ConversionCopyNote");
            return;
        }
        EncoderBox.ItemsSource = InitialEncoders(_catalog, format, _kind, _existing);
        EncoderBox.SelectedItem = _catalog.SuggestedEncoder(format, _kind);
        SetEncoderNote("ConversionOtherEncodersHint");
    }

    internal static ConversionEncoder[] InitialEncoders(FfmpegConversionCatalog catalog,
        ConversionFormat format, string kind, ConversionSettings? existing)
    {
        var encoders = catalog.EncodersFor(kind)
            .Where(e => !e.IsHardware && e.Codec == format.DefaultCodec(kind)).ToList();
        if (existing?.Format == format && !encoders.Contains(existing.Encoder))
            encoders.Add(existing.Encoder);
        encoders.AddRange(catalog.ConfirmedAdditionalFor(format, kind).Where(e => !encoders.Contains(e)));
        if (kind == "video")
            encoders.AddRange(catalog.ConfirmedHardwareFor(format).Where(e => !encoders.Contains(e)));
        return encoders.ToArray();
    }

    private void HardwareDivider_Loaded(object? sender, RoutedEventArgs e)
    {
        if (sender is not Border divider || divider.DataContext is not ConversionEncoder encoder
            || !encoder.IsHardware || divider.FindAncestorOfType<ComboBoxItem>() is null
            || EncoderBox.ItemsSource is not System.Collections.IEnumerable items) return;
        divider.IsVisible = items.Cast<ConversionEncoder>().FirstOrDefault(item => item.IsHardware) == encoder;
    }

    private async void MoreEncoders_Click(object? sender, RoutedEventArgs e)
    {
        if (_catalog is null || FormatBox.SelectedItem is not ConversionFormat format) return;
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
            if (FormatBox.SelectedItem is not ConversionFormat selectedFormat || selectedFormat != format)
            {
                RecordSearch(CompatibleSearch, "Search result not applied: output format changed.",
                    level: DiagnosticLevel.Warn, userMessageKey: "LogSearchStale");
                return;
            }
            var current = EncoderBox.SelectedItem;
            var hardware = EncoderBox.ItemsSource is System.Collections.IEnumerable items
                ? items.Cast<ConversionEncoder>().Where(e => e.IsHardware)
                : Enumerable.Empty<ConversionEncoder>();
            EncoderBox.ItemsSource = working.Concat(hardware).Distinct().ToArray();
            EncoderBox.SelectedItem = current;
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
        finally { MoreEncodersButton.IsEnabled = true; }
    }

    private void RecordSearch(string operation, string message, string? details = null,
        DiagnosticLevel level = DiagnosticLevel.Info, string? userMessageKey = null,
        object?[]? userMessageArgs = null) =>
        _diagnostics?.Record(level, operation, "Search: " + message, details,
            userMessageKey, userMessageArgs);

    private async void Encoder_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_catalog is null || _kind != "video" || EncoderBox.SelectedItem is not ConversionEncoder encoder) return;
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
        if (_catalog is null || FormatBox.SelectedItem is not ConversionFormat format) return;
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
            if (!searchFailed) _catalog.RememberConfirmedHardware(format, working);
            if (FormatBox.SelectedItem is not ConversionFormat selectedFormat || selectedFormat != format)
            {
                RecordSearch(HardwareSearch, "Search result not applied: output format changed.",
                    level: DiagnosticLevel.Warn, userMessageKey: "LogSearchStale");
                return;
            }
            var current = EncoderBox.SelectedItem;
            var software = EncoderBox.ItemsSource is System.Collections.IEnumerable items
                ? items.Cast<ConversionEncoder>().Where(e => !e.IsHardware)
                : Enumerable.Empty<ConversionEncoder>();
            EncoderBox.ItemsSource = software.Concat(working).Distinct().ToArray();
            EncoderBox.SelectedItem = current;
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
        if (ReferenceEquals(FormatBox.SelectedItem, _copyFormat) && _copyPlan is not null)
        {
            AppliedCopy = true;
            Close(null);
            return;
        }
        if (_catalog is null || _stream is null || FormatBox.SelectedItem is not ConversionFormat format
            || EncoderBox.SelectedItem is not ConversionEncoder encoder)
        { SetValidationMessage("ConversionSelectFormatAndEncoder"); return; }
        try
        {
            int? Number(ComboBox box) => string.IsNullOrWhiteSpace(box.Text) ? null
                : int.Parse(box.Text, NumberStyles.None, CultureInfo.InvariantCulture);
            double? Decimal(ComboBox box) => string.IsNullOrWhiteSpace(box.Text) ? null
                : double.Parse(box.Text, NumberStyles.Float, CultureInfo.CurrentCulture);
            var options = new ConversionSettings(format, encoder,
                _kind == "video" && CrfBox.IsEnabled ? Number(CrfBox) : null,
                _kind == "video" ? Number(VideoBitrateBox) : _kind == "audio" ? Number(AudioBitrateBox) : null,
                _kind == "video" ? Number(WidthBox) : null,
                _kind == "video" ? Number(HeightBox) : null,
                _kind == "video" ? Decimal(FrameRateBox) : null,
                _kind == "audio" ? Number(SampleRateBox) : null,
                _kind == "audio" ? Number(ChannelsBox) : null,
                _kind == "audio" ? Number(QualityBox) : null);
            if (options.Validate(_stream) is { } error)
            { _validationKey = null; ValidationMessage.Text = error; return; }
            SaveButton.IsEnabled = false;
            SetValidationMessage("ConversionCheckingCompatibility");
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(_shutdown, _closed.Token);
            var check = await _catalog.CheckCombinationAsync(format, encoder, linked.Token, options);
            if (!check.Compatible) { _validationKey = null; ValidationMessage.Text = check.Reason; return; }
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
