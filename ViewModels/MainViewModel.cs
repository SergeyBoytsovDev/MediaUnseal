using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using TrackExtractor.Models;
using TrackExtractor.Services;

namespace TrackExtractor.ViewModels;

public class MainViewModel : ViewModelBase
{
    private readonly FfmpegTools _tools;
    private readonly MediaAnalyzer _analyzer;
    private readonly TrackExporter _exporter;
    public ObservableCollection<string> Events { get; } = new();
    public ObservableCollection<UserLogEntry> UserEvents { get; } = new();
    public ObservableCollection<string> StreamSummaries { get; } = new();
    public ObservableCollection<ExportTrackViewModel> ExportTracks { get; } = new();

    public MainViewModel() : this(new FfmpegTools()) { }

    internal MainViewModel(FfmpegTools tools, MediaAnalyzer? analyzer = null, TrackExporter? exporter = null)
    {
        _tools = tools;
        _analyzer = analyzer ?? new MediaAnalyzer(tools.Diagnostics);
        _exporter = exporter ?? new TrackExporter(tools.Diagnostics);
        _tools.Diagnostics.EventRecorded += entry => Dispatcher.UIThread.Post(() =>
        {
            Events.Add($"[{entry.Time:HH:mm:ss}] {entry.Operation}: {entry.Message}" +
                (entry.Details is null ? "" : "  " + entry.Details));
            if (Events.Count > 200) Events.RemoveAt(0);
            if (UserLogEntry.FromDiagnostic(entry) is { } visible)
            {
                UserEvents.Add(visible);
                if (UserEvents.Count > 200) UserEvents.RemoveAt(0);
            }
        });
    }
    private bool _isBusy;
    private bool _isReady;
    private string _greeting = "Проверка FFmpeg и FFprobe…";
    private string _ffmpegStatus = "Проверка…";
    private string _ffprobeStatus = "Проверка…";
    private string _message = "";
    private string _installOption = "";
    private string _fileSelectionMessage = "";
    private bool _showDependencyOffer;
    private string? _ffprobePath;
    private string? _ffmpegPath;
    private MediaInfo? _selectedMedia;
    private string _analysisStatus = "Файл не выбран";
    private string _mediaSummary = "";
    private bool _isAnalyzing;
    private CancellationTokenSource? _analysisOperation;
    private CancellationTokenSource? _previewOperation;
    private Bitmap? _previewImage;
    private int _analysisGeneration;
    private int _activeAnalyses;
    private TaskCompletionSource? _analysesIdle;
    private string? _outputDirectory;
    private bool _isExporting;
    private string _exportStatus = "Выберите папку и дорожки.";
    private double _exportProgress;
    private bool _exportProgressIndeterminate;
    private CancellationTokenSource? _exportOperation;
    private TaskCompletionSource? _exportIdle;
    private Stopwatch? _exportWatch;
    private DispatcherTimer? _exportTimer;
    private string _exportActiveMessage = "";
    private CancellationTokenSource? _operation;
    private TaskCompletionSource? _idle;
    private double _progress;
    private bool _progressIndeterminate = true;

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (SetProperty(ref _isBusy, value))
            {
                if (value) _idle = new(TaskCreationOptions.RunContinuationsAsynchronously);
                else _idle?.TrySetResult();
                OnPropertyChanged(nameof(CanAct));
                OnPropertyChanged(nameof(CanAutoInstall));
                OnPropertyChanged(nameof(CanExtract));
                OnPropertyChanged(nameof(CanChooseFile));
                OnPropertyChanged(nameof(FooterStatus));
            }
        }
    }

    public bool CanAct => !IsBusy;
    public bool CanAutoInstall => CanAct;
    public bool NeedsTools => !IsReady;
    public bool IsReady
    {
        get => _isReady;
        private set
        {
            if (SetProperty(ref _isReady, value))
            {
                OnPropertyChanged(nameof(NeedsTools));
                OnPropertyChanged(nameof(CanExtract));
                OnPropertyChanged(nameof(CanChooseFile));
                OnPropertyChanged(nameof(FooterStatus));
                if (value) ShowDependencyOffer = false;
            }
        }
    }
    public string Greeting { get => _greeting; private set => SetProperty(ref _greeting, value); }
    public string FFmpegStatus { get => _ffmpegStatus; private set => SetProperty(ref _ffmpegStatus, value); }
    public string FFprobeStatus { get => _ffprobeStatus; private set => SetProperty(ref _ffprobeStatus, value); }
    public string Message { get => _message; private set => SetProperty(ref _message, value); }
    public string InstallOption { get => _installOption; private set => SetProperty(ref _installOption, value); }
    public string FileSelectionMessage
    {
        get => _fileSelectionMessage;
        private set { if (SetProperty(ref _fileSelectionMessage, value)) OnPropertyChanged(nameof(HasFileSelectionMessage)); }
    }
    public bool HasFileSelectionMessage => !string.IsNullOrWhiteSpace(FileSelectionMessage);
    public MediaInfo? SelectedMedia
    {
        get => _selectedMedia;
        private set
        {
            if (!SetProperty(ref _selectedMedia, value)) return;
            OnPropertyChanged(nameof(CanExtract));
            OnPropertyChanged(nameof(HasMedia));
            OnPropertyChanged(nameof(HasNoMedia));
            OnPropertyChanged(nameof(CanClearFile));
            OnPropertyChanged(nameof(HasSourceOrAnalysis));
            OnPropertyChanged(nameof(MediaFileName));
            OnPropertyChanged(nameof(MediaFilePath));
            OnPropertyChanged(nameof(ContainerDisplay));
            OnPropertyChanged(nameof(DurationDisplay));
            OnPropertyChanged(nameof(SizeDisplay));
            OnPropertyChanged(nameof(SizeExactDisplay));
            OnPropertyChanged(nameof(ResolutionDisplay));
            OnPropertyChanged(nameof(VideoCodecDisplay));
            OnPropertyChanged(nameof(AudioSummaryDisplay));
            OnPropertyChanged(nameof(SubtitleSummaryDisplay));
            OnPropertyChanged(nameof(CanBulkSelect));
            OnPropertyChanged(nameof(FooterStatus));
        }
    }
    public string AnalysisStatus
    {
        get => _analysisStatus;
        private set { if (SetProperty(ref _analysisStatus, value)) OnPropertyChanged(nameof(FooterStatus)); }
    }
    public string MediaSummary { get => _mediaSummary; private set => SetProperty(ref _mediaSummary, value); }
    public bool IsAnalyzing
    {
        get => _isAnalyzing;
        private set { if (SetProperty(ref _isAnalyzing, value)) { OnPropertyChanged(nameof(CanExtract)); OnPropertyChanged(nameof(CanClearFile)); OnPropertyChanged(nameof(HasSourceOrAnalysis)); OnPropertyChanged(nameof(FooterStatus)); } }
    }
    public bool HasActiveAnalyses => _activeAnalyses > 0;
    public bool ShowDependencyOffer { get => _showDependencyOffer; private set => SetProperty(ref _showDependencyOffer, value); }
    public string? OutputDirectory
    {
        get => _outputDirectory;
        private set { if (SetProperty(ref _outputDirectory, value)) { OnPropertyChanged(nameof(CanExtract)); OnPropertyChanged(nameof(CanOpenOutputDirectory)); } }
    }
    public bool IsExporting
    {
        get => _isExporting;
        private set
        {
            if (!SetProperty(ref _isExporting, value)) return;
            OnPropertyChanged(nameof(CanExtract));
            OnPropertyChanged(nameof(CanChooseFile));
            OnPropertyChanged(nameof(CanChooseFolder));
            OnPropertyChanged(nameof(CanBulkSelect));
            OnPropertyChanged(nameof(CanClearFile));
            OnPropertyChanged(nameof(FooterStatus));
        }
    }
    public bool CanExtract => IsReady && !IsBusy && !IsAnalyzing && !IsExporting
        && SelectedMedia is not null && OutputDirectory is not null && ExportTracks.Any(t => t.IsSelected && t.IsSupported);
    public bool CanChooseFile => IsReady && !IsBusy && !IsExporting;
    public bool CanClearFile => (HasMedia || IsAnalyzing) && !IsExporting;
    public bool HasSourceOrAnalysis => HasMedia || IsAnalyzing;
    public bool CanChooseFolder => !IsExporting;
    public bool CanOpenOutputDirectory => OutputDirectory is not null;
    public bool CanBulkSelect => SelectedMedia is not null && !IsExporting;
    public bool HasMedia => SelectedMedia is not null;
    public bool HasNoMedia => !HasMedia;
    public string TrackHeading => $"Найденные медиапотоки ({ExportTracks.Count})";
    public string SelectedCountDisplay => $"Выбрано: {ExportTracks.Count(t => t.IsSelected && t.IsSupported)}";
    public string FooterStatus => IsBusy ? "Проверка зависимостей…" : !IsReady
        ? "Нужны FFmpeg и FFprobe" : IsAnalyzing ? "Анализ файла…"
        : IsExporting ? "Идёт извлечение…" : AnalysisStatus == "Ошибка"
        ? "Ошибка анализа" : "Готов к работе";
    public string MediaFileName => SelectedMedia?.Name ?? "Файл не выбран";
    public string MediaFilePath => SelectedMedia?.FullPath ?? "Выберите файл или перетащите его в область слева";
    public string ContainerDisplay => SelectedMedia?.Container ?? "—";
    public string DurationDisplay => SelectedMedia is { } media ? FormatDuration(media.Duration) : "—";
    public string SizeDisplay => SelectedMedia is { } media ? FormatByteSize(media.Size) : "—";
    public string SizeExactDisplay => SelectedMedia is { } media ? $"{media.Size:N0} байт" : "—";
    public string ResolutionDisplay
    {
        get
        {
            var visual = SelectedMedia?.Streams.FirstOrDefault(s => s.Width is > 0 && s.Height is > 0);
            return visual is null ? "—" : $"{visual.Width} × {visual.Height}";
        }
    }
    public string VideoCodecDisplay
    {
        get
        {
            var videos = SelectedMedia?.Streams.Where(s => s.VisualKind == VisualKind.Video)
                .Select(s => s.Codec is null ? "—" : s.Profile is null
                    ? s.Codec.ToUpperInvariant() : $"{s.Codec.ToUpperInvariant()} ({s.Profile})")
                .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            return videos is { Length: > 0 } ? string.Join(", ", videos) : "—";
        }
    }
    public Bitmap? PreviewImage
    {
        get => _previewImage;
        private set
        {
            if (_previewImage == value) return;
            var old = _previewImage;
            _previewImage = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(HasPreview));
            OnPropertyChanged(nameof(HasNoPreview));
            old?.Dispose();
        }
    }
    public bool HasPreview => PreviewImage is not null;
    public bool HasNoPreview => PreviewImage is null;
    internal Func<MediaInfo, string, CancellationToken, Task<byte[]?>> PreviewLoader { get; set; } = MediaPreview.LoadAsync;
    public string AudioSummaryDisplay => SelectedMedia is { } media
        ? FormatStreamSummary(media.Streams.Where(s => s.CodecType == "audio").ToArray()) : "—";
    public string SubtitleSummaryDisplay => SelectedMedia is { } media
        ? FormatStreamSummary(media.Streams.Where(s => s.CodecType == "subtitle").ToArray()) : "—";
    public bool SelectAllAudio
    {
        get => AllSelected("audio");
        set => SetGroupSelected("audio", value);
    }
    public bool SelectAllSubtitles
    {
        get => AllSelected("subtitle");
        set => SetGroupSelected("subtitle", value);
    }
    public string ExportStatus { get => _exportStatus; private set => SetProperty(ref _exportStatus, value); }
    public double ExportProgress { get => _exportProgress; private set => SetProperty(ref _exportProgress, value); }
    public bool ExportProgressIndeterminate { get => _exportProgressIndeterminate; private set => SetProperty(ref _exportProgressIndeterminate, value); }
    public double Progress { get => _progress; private set => SetProperty(ref _progress, value); }
    public bool ProgressIndeterminate { get => _progressIndeterminate; private set => SetProperty(ref _progressIndeterminate, value); }
    public void CancelOperation() => _operation?.Cancel();
    public void CancelAnalysis() { _analysisOperation?.Cancel(); _previewOperation?.Cancel(); }
    public void CancelExport() => _exportOperation?.Cancel();
    public Task WaitForIdleAsync() => _idle?.Task ?? Task.CompletedTask;
    public Task WaitForAnalysesAsync() => _analysesIdle?.Task ?? Task.CompletedTask;
    public Task WaitForExportAsync() => _exportIdle?.Task ?? Task.CompletedTask;

    public void ReportError(string message) => Message = message;
    public void ReportExportError(string message) => ExportStatus = message;
    public void ReportFileError(string message)
    {
        FileSelectionMessage = message;
        AnalysisStatus = "Ошибка";
    }
    public string DiagnosticReport() => _tools.Diagnostics.ExportReport();
    public void ClearVisibleLog() { Events.Clear(); UserEvents.Clear(); }

    public void ClearCurrentFile()
    {
        if (IsExporting) return;
        ++_analysisGeneration;
        _analysisOperation?.Cancel();
        _previewOperation?.Cancel();
        _previewOperation = null;
        PreviewImage = null;
        IsAnalyzing = false;
        SelectedMedia = null;
        StreamSummaries.Clear();
        ExportTracks.Clear();
        MediaSummary = "";
        AnalysisStatus = "Файл не выбран";
        FileSelectionMessage = "";
        ExportStatus = "Выберите файл и дорожки.";
        ExportProgress = 0;
        ExportProgressIndeterminate = false;
        OnPropertyChanged(nameof(TrackHeading));
        OnPropertyChanged(nameof(SelectedCountDisplay));
        OnPropertyChanged(nameof(SelectAllAudio));
        OnPropertyChanged(nameof(SelectAllSubtitles));
        OnPropertyChanged(nameof(CanExtract));
    }

    private bool AllSelected(string codecType)
    {
        var group = ExportTracks.Where(t => t.Stream.CodecType == codecType && t.IsSupported).ToArray();
        return group.Length > 0 && group.All(t => t.IsSelected);
    }

    private void SetGroupSelected(string codecType, bool selected)
    {
        if (!CanBulkSelect) return;
        foreach (var track in ExportTracks.Where(t => t.Stream.CodecType == codecType && t.CanSelect))
            track.IsSelected = selected;
        OnPropertyChanged(nameof(SelectAllAudio));
        OnPropertyChanged(nameof(SelectAllSubtitles));
        OnPropertyChanged(nameof(SelectedCountDisplay));
    }
    public bool SelectFile()
    {
        if (IsExporting)
        {
            FileSelectionMessage = "Сначала остановите или завершите извлечение.";
            return false;
        }
        if (!IsReady)
        {
            FileSelectionMessage = "Для выбора файла сначала установите FFmpeg и FFprobe.";
            ShowDependencyOffer = true;
            return false;
        }
        FileSelectionMessage = "";
        ShowDependencyOffer = false;
        return true;
    }

    public async Task AnalyzeFileAsync(string path, CancellationToken cancellationToken)
    {
        if (!SelectFile() || _ffprobePath is null) return;
        _analysisOperation?.Cancel();
        _previewOperation?.Cancel();
        PreviewImage = null;
        var generation = ++_analysisGeneration;
        using var operation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _analysisOperation = operation;
        if (_activeAnalyses++ == 0) _analysesIdle = new(TaskCreationOptions.RunContinuationsAsynchronously);
        IsAnalyzing = true;
        SelectedMedia = null;
        StreamSummaries.Clear();
        ExportTracks.Clear();
        OnPropertyChanged(nameof(CanExtract));
        OnPropertyChanged(nameof(TrackHeading));
        OnPropertyChanged(nameof(SelectedCountDisplay));
        OnPropertyChanged(nameof(SelectAllAudio));
        OnPropertyChanged(nameof(SelectAllSubtitles));
        MediaSummary = "";
        AnalysisStatus = "Анализируется";
        try
        {
            var media = await Task.Run(() => _analyzer.AnalyzeAsync(path, _ffprobePath, operation.Token), operation.Token);
            if (generation != _analysisGeneration) return;
            SelectedMedia = media;
            MediaSummary = FormatMediaSummary(media);
            foreach (var stream in media.Streams)
            {
                var description = Describe(stream);
                StreamSummaries.Add(description);
                var track = new ExportTrackViewModel(stream, description, TrackExporter.Decide(media, stream));
                track.PropertyChanged += (_, args) =>
                {
                    if (args.PropertyName == nameof(ExportTrackViewModel.IsSelected))
                    {
                        OnPropertyChanged(nameof(CanExtract));
                        OnPropertyChanged(nameof(SelectedCountDisplay));
                        OnPropertyChanged(nameof(SelectAllAudio));
                        OnPropertyChanged(nameof(SelectAllSubtitles));
                    }
                };
                ExportTracks.Add(track);
            }
            OnPropertyChanged(nameof(CanExtract));
            OnPropertyChanged(nameof(TrackHeading));
            OnPropertyChanged(nameof(SelectedCountDisplay));
            OnPropertyChanged(nameof(SelectAllAudio));
            OnPropertyChanged(nameof(SelectAllSubtitles));
            AnalysisStatus = "Готово";
            if (_ffmpegPath is { } ffmpeg) _ = LoadPreviewAsync(media, ffmpeg, generation, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            if (generation == _analysisGeneration) AnalysisStatus = "Анализ отменён";
        }
        catch (Exception ex)
        {
            if (generation == _analysisGeneration)
            {
                AnalysisStatus = "Ошибка";
                FileSelectionMessage = ex switch
                {
                    FileNotFoundException => "Файл не найден. Выберите другой файл.",
                    UnauthorizedAccessException => "Нет доступа к файлу или FFprobe.",
                    TimeoutException => "Время ожидания FFprobe истекло. Выберите другой файл или повторите попытку.",
                    InvalidDataException or ArgumentException => ex.Message,
                    Win32Exception => "Не удалось запустить FFprobe. Повторите проверку зависимостей.",
                    IOException => "Не удалось прочитать файл. Проверьте доступ и повторите попытку.",
                    _ => "Не удалось проанализировать файл. Выберите другой файл."
                };
            }
        }
        finally
        {
            if (generation == _analysisGeneration)
            {
                IsAnalyzing = false;
                _analysisOperation = null;
            }
            if (--_activeAnalyses == 0) _analysesIdle?.TrySetResult();
        }
    }

    private async Task LoadPreviewAsync(MediaInfo media, string ffmpegPath, int generation,
        CancellationToken cancellationToken)
    {
        using var operation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _previewOperation = operation;
        try
        {
            var bytes = await PreviewLoader(media, ffmpegPath, operation.Token);
            if (generation != _analysisGeneration || operation.IsCancellationRequested || bytes is null) return;
            await using var stream = new MemoryStream(bytes, writable: false);
            var bitmap = new Bitmap(stream);
            if (generation == _analysisGeneration && !operation.IsCancellationRequested) PreviewImage = bitmap;
            else bitmap.Dispose();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException
            or ArgumentException or NotSupportedException or OutOfMemoryException)
        {
            // A missing or corrupt preview must not change the analyzed media state.
        }
        finally { if (_previewOperation == operation) _previewOperation = null; }
    }

    public void SetOutputDirectory(string path)
    {
        if (IsExporting) return;
        string normalized;
        try
        {
            var root = Path.GetPathRoot(path);
            if (OperatingSystem.IsWindows() && path.Length == 2 && root?.Length == 2
                && root[1] == ':') path += Path.DirectorySeparatorChar;
            normalized = Path.GetFullPath(path);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            ExportStatus = "Недопустимый путь к папке сохранения.";
            return;
        }
        if (!Directory.Exists(normalized))
        {
            ExportStatus = "Папка сохранения не найдена. Выберите другую папку.";
            return;
        }
        // Keep the trailing separator for drive and filesystem roots.
        OutputDirectory = normalized;
        ExportStatus = "Папка выбрана. Совпадающие имена получат номер; существующие файлы сохранятся.";
    }

    public async Task StartExportAsync(CancellationToken cancellationToken)
    {
        if (IsExporting) return;
        if (!CanExtract || SelectedMedia is null || OutputDirectory is null || _ffmpegPath is null)
        {
            ExportStatus = "Выберите доступные дорожки и папку сохранения; проверьте FFmpeg и FFprobe.";
            return;
        }
        var media = SelectedMedia;
        var selected = ExportTracks.Where(t => t.IsSelected && t.Decision.Plan is not null).ToArray();
        var plans = selected.Select(t => t.Decision.Plan!).ToArray();
        using var operation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _exportOperation = operation;
        _exportIdle = new(TaskCreationOptions.RunContinuationsAsynchronously);
        IsExporting = true;
        _exportWatch = Stopwatch.StartNew();
        _exportActiveMessage = $"Начато извлечение {plans.Length} дорожек.";
        _exportTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _exportTimer.Tick += (_, _) =>
        {
            if (IsExporting && _exportWatch is not null)
                ExportStatus = $"{_exportActiveMessage} Время: {TrackExporter.FormatElapsed(_exportWatch.Elapsed)}";
        };
        _exportTimer.Start();
        foreach (var track in ExportTracks) track.SetLocked(true);
        foreach (var track in selected) track.SetResult(ExportState.Pending, "Ожидает извлечения.", null);
        ExportProgress = 0;
        ExportProgressIndeterminate = true;
        ExportStatus = _exportActiveMessage;
        try
        {
            var updates = new Progress<ExportUpdate>(update =>
            {
                if (_exportOperation != operation || !IsExporting) return;
                var track = selected.FirstOrDefault(t => t.Stream.Index == update.StreamIndex);
                track?.SetResult(update.State, update.Message, null);
                ExportProgressIndeterminate = update.OverallPercent is null;
                if (update.OverallPercent is { } value) ExportProgress = value;
                _exportActiveMessage = $"{update.Message} ({update.Processed}/{update.Total} обработано)";
                ExportStatus = $"{_exportActiveMessage} Время: {TrackExporter.FormatElapsed(_exportWatch!.Elapsed)}";
            });
            var result = await Task.Run(() => _exporter.ExportAsync(media, plans, OutputDirectory,
                _ffmpegPath, updates, operation.Token), operation.Token);
            _exportOperation = null;
            foreach (var trackResult in result.Tracks)
                selected.First(t => t.Stream.Index == trackResult.StreamIndex)
                    .SetResult(trackResult.State, trackResult.Message, trackResult.OutputPath);
            ExportProgressIndeterminate = false;
            ExportProgress = result.Success ? 100 : Math.Min(99,
                result.Tracks.Count(t => t.State is ExportState.Completed or ExportState.Failed) * 100.0 / plans.Length);
            ExportStatus = (result.Success ? "Все выбранные дорожки извлечены."
                : result.WasCancelled ? "Извлечение остановлено. Готовые файлы сохранены."
                : "Очередь завершена с ошибками. Готовые файлы сохранены.") +
                $" Общее время: {TrackExporter.FormatElapsed(result.Elapsed)}";
        }
        catch (OperationCanceledException)
        {
            ExportProgressIndeterminate = false;
            foreach (var track in selected) track.SetResult(ExportState.Cancelled, "Извлечение отменено.", null);
            ExportStatus = "Извлечение остановлено. Готовые файлы сохранены. Общее время: " +
                TrackExporter.FormatElapsed(_exportWatch!.Elapsed);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            ExportProgressIndeterminate = false;
            foreach (var track in selected) track.SetResult(ExportState.Failed, "Не удалось начать извлечение.", null);
            ExportStatus = ex is UnauthorizedAccessException
                ? "Нет доступа к папке сохранения."
                : ex is DirectoryNotFoundException ? "Папка сохранения не найдена."
                : "Не удалось начать извлечение. Проверьте исходный файл и папку сохранения.";
            ExportStatus += " Общее время: " + TrackExporter.FormatElapsed(_exportWatch!.Elapsed);
        }
        catch (Exception)
        {
            ExportProgressIndeterminate = false;
            foreach (var track in selected) track.SetResult(ExportState.Failed, "Ошибка извлечения.", null);
            ExportStatus = "Ошибка извлечения. Проверьте лог и повторите попытку. Общее время: " +
                TrackExporter.FormatElapsed(_exportWatch!.Elapsed);
        }
        finally
        {
            _exportTimer?.Stop();
            _exportTimer = null;
            _exportWatch?.Stop();
            _exportOperation = null;
            IsExporting = false;
            foreach (var track in ExportTracks) track.SetLocked(false);
            _exportIdle?.TrySetResult();
        }
    }

    internal static string FormatMediaSummary(MediaInfo media)
    {
        var onlyStillImages = media.ImageCount > 0 && media.VideoCount == 0 && media.AnimationCount == 0
            && media.AudioCount == 0 && media.SubtitleCount == 0 && media.UnknownVisualCount == 0;
        return $"{media.Name}\n{media.FullPath}\nРазмер: {media.Size:N0} байт; контейнер: {media.Container ?? "—"}; " +
            $"длительность: {(onlyStillImages ? "—" : FormatDuration(media.Duration))}; битрейт: {FormatBitRate(media.BitRate)}\n" +
            $"Видео: {media.VideoCount}; изображения: {media.ImageCount}; анимации: {media.AnimationCount}; " +
            $"аудио: {media.AudioCount}; субтитры: {media.SubtitleCount}; " +
            $"неопределённые визуальные: {media.UnknownVisualCount}; всего потоков: {media.Streams.Count}";
    }

    internal static string Describe(MediaStreamInfo stream)
    {
        var details = new System.Collections.Generic.List<string>();
        var visual = stream.CodecType == "video" || stream.VisualKind is VisualKind.Image or VisualKind.Animation;
        if (visual)
            details.Add(stream.Width is { } width && stream.Height is { } height ? $"{width}×{height}" : "разрешение: —");
        if (stream.VisualKind is VisualKind.Video or VisualKind.Animation)
        {
            details.Add(stream.FrameRate is > 0 and <= 1000 ? $"{stream.FrameRate.Value:0.##} кадр/с" : "FPS: —");
            details.Add("длительность: " + FormatDuration(stream.Duration));
        }
        if (stream.CodecType == "audio")
        {
            details.Add(stream.Channels is { } channels ? $"{channels} канал(ов)" : "каналы: —");
            details.Add(stream.SampleRate is { } sampleRate ? $"{sampleRate} Гц" : "частота: —");
        }
        if (stream.Language is { } language) details.Add(language);
        if (stream.Title is { } title) details.Add(title.Length > 80 ? title[..80] + "…" : title);
        if (stream.SubtitleKind != SubtitleKind.Unknown) details.Add(stream.SubtitleKind == SubtitleKind.Text ? "текстовые" : "графические");
        var type = stream.VisualKind switch
        {
            VisualKind.Video => "Видео",
            VisualKind.Image => stream.IsCoverArt ? "Изображение (обложка)" : "Изображение",
            VisualKind.Animation => "Анимация",
            _ when stream.CodecType == "video" => "Неопределённый визуальный поток",
            _ => stream.CodecType switch
            {
                "audio" => "Аудио", "subtitle" => "Субтитры", "attachment" => "Вложение",
                null => "Неизвестный тип", _ => stream.CodecType
            }
        };
        var codec = stream.ImageFormat is { } imageFormat && stream.VisualKind is VisualKind.Image or VisualKind.Animation
            ? $"{imageFormat} ({stream.Codec ?? "—"})" : stream.Codec ?? "—";
        return $"#{stream.Index?.ToString() ?? "—"} {type} · {codec}" +
            (details.Count == 0 ? "" : " · " + string.Join(" · ", details));
    }

    private static string FormatDuration(TimeSpan? value) => value is { } duration
        ? $"{(long)duration.TotalHours:00}:{duration.Minutes:00}:{duration.Seconds:00}.{duration.Milliseconds:000}"
        : "—";

    private static string FormatByteSize(long bytes) => bytes switch
    {
        >= 1L << 30 => $"{bytes / (double)(1L << 30):0.##} ГиБ",
        >= 1L << 20 => $"{bytes / (double)(1L << 20):0.##} МиБ",
        >= 1L << 10 => $"{bytes / (double)(1L << 10):0.##} КиБ",
        _ => $"{bytes} байт"
    };

    internal static string FormatCount(int count, string one, string few, string many)
    {
        var lastTwo = count % 100;
        var word = lastTwo is >= 11 and <= 14 ? many : (count % 10) switch
        {
            1 => one, >= 2 and <= 4 => few, _ => many
        };
        return $"{count} {word}";
    }

    private static string FormatStreamSummary(MediaStreamInfo[] streams)
    {
        if (streams.Length == 0) return "—";
        var codecs = string.Join(", ", streams.Select(s => s.Codec?.ToUpperInvariant() ?? "—")
            .Distinct(StringComparer.OrdinalIgnoreCase).Take(4));
        return $"{FormatCount(streams.Length, "дорожка", "дорожки", "дорожек")} ({codecs})";
    }

    internal static string FormatBitRate(long? value) => value switch
    {
        null => "—",
        >= 1_000_000 => $"{value.Value / 1_000_000.0:0.##} Мбит/с",
        >= 1_000 => $"{value.Value / 1_000.0:0.##} Кбит/с",
        _ => $"{value.Value} бит/с"
    };

    public async Task CheckAsync(CancellationToken cancellationToken)
    {
        if (IsBusy) return;
        using var operation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _operation = operation;
        cancellationToken = operation.Token;
        ProgressIndeterminate = true;
        IsBusy = true;
        try
        {
            await RefreshAsync(cancellationToken);
            if (!IsReady)
                InstallOption = await Task.Run(() => _tools.GetInstallOptionAsync(cancellationToken), cancellationToken);
        }
        finally { _operation = null; IsBusy = false; }
    }

    public async Task InstallAsync(CancellationToken cancellationToken, bool? directDownload = null)
    {
        if (IsBusy || IsReady) return;
        if (!OperatingSystem.IsWindows())
        {
            Message = InstallOption;
            return;
        }
        var useDirectDownload = directDownload ?? _tools.OfferDirectDownload;
        if (useDirectDownload && !_tools.CanDirectDownload)
        {
            Message = "Прямая загрузка поддерживается только в Windows 10/11 x64.";
            return;
        }

        using var operation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _operation = operation;
        cancellationToken = operation.Token;
        ProgressIndeterminate = true;
        Progress = 0;
        IsBusy = true;
        try
        {
            var progress = new Progress<InstallProgress>(value =>
            {
                if (_operation != operation || operation.IsCancellationRequested) return;
                Message = value.Message;
                ProgressIndeterminate = !value.Percent.HasValue;
                Progress = value.Percent ?? 0;
            });
            Message = useDirectDownload ? "Подготовка загрузки с gyan.dev…" : "Выполняется установка через WinGet…";
            var result = await Task.Run(() => useDirectDownload
                ? _tools.DownloadWindowsAsync(progress, cancellationToken)
                : _tools.InstallOnWindowsAsync(cancellationToken), cancellationToken);
            await RefreshAsync(cancellationToken);
            Message = result.Success && !IsReady
                ? "Повторная проверка не подтвердила готовность обеих утилит. Повторите проверку или укажите пути вручную."
                : result.Message;
            if (!IsReady && _tools.OfferDirectDownload) InstallOption = result.Message;
        }
        finally { _operation = null; IsBusy = false; }
    }

    public async Task SetManualPathAsync(string name, string path, CancellationToken cancellationToken)
    {
        if (IsBusy) return;
        using var operation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _operation = operation;
        cancellationToken = operation.Token;
        ProgressIndeterminate = true;
        IsBusy = true;
        try
        {
            var error = await Task.Run(() => _tools.SetManualPathAsync(name, path, cancellationToken), cancellationToken);
            if (error is not null)
            {
                Message = error;
                return;
            }
            await RefreshAsync(cancellationToken);
        }
        finally { _operation = null; IsBusy = false; }
    }

    private async Task RefreshAsync(CancellationToken cancellationToken)
    {
        Message = "Проверка утилит…";
        IsReady = false;
        var status = await Task.Run(() => _tools.CheckAsync(cancellationToken), cancellationToken);
        FFmpegStatus = Format(status.FFmpeg);
        FFprobeStatus = Format(status.FFprobe);
        IsReady = status.IsReady;
        _ffmpegPath = status.IsReady ? status.FFmpeg.Path : null;
        _ffprobePath = status.IsReady ? status.FFprobe.Path : null;
        if (!status.IsReady) { CancelAnalysis(); CancelExport(); }
        if (IsReady) InstallOption = "";
        Greeting = IsReady
            ? $"Готово. FFmpeg {Version(status.FFmpeg)}, FFprobe {Version(status.FFprobe)}."
            : "FFmpeg и FFprobe требуются для работы с медиафайлами.";
        Message = _tools.SettingsError ?? (IsReady ? "Обе утилиты готовы." : "Установите недостающие утилиты или укажите их пути.");
    }

    private static string Format(ToolStatus status) =>
        status.IsReady ? $"{status.Name}: {status.Details}\n{status.Path}" : $"{status.Name}: {status.Details}";

    private static string Version(ToolStatus status) => status.Details.Split(' ', StringSplitOptions.RemoveEmptyEntries)[2];
}

