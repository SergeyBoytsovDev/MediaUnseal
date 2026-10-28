using System;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using TrackExtractor.Models;
using TrackExtractor.Services;

namespace TrackExtractor.Views;

public partial class ConversionWindow : Window
{
    private FfmpegConversionCatalog? _catalog;
    private MediaInfo? _media;
    private MediaStreamInfo? _stream;
    private ConversionSettings? _existing;
    private CancellationToken _shutdown;
    private readonly CancellationTokenSource _closed = new();
    private int _encoderRevision;
    private string _kind = "";

    public ConversionWindow()
    {
        InitializeComponent();
        Closed += (_, _) => _closed.Cancel();
    }

    internal ConversionWindow(FfmpegConversionCatalog catalog, MediaInfo media, MediaStreamInfo stream,
        ConversionSettings? existing, CancellationToken shutdown, ConversionFormat? initialFormat = null) : this()
    {
        _catalog = catalog;
        _media = media;
        _stream = stream;
        _existing = existing;
        _shutdown = shutdown;
        _kind = stream.VisualKind == VisualKind.Video ? "video" : stream.CodecType ?? "";
        StreamHeading.Text = $"Поток #{stream.Index}: конвертация {(_kind == "video" ? "видео" : _kind == "audio" ? "аудио" : "субтитров")}";
        VideoOptions.IsVisible = _kind == "video";
        AudioOptions.IsVisible = _kind == "audio";
        FormatBox.ItemsSource = catalog.FormatsFor(_kind);
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
        FormatBox.SelectedItem = existing?.Format ?? initialFormat ?? catalog.FormatsFor(_kind).FirstOrDefault();
        EncoderBox.SelectedItem = existing?.Encoder ??
            (FormatBox.SelectedItem is ConversionFormat format ? catalog.SuggestedEncoder(format, _kind) : null);
        UpdateExample();
    }

