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
    private MediaInfo? _media;
    private MediaStreamInfo? _stream;
    private ConversionSettings? _existing;
    private CancellationToken _shutdown;
    private readonly CancellationTokenSource _closed = new();
    private int _encoderRevision;
    private string _kind = "";
    private ConversionFormat? _copyFormat;
    private TrackExportPlan? _copyPlan;
    private const string CompatibleSearch = "Совместимые кодировщики";
    private const string HardwareSearch = "Аппаратные кодировщики";
    internal bool AppliedCopy { get; private set; }

    public ConversionWindow()
    {
        InitializeComponent();
        Closed += (_, _) => _closed.Cancel();
    }

    internal ConversionWindow(FfmpegConversionCatalog catalog, DiagnosticLog diagnostics, MediaInfo media, MediaStreamInfo stream,
        ConversionSettings? existing, CancellationToken shutdown, ConversionFormat? initialFormat = null,
        TrackExportPlan? copyPlan = null) : this()
    {
        _catalog = catalog;
        _diagnostics = diagnostics;
        _media = media;
        _stream = stream;
        _existing = existing;
        _shutdown = shutdown;
        _kind = stream.VisualKind == VisualKind.Video ? "video" : stream.CodecType ?? "";
        Title = StreamHeading.Text = _kind switch
        {
            "video" => "Расширенные настройки видео",
            "audio" => "Расширенные настройки аудио",
            _ => "Расширенные настройки субтитров"
        };
        VideoOptions.IsVisible = _kind == "video";
        AudioOptions.IsVisible = _kind == "audio";
        _copyPlan = copyPlan;
        if (copyPlan is not null)
            _copyFormat = new(copyPlan.Muxer, copyPlan.Extension, "Исходный — без перекодирования",
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
        UpdateExample();
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
                "копирование без перекодирования") };
            EncoderBox.SelectedIndex = 0;
            EncoderNote.Text = "Оригинальный поток будет скопирован без перекодирования.";
            UpdateExample();
            return;
        }
        EncoderBox.ItemsSource = InitialEncoders(_catalog, format, _kind, _existing);
        EncoderBox.SelectedItem = _catalog.SuggestedEncoder(format, _kind);
        EncoderNote.Text = "Другие кодировщики можно проверить для выбранного формата.";
        UpdateExample();
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
        EncoderNote.Text = "Проверка дополнительных кодировщиков…";
        RecordSearch(CompatibleSearch, "Начат поиск совместимых кодировщиков.",
            $"формат {format.Muxer}; тип {_kind}");
        try
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(_shutdown, _closed.Token);
            var working = new System.Collections.Generic.List<ConversionEncoder>();
            foreach (var encoder in _catalog.EncodersFor(_kind).Where(e => !e.IsHardware))
            {
                linked.Token.ThrowIfCancellationRequested();
                var result = await _catalog.CheckCombinationAsync(format, encoder, linked.Token,
                    diagnostics: details => _diagnostics?.Record(DiagnosticLevel.Info,
                        CompatibleSearch, "Ответ FFmpeg при проверке кодировщика.", details));
                if (result.Compatible) working.Add(encoder);
                _diagnostics?.Record(DiagnosticLevel.Info, CompatibleSearch,
                    $"{encoder.Name}: проверка {(result.Compatible ? "пройдена" : "не пройдена")}.",
                    result.Compatible ? null : result.Reason);
            }
            _catalog.RememberConfirmedAdditional(format, _kind, working);
            if (FormatBox.SelectedItem is not ConversionFormat selectedFormat || selectedFormat != format)
            {
                RecordSearch(CompatibleSearch, "Результат поиска не применён: выходной формат изменён.",
                    level: DiagnosticLevel.Warn);
                return;
            }
            var current = EncoderBox.SelectedItem;
            var hardware = EncoderBox.ItemsSource is System.Collections.IEnumerable items
                ? items.Cast<ConversionEncoder>().Where(e => e.IsHardware)
                : Enumerable.Empty<ConversionEncoder>();
            EncoderBox.ItemsSource = working.Concat(hardware).Distinct().ToArray();
            EncoderBox.SelectedItem = current;
            EncoderNote.Text = $"Подтверждено совместимых кодировщиков: {working.Count}.";
            RecordSearch(CompatibleSearch, working.Count == 0
                ? "Совместимые кодировщики не найдены."
                : $"Найдено {working.Count} совместимых кодировщиков.");
        }
        catch (OperationCanceledException)
        {
            EncoderNote.Text = "Проверка кодировщиков отменена.";
            RecordSearch(CompatibleSearch, "Поиск совместимых кодировщиков отменён.", level: DiagnosticLevel.Warn);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or Win32Exception
            or TimeoutException or InvalidDataException)
        {
            EncoderNote.Text = "Не удалось проверить дополнительные кодировщики.";
            RecordSearch(CompatibleSearch, "Не удалось завершить поиск совместимых кодировщиков.",
                ex.Message, DiagnosticLevel.Error);
        }
        finally { MoreEncodersButton.IsEnabled = true; }
    }

    private void RecordSearch(string operation, string message, string? details = null,
        DiagnosticLevel level = DiagnosticLevel.Info) =>
        _diagnostics?.Record(level, operation, "Поиск: " + message, details);

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
        RecordSearch(HardwareSearch, "Начат поиск аппаратных кодировщиков.", $"формат {format.Muxer}");
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
                        HardwareSearch, "Ответ FFmpeg при проверке кодировщика.", details));
                if (result.Compatible)
                {
                    working.Add(encoder);
                    _diagnostics?.Record(DiagnosticLevel.Info, HardwareSearch,
                        $"{encoder.Name}: проверка пройдена.");
                }
                else
                {
                    searchFailed |= result.Reason.StartsWith("Не удалось проверить кодировщик", StringComparison.Ordinal);
                    _diagnostics?.Record(DiagnosticLevel.Info, HardwareSearch,
                        $"{encoder.Name}: проверка не пройдена.", result.Reason);
                }
            }
            if (!searchFailed) _catalog.RememberConfirmedHardware(format, working);
            if (FormatBox.SelectedItem is not ConversionFormat selectedFormat || selectedFormat != format)
            {
                RecordSearch(HardwareSearch, "Результат поиска не применён: выходной формат изменён.",
                    level: DiagnosticLevel.Warn);
                return;
            }
            var current = EncoderBox.SelectedItem;
            var software = EncoderBox.ItemsSource is System.Collections.IEnumerable items
                ? items.Cast<ConversionEncoder>().Where(e => !e.IsHardware)
                : Enumerable.Empty<ConversionEncoder>();
            EncoderBox.ItemsSource = software.Concat(working).Distinct().ToArray();
            EncoderBox.SelectedItem = current;
            EncoderNote.Text = searchFailed
                ? "Не удалось завершить проверку аппаратных кодировщиков. Подробности в диагностическом журнале."
                : working.Count > 0 ? $"Найдено {working.Count} доступных аппаратных кодировщиков."
                : "Доступные аппаратные кодировщики не найдены.";
            RecordSearch(HardwareSearch, searchFailed
                ? "Не удалось завершить поиск аппаратных кодировщиков."
                : working.Count > 0 ? $"Найдено {working.Count} доступных аппаратных кодировщиков."
                : "Доступные аппаратные кодировщики не найдены.",
                level: searchFailed ? DiagnosticLevel.Error : DiagnosticLevel.Info);
        }
        catch (OperationCanceledException)
        {
            EncoderNote.Text = "Проверка аппаратных кодировщиков отменена.";
            RecordSearch(HardwareSearch, "Поиск аппаратных кодировщиков отменён.", level: DiagnosticLevel.Warn);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or Win32Exception
            or TimeoutException or InvalidDataException)
        {
            RecordSearch(HardwareSearch, "Не удалось завершить поиск аппаратных кодировщиков.",
                ex.Message, DiagnosticLevel.Error);
            EncoderNote.Text = "Не удалось проверить аппаратные кодировщики.";
        }
        finally { HardwareButton.IsEnabled = true; }
    }

    private void UpdateExample()
    {
        if (_media is null || _stream is null || FormatBox.SelectedItem is not ConversionFormat format
            || EncoderBox.SelectedItem is not ConversionEncoder encoder) return;
        var plan = ReferenceEquals(format, _copyFormat) ? _copyPlan! : new TrackExportPlan(_stream, format.Extension, format.Muxer, false,
            Conversion: new ConversionSettings(format, encoder));
        OutputExample.Text = "Выходной файл: " + TrackExporter.BuildFileName(_media, plan);
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