public sealed class ExportTrackViewModel : ViewModelBase
{
    private static readonly IBrush ReadyBackground = new SolidColorBrush(Color.Parse("#175541"));
    private static readonly IBrush RunningBackground = new SolidColorBrush(Color.Parse("#19477B"));
    private static readonly IBrush ErrorBackground = new SolidColorBrush(Color.Parse("#633042"));
    private static readonly IBrush MutedBackground = new SolidColorBrush(Color.Parse("#344359"));
    public ExportTrackViewModel(MediaStreamInfo stream, string caption, ExportDecision decision)
    {
        Stream = stream;
        Caption = caption;
        Decision = decision;
        _status = decision.Plan is null ? "Недоступно: " + decision.Reason : "Не выбран";
    }

    private bool _isSelected;
    private bool _locked;
    private ExportState _state = ExportState.Pending;
    private string _status;
    private string? _resultPath;
    public MediaStreamInfo Stream { get; }
    public string Caption { get; }
    public ExportDecision Decision { get; }
    public bool IsSupported => Decision.Plan is not null;
    public bool CanSelect => IsSupported && !_locked;
    public string IndexDisplay => Stream.Index?.ToString(CultureInfo.InvariantCulture) ?? "—";
    public string TypeDisplay => Stream.VisualKind switch
    {
        VisualKind.Image => Stream.IsCoverArt ? "Обложка" : "Изображение",
        VisualKind.Animation => "Анимация",
        VisualKind.Video => "Видео",
        _ => Stream.CodecType switch
        {
            "audio" => "Аудио", "subtitle" => "Субтитры", "attachment" => "Вложение",
            _ => "Другой"
        }
    };
    public string TypeGlyph => Stream.CodecType switch
    {
        "audio" => "♫", "subtitle" => "▤", "attachment" => "▧", _ => "▣"
    };
    public string LanguageDisplay
    {
        get
        {
            var language = Stream.Language;
            if (string.IsNullOrWhiteSpace(language) || language == "und") return "—";
            try { return $"{CultureInfo.GetCultureInfo(language).DisplayName} ({language})"; }
            catch (CultureNotFoundException) { return language; }
        }
    }
    public string CodecDisplay => Stream.Codec is null ? "—" : Stream.Profile is null
        ? Stream.Codec.ToUpperInvariant() : $"{Stream.Codec.ToUpperInvariant()} ({Stream.Profile})";
    public string BitRateDisplay => MainViewModel.FormatBitRate(Stream.BitRate);
    public string DetailsDisplay => Stream.CodecType switch
    {
        "audio" => Stream.Channels is { } channels
            ? MainViewModel.FormatCount(channels, "канал", "канала", "каналов")
                + (string.IsNullOrWhiteSpace(Stream.ChannelLayout) ? "" : $" ({Stream.ChannelLayout})")
            : "Каналы: —",
        "subtitle" => Stream.SubtitleKind switch
        {
            SubtitleKind.Text => "Текстовые", SubtitleKind.Graphic => "Графические", _ => "—"
        },
        _ => Stream.Width is { } width && Stream.Height is { } height ? $"{width} × {height}" : "—"
    };
    public ExportState State { get => _state; private set => SetProperty(ref _state, value); }
    public bool IsSelected
    {
        get => _isSelected;
        set { if (CanSelect) SetProperty(ref _isSelected, value); }
    }
    public string Status { get => _status; private set => SetProperty(ref _status, value); }
    public string StatusLabel => !IsSupported ? "Недоступен" : State switch
    {
        ExportState.Running => "Извлекается", ExportState.Completed => "Извлечён",
        ExportState.Failed => "Ошибка", ExportState.Cancelled => "Отменено",
        _ => "Доступен"
    };
    public IBrush StatusBadgeBackground => !IsSupported ? MutedBackground : State switch
    {
        ExportState.Running => RunningBackground,
        ExportState.Failed or ExportState.Cancelled => ErrorBackground,
        _ => ReadyBackground
    };
    public IBrush StatusForeground => !IsSupported ? new SolidColorBrush(Color.Parse("#F0989D")) : State switch
    {
        ExportState.Failed or ExportState.Cancelled => new SolidColorBrush(Color.Parse("#F0989D")),
        ExportState.Running => new SolidColorBrush(Color.Parse("#82BAFF")),
        _ => new SolidColorBrush(Color.Parse("#51D990"))
    };
    public string? ResultPath { get => _resultPath; private set => SetProperty(ref _resultPath, value); }