    private void Format_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_catalog is null || FormatBox.SelectedItem is not ConversionFormat format) return;
        var defaults = _catalog.EncodersFor(_kind)
            .Where(e => !e.IsHardware && e.Codec == format.DefaultCodec(_kind)).ToList();
        if (_existing?.Format == format && !defaults.Contains(_existing.Encoder))
            defaults.Add(_existing.Encoder);
        EncoderBox.ItemsSource = defaults;
        EncoderBox.SelectedItem = _catalog.SuggestedEncoder(format, _kind);
        EncoderNote.Text = "Другие кодировщики можно проверить для выбранного формата.";
        UpdateExample();
    }

    private async void MoreEncoders_Click(object? sender, RoutedEventArgs e)
    {
        if (_catalog is null || FormatBox.SelectedItem is not ConversionFormat format) return;
        MoreEncodersButton.IsEnabled = false;
        EncoderNote.Text = "FFmpeg проверяет дополнительные кодировщики…";
        try
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(_shutdown, _closed.Token);
            var working = new System.Collections.Generic.List<ConversionEncoder>();
            foreach (var encoder in _catalog.EncodersFor(_kind).Where(e => !e.IsHardware))
            {
                linked.Token.ThrowIfCancellationRequested();
                if (encoder.Codec == format.DefaultCodec(_kind)) { working.Add(encoder); continue; }
                var result = await _catalog.CheckCombinationAsync(format, encoder, linked.Token);
                if (result.Compatible) working.Add(encoder);
            }
            if (FormatBox.SelectedItem is not ConversionFormat selectedFormat || selectedFormat != format) return;
            var current = EncoderBox.SelectedItem;
            EncoderBox.ItemsSource = working;
            EncoderBox.SelectedItem = current;
            EncoderNote.Text = $"Подтверждено совместимых кодировщиков: {working.Count}.";
        }
        catch (OperationCanceledException) { EncoderNote.Text = "Проверка кодировщиков отменена."; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or Win32Exception
            or TimeoutException or InvalidDataException)
        { EncoderNote.Text = "Не удалось проверить дополнительные кодировщики."; }
        finally { MoreEncodersButton.IsEnabled = true; }
    }

    private async void Encoder_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        UpdateExample();
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
        EncoderNote.Text = "Проверка аппаратных кодировщиков…";
        try
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(_shutdown, _closed.Token);
            var working = new System.Collections.Generic.List<ConversionEncoder>();
            foreach (var encoder in _catalog.EncodersFor("video").Where(e => e.IsHardware))
            {
                linked.Token.ThrowIfCancellationRequested();
                var result = await _catalog.CheckCombinationAsync(format, encoder, linked.Token);
                if (result.Compatible) working.Add(encoder);
            }
            if (FormatBox.SelectedItem is not ConversionFormat selectedFormat || selectedFormat != format) return;
            var current = EncoderBox.SelectedItem;
            var software = EncoderBox.ItemsSource is System.Collections.IEnumerable items
                ? items.Cast<ConversionEncoder>().Where(e => !e.IsHardware)
                : Enumerable.Empty<ConversionEncoder>();
            EncoderBox.ItemsSource = software.Concat(working).Distinct().ToArray();
            EncoderBox.SelectedItem = current;
            EncoderNote.Text = working.Count == 0
                ? "Доступные аппаратные кодировщики для этого формата не обнаружены."
                : $"Доступно аппаратных кодировщиков: {working.Count}.";
        }
        catch (OperationCanceledException) { EncoderNote.Text = "Проверка аппаратных кодировщиков отменена."; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or Win32Exception
            or TimeoutException or InvalidDataException)
        { EncoderNote.Text = "Не удалось проверить аппаратные кодировщики."; }
        finally { HardwareButton.IsEnabled = true; }
    }

    private void UpdateExample()
    {
        if (_media is null || _stream is null || FormatBox.SelectedItem is not ConversionFormat format
            || EncoderBox.SelectedItem is not ConversionEncoder encoder) return;
        var plan = new TrackExportPlan(_stream, format.Extension, format.Muxer, false,
            Conversion: new ConversionSettings(format, encoder));
        OutputExample.Text = "Выходной файл: " + TrackExporter.BuildFileName(_media, plan);
    }

    private async void Save_Click(object? sender, RoutedEventArgs e)
    {
        if (_catalog is null || _stream is null || FormatBox.SelectedItem is not ConversionFormat format
            || EncoderBox.SelectedItem is not ConversionEncoder encoder)
        { ValidationMessage.Text = "Выберите выходной формат и кодировщик."; return; }
        try
        {
            int? Number(TextBox box) => string.IsNullOrWhiteSpace(box.Text) ? null
                : int.Parse(box.Text, NumberStyles.None, CultureInfo.InvariantCulture);
            double? Decimal(TextBox box) => string.IsNullOrWhiteSpace(box.Text) ? null
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
            if (options.Validate(_stream) is { } error) { ValidationMessage.Text = error; return; }
            SaveButton.IsEnabled = false;
            ValidationMessage.Text = "Проверяется совместимость кодировщика и выходного формата…";
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(_shutdown, _closed.Token);
            var check = await _catalog.CheckCombinationAsync(format, encoder, linked.Token, options);
            if (!check.Compatible) { ValidationMessage.Text = check.Reason; return; }
            Close(options);
        }
        catch (FormatException) { ValidationMessage.Text = "Введите числовые значения или оставьте поля пустыми."; }
        catch (OverflowException) { ValidationMessage.Text = "Числовое значение слишком велико."; }
        catch (OperationCanceledException) { ValidationMessage.Text = "Проверка отменена."; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or Win32Exception
            or TimeoutException or InvalidDataException)
        { ValidationMessage.Text = "Не удалось проверить параметры. Проверьте доступность FFmpeg."; }
        finally { SaveButton.IsEnabled = true; }
    }

    private void Cancel_Click(object? sender, RoutedEventArgs e) => Close(null);
}
