using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
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
            Events.Add($"{entry.Time:HH:mm:ss} [{entry.Level.ToString().ToUpperInvariant()}] {entry.Message}");
            if (Events.Count > 200) Events.RemoveAt(0);
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
                if (value) ShowDependencyOffer = false;
            }
        }
    }
    public string Greeting { get => _greeting; private set => SetProperty(ref _greeting, value); }
    public string FFmpegStatus { get => _ffmpegStatus; private set => SetProperty(ref _ffmpegStatus, value); }
    public string FFprobeStatus { get => _ffprobeStatus; private set => SetProperty(ref _ffprobeStatus, value); }
    public string Message { get => _message; private set => SetProperty(ref _message, value); }
    public string InstallOption { get => _installOption; private set => SetProperty(ref _installOption, value); }
    public string FileSelectionMessage { get => _fileSelectionMessage; private set => SetProperty(ref _fileSelectionMessage, value); }
    public MediaInfo? SelectedMedia
    {
        get => _selectedMedia;
        private set { if (SetProperty(ref _selectedMedia, value)) OnPropertyChanged(nameof(CanExtract)); }
    }
    public string AnalysisStatus { get => _analysisStatus; private set => SetProperty(ref _analysisStatus, value); }
    public string MediaSummary { get => _mediaSummary; private set => SetProperty(ref _mediaSummary, value); }
    public bool IsAnalyzing
    {
        get => _isAnalyzing;
        private set { if (SetProperty(ref _isAnalyzing, value)) OnPropertyChanged(nameof(CanExtract)); }
    }
    public bool HasActiveAnalyses => _activeAnalyses > 0;
    public bool ShowDependencyOffer { get => _showDependencyOffer; private set => SetProperty(ref _showDependencyOffer, value); }
    public string? OutputDirectory
    {
        get => _outputDirectory;
        private set { if (SetProperty(ref _outputDirectory, value)) OnPropertyChanged(nameof(CanExtract)); }
    }
    public bool IsExporting
    {
        get => _isExporting;
        private set { if (SetProperty(ref _isExporting, value)) OnPropertyChanged(nameof(CanExtract)); }
    }
    public bool CanExtract => IsReady && !IsBusy && !IsAnalyzing && !IsExporting
        && SelectedMedia is not null && OutputDirectory is not null && ExportTracks.Any(t => t.IsSelected && t.IsSupported);
    public string ExportStatus { get => _exportStatus; private set => SetProperty(ref _exportStatus, value); }
    public double ExportProgress { get => _exportProgress; private set => SetProperty(ref _exportProgress, value); }
    public bool ExportProgressIndeterminate { get => _exportProgressIndeterminate; private set => SetProperty(ref _exportProgressIndeterminate, value); }
    public double Progress { get => _progress; private set => SetProperty(ref _progress, value); }
    public bool ProgressIndeterminate { get => _progressIndeterminate; private set => SetProperty(ref _progressIndeterminate, value); }
    public void CancelOperation() => _operation?.Cancel();
    public void CancelAnalysis() => _analysisOperation?.Cancel();
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
        var generation = ++_analysisGeneration;
        using var operation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _analysisOperation = operation;
        if (_activeAnalyses++ == 0) _analysesIdle = new(TaskCreationOptions.RunContinuationsAsynchronously);
        IsAnalyzing = true;
        SelectedMedia = null;
        StreamSummaries.Clear();
        ExportTracks.Clear();
        OnPropertyChanged(nameof(CanExtract));
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
                    if (args.PropertyName == nameof(ExportTrackViewModel.IsSelected)) OnPropertyChanged(nameof(CanExtract));
                };
                ExportTracks.Add(track);
            }
            OnPropertyChanged(nameof(CanExtract));
            AnalysisStatus = "Готово";
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

    private static string FormatBitRate(long? value) => value switch
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
    public ExportState State { get => _state; private set => SetProperty(ref _state, value); }
    public bool IsSelected
    {
        get => _isSelected;
        set { if (CanSelect) SetProperty(ref _isSelected, value); }
    }
    public string Status { get => _status; private set => SetProperty(ref _status, value); }
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
    }
}