    public void SetLocked(bool locked)
    {
        if (SetProperty(ref _locked, locked)) OnPropertyChanged(nameof(CanSelect));
    }

    public void SetResult(ExportState state, string message, string? path)
    {
        State = state;
        Status = message;
        ResultPath = path;
        OnPropertyChanged(nameof(StatusLabel));
        OnPropertyChanged(nameof(StatusBadgeBackground));
        OnPropertyChanged(nameof(StatusForeground));
    }
}

public sealed record UserLogEntry(string Time, string Message, DiagnosticLevel Level, bool IsSuccess)
{
    public bool IsWarning => Level == DiagnosticLevel.Warn;
    public bool IsError => Level == DiagnosticLevel.Error;
    public string Symbol => IsError ? "●" : IsWarning ? "●" : IsSuccess ? "●" : "◉";

    internal static UserLogEntry? FromDiagnostic(DiagnosticEvent entry)
    {
        var message = entry.Message;
        if (entry.Operation == "Проверка")
        {
            if (message.StartsWith("Проверяется ", StringComparison.OrdinalIgnoreCase))
                message = message.Split(':')[0] + "…";
            else if (message.Contains("не прошёл проверку", StringComparison.OrdinalIgnoreCase))
                message = message.Split(' ')[0] + " не найден или не работает.";
            else if (message.StartsWith("ffmpeg ", StringComparison.OrdinalIgnoreCase)
                || message.StartsWith("ffprobe ", StringComparison.OrdinalIgnoreCase))
            {
                var version = Regex.Match(message,
                    @"(?i)^(ffmpeg|ffprobe)\s+(?:ffmpeg|ffprobe)\s+version\s+([^\s]+)");
                message = version.Success
                    ? $"{(version.Groups[1].Value.Equals("ffmpeg", StringComparison.OrdinalIgnoreCase) ? "FFmpeg" : "FFprobe")} {version.Groups[2].Value} обнаружен. Готов к работе."
                    : message.Split(':')[0] + ". Готов к работе.";
            }
        }
        else if (entry.Operation == "Анализ")
        {
            if (message.StartsWith("Анализ завершён", StringComparison.OrdinalIgnoreCase))
            {
                var marker = message.LastIndexOf("потоков: ", StringComparison.OrdinalIgnoreCase);
                message = marker >= 0 ? "Анализ завершён. Обнаружено медиапотоков: "
                    + message[(marker + 9)..].TrimEnd('.') : "Анализ завершён.";
            }
            else if (message.StartsWith("Начат", StringComparison.OrdinalIgnoreCase))
                message = "Начат анализ файла.";
        }
        else if (entry.Operation == "Экспорт")
        {
            if (message.Contains("SeekHead=", StringComparison.Ordinal)
                || message.StartsWith("FFmpeg для потока", StringComparison.OrdinalIgnoreCase)
                || message.StartsWith("Чтение вложения Matroska", StringComparison.OrdinalIgnoreCase)
                || message.Contains("команда FFmpeg", StringComparison.OrdinalIgnoreCase)) return null;
            if (message.StartsWith("Начало ", StringComparison.OrdinalIgnoreCase))
            {
                var count = Regex.Match(message, @"потоков: (\d+)");
                message = count.Success ? $"Начато извлечение: {count.Groups[1].Value} потоков."
                    : "Начато извлечение.";
            }
            else if (message.StartsWith("Начато извлечение:", StringComparison.OrdinalIgnoreCase))
            {
                var index = Regex.Match(message, @"поток #(\d+)");
                var format = Regex.Match(message, @"формат ([^;]+)");
                message = index.Success ? $"Начато извлечение потока #{index.Groups[1].Value}" +
                    (format.Success ? $" в формате {format.Groups[1].Value}." : ".") : "Начато извлечение потока.";
            }
            else if (message.StartsWith("Завершён поток #", StringComparison.OrdinalIgnoreCase))
            {
                var index = Regex.Match(message, @"#(\d+)");
                var format = Regex.Match(message, @"контейнер ([^.;]+)");
                var size = Regex.Match(entry.Details ?? "", @"размер ([\d\s\u00a0]+) байт");
                message = $"Извлечён поток #{index.Groups[1].Value}" +
                    (format.Success ? $" ({format.Groups[1].Value})" : "") +
                    (size.Success ? $", {size.Groups[1].Value.Trim()} байт" : "") + ".";
            }
            else if (message.StartsWith("Очередь завершена успешно", StringComparison.OrdinalIgnoreCase))
                message = "Извлечение завершено.";
            else if (message.StartsWith("Очередь остановлена", StringComparison.OrdinalIgnoreCase))
                message = "Извлечение остановлено пользователем.";
            else if (message.StartsWith("Очередь завершена с ошибками", StringComparison.OrdinalIgnoreCase))
                message = "Извлечение завершено с ошибками.";
            else if (message.StartsWith("Ошибка потока #", StringComparison.OrdinalIgnoreCase))
            {
                var index = Regex.Match(message, @"#(\d+)");
                var reason = message.Contains("UnauthorizedAccessException") ? "нет доступа к папке сохранения"
                    : message.Contains("IOException") ? "ошибка записи файла"
                    : message.Contains("Win32Exception") ? "не удалось запустить FFmpeg"
                    : message.Split(';')[0].Split(':', 2).Last().Trim();
                message = $"Не удалось извлечь поток #{index.Groups[1].Value}: {reason}.";
            }
            else
            {
                var details = message.IndexOf(';');
                if (details >= 0) message = message[..details] + ".";
            }
        }
        else if (entry.Operation is "HTTPS" or "Зависимости" &&
            (message.Contains("Источник ") || message.Contains("Путь "))) return null;
        if (message.Length > 240) message = message[..237] + "…";
        return new UserLogEntry(entry.Time.ToString("HH:mm:ss"), message, entry.Level,
            entry.Level == DiagnosticLevel.Info &&
            (message.Contains("готов", StringComparison.OrdinalIgnoreCase)
                || message.Contains("заверш", StringComparison.OrdinalIgnoreCase)
                || message.Contains("успеш", StringComparison.OrdinalIgnoreCase)
                || message.Contains("извлечён", StringComparison.OrdinalIgnoreCase)
                || message.Contains("обнаружен", StringComparison.OrdinalIgnoreCase)));
    }
}
