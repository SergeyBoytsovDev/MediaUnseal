using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using MediaUnseal.Models;
using MediaUnseal.Services;
namespace MediaUnseal.ViewModels;

public class MainViewModel : ViewModelBase
{
    private static LocalizationManager L => LocalizationManager.Active;
    private enum DependencyState { Checking, Ready, Missing, Error }

    private readonly FfmpegTools _tools;
    private readonly MediaAnalyzer _analyzer;
    private readonly TrackExporter _exporter;
    public ObservableCollection<string> Events { get; } = new();
    public ObservableCollection<UserLogEntry> UserEvents { get; } = new();
    public ObservableCollection<string> StreamSummaries { get; } = new();
    public ObservableCollection<ExportTrackViewModel> ExportTracks { get; } = new();
    public IReadOnlyList<AttachmentFile> AttachmentFiles { get; private set; } = [];
    private IReadOnlyDictionary<int, ulong> _attachmentStreamUids = new Dictionary<int, ulong>();
    private bool _syncingAttachmentSelection;

    public MainViewModel() : this(new FfmpegTools()) { }

    internal MainViewModel(FfmpegTools tools, MediaAnalyzer? analyzer = null, TrackExporter? exporter = null)
    {
        _tools = tools;
        PreviewLoader = (media, ffmpeg, token) => MediaPreview.LoadAsync(media, ffmpeg, token,
            message => _tools.Diagnostics.Record(DiagnosticLevel.Info, "Preview", message));
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
    private string _greeting = L.Get("InstallerCheckingTools");
    private string _ffmpegStatus = L.Get("InstallerCheckingBrief");
    private string _ffprobeStatus = L.Get("InstallerCheckingBrief");
    private string _ffmpegBriefStatus = L.Get("InstallerCheckingBrief");
    private string _ffprobeBriefStatus = L.Get("InstallerCheckingBrief");
    private string _knownBuildSource = "";
    private ToolStatus? _lastFFmpegToolStatus;
    private ToolStatus? _lastFFprobeToolStatus;
    private DependencyState _ffmpegState;
    private DependencyState _ffprobeState;
    private bool _installFailed;
    private string _fallbackNotice = "";
    private string? _fallbackNoticeKey;
    private string _message = "";
    private LocalizedMessage? _messageToken;
    private string? _installerNoticeOverrideKey;
    private string _installOption = "";
    private LocalizedMessage? _installOptionToken;
    private string _fileSelectionMessage = "";
    private string? _fileSelectionMessageKey;
    private bool _showDependencyOffer;
    private string? _ffprobePath;
    private string? _ffmpegPath;
    private MediaInfo? _selectedMedia;
    private string _analysisStatusKey = "MainNoFile";
    private bool _analysisFailed;
    private string _mediaSummary = "";
    private bool _isAnalyzing;
    private CancellationTokenSource? _analysisOperation;
    private CancellationTokenSource? _previewOperation;
    private Bitmap? _previewImage;
    private int _analysisGeneration;
    private int _activeAnalyses;
    private TaskCompletionSource? _analysesIdle;
    private string? _outputDirectory;
    private bool _outputDirectoryInputValid = true;
    private bool _isExporting;
    private string _exportStatus = L.Get("MainSelectFolderAndTracks");
    private Func<string>? _exportStatusFactory = () => L.Get("MainSelectFolderAndTracks");
    private double _exportProgress;
    private bool _exportProgressIndeterminate;
    private CancellationTokenSource? _exportOperation;
    private TaskCompletionSource? _exportIdle;
    private Stopwatch? _exportWatch;
    private DispatcherTimer? _exportTimer;
    private string _exportActiveMessage = "";
    private LocalizedMessage? _exportActiveToken;
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
                OnPropertyChanged(nameof(ProgressPercentDisplay));
                OnPropertyChanged(nameof(ProgressKnown));
                OnPropertyChanged(nameof(ProgressBarIndeterminate));
                OnPropertyChanged(nameof(InstallerNotice));
                OnPropertyChanged(nameof(HasInstallerNotice));
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
                OnPropertyChanged(nameof(HasGyanLicenseNotice));
                OnPropertyChanged(nameof(CanExtract));
                OnPropertyChanged(nameof(CanChooseFile));
                OnPropertyChanged(nameof(FooterStatus));
                OnPropertyChanged(nameof(InstallerNotice));
                OnPropertyChanged(nameof(HasInstallerNotice));
                if (value) ShowDependencyOffer = false;
            }
        }
    }
    public string Greeting { get => _greeting; private set => SetProperty(ref _greeting, value); }
    public string FFmpegStatus { get => _ffmpegStatus; private set => SetProperty(ref _ffmpegStatus, value); }
    public string FFprobeStatus { get => _ffprobeStatus; private set => SetProperty(ref _ffprobeStatus, value); }
    public string FFmpegBriefStatus { get => _ffmpegBriefStatus; private set => SetProperty(ref _ffmpegBriefStatus, value); }
    public string FFprobeBriefStatus { get => _ffprobeBriefStatus; private set => SetProperty(ref _ffprobeBriefStatus, value); }
    public string? FFmpegExecutablePath => _lastFFmpegToolStatus?.IsReady == true ? _lastFFmpegToolStatus.Path : null;
    public string? FFprobeExecutablePath => _lastFFprobeToolStatus?.IsReady == true ? _lastFFprobeToolStatus.Path : null;
    public string KnownBuildSource { get => _knownBuildSource; private set { if (SetProperty(ref _knownBuildSource, value)) OnPropertyChanged(nameof(HasKnownBuildSource)); } }
    public bool HasKnownBuildSource => !string.IsNullOrEmpty(KnownBuildSource);
    public bool FFmpegReady => _ffmpegState == DependencyState.Ready;
    public bool FFmpegMissing => _ffmpegState == DependencyState.Missing;
    public bool FFmpegError => _ffmpegState == DependencyState.Error;
    public bool FFprobeReady => _ffprobeState == DependencyState.Ready;
    public bool FFprobeMissing => _ffprobeState == DependencyState.Missing;
    public bool FFprobeError => _ffprobeState == DependencyState.Error;
    public bool InstallFailed { get => _installFailed; private set { if (SetProperty(ref _installFailed, value)) OnPropertyChanged(nameof(InstallButtonText)); } }
    public string InstallButtonText => L.Get(_tools.RequiredRepair != FfmpegTools.RepairSource.None
        ? "InstallerRepairFfmpeg" : InstallFailed ? "InstallerRetryInstall" : OperatingSystem.IsWindows()
        ? "InstallerInstallComponents" : "InstallerInstallationMethod");
    public string FallbackNotice { get => _fallbackNoticeKey is { } key ? L.Get(key) : _fallbackNotice; private set { _fallbackNoticeKey = null; if (SetProperty(ref _fallbackNotice, value)) { OnPropertyChanged(nameof(HasFallbackNotice)); OnPropertyChanged(nameof(InstallerNotice)); } } }
    public bool HasFallbackNotice => !string.IsNullOrEmpty(FallbackNotice);
    public string Message { get => _messageToken?.Resolve() ?? _message; private set { _messageToken = null; _installerNoticeOverrideKey = null; if (SetProperty(ref _message, value)) { OnPropertyChanged(nameof(InstallerNotice)); OnPropertyChanged(nameof(HasInstallerNotice)); } } }
    private string InstallerMessage => IsReady ? "" : _installerNoticeOverrideKey is { } key ? L.Get(key) : Message;
    private void SetLocalizedMessage(string key) => SetLocalizedMessage(new LocalizedMessage(key));
    private void SetLocalizedMessage(LocalizedMessage token)
    {
        Message = token.Resolve();
        _messageToken = token;
    }
    private void OverrideInstallerNotice(string key)
    {
        _installerNoticeOverrideKey = key;
        OnPropertyChanged(nameof(InstallerNotice));
        OnPropertyChanged(nameof(HasInstallerNotice));
    }
    private void SetFallbackNotice(string key) { FallbackNotice = L.Get(key); _fallbackNoticeKey = key; }

    internal void RefreshInstallerLocalization()
    {
        OnPropertyChanged(nameof(GyanLicenseNotice));
        OnPropertyChanged(nameof(InstallButtonText));
        OnPropertyChanged(nameof(Message));
        OnPropertyChanged(nameof(InstallOption));
        OnPropertyChanged(nameof(FallbackNotice));
        if (_lastFFmpegToolStatus is { } ffmpeg)
        {
            FFmpegStatus = Format(ffmpeg);
            SetDependencyStatus(ffmpeg, ffmpeg: true);
        }
        else FFmpegBriefStatus = L.Get("InstallerCheckingBrief");
        if (_lastFFprobeToolStatus is { } ffprobe)
        {
            FFprobeStatus = Format(ffprobe);
            SetDependencyStatus(ffprobe, ffmpeg: false);
        }
        else FFprobeBriefStatus = L.Get("InstallerCheckingBrief");
        if (_lastFFmpegToolStatus is { } a && _lastFFprobeToolStatus is { } b)
        {
            Greeting = IsReady ? L.Format("InstallerReadyVersions", Version(a), Version(b))
                : L.Get("InstallerToolsRequired");
            UpdateKnownBuildSource(a, b);
        }
        else Greeting = L.Get("InstallerCheckingTools");
        OnPropertyChanged(nameof(InstallerNotice));
        OnPropertyChanged(nameof(HasInstallerNotice));
    }
    public string InstallerNotice
    {
        get
        {
            if (!IsReady && _tools.RequiredRepair != FfmpegTools.RepairSource.None)
                return L.Get(_tools.RequiredRepair == FfmpegTools.RepairSource.WinGet
                    ? "ToolsWingetRepairNeeded" : "ToolsLocalRepairNeeded");
            if (IsReady || HasFallbackNotice) return InstallerMessage;
            var method = OperatingSystem.IsWindows() ? _tools.WinGetAvailable switch
            {
                true when _tools.NetworkCheckFailed => L.Get("InstallerWingetSourcesUnavailable"),
                true when _tools.OfferDirectDownload => L.Get("InstallerWingetFallbackAvailable"),
                true => L.Get("InstallerWingetAvailable"),
                false when !_tools.CanDirectDownload => L.Get("InstallerWingetUnavailableManual"),
                false when _tools.NetworkCheckFailed => L.Get("InstallerWingetAndSourceUnavailable"),
                false => L.Get("InstallerWingetUnavailableFallback"),
                null => ""
            } : InstallOption;
            return string.IsNullOrWhiteSpace(method) ? InstallerMessage
                : string.IsNullOrWhiteSpace(InstallerMessage) ? method : L.Format("InstallerNoticeWithMethod", InstallerMessage, method);
        }
    }
    public bool HasInstallerNotice => !string.IsNullOrWhiteSpace(InstallerNotice);
    public bool HasGyanLicenseNotice => OperatingSystem.IsWindows() && NeedsTools;
    public string GyanLicenseNotice => L.Get("InstallerGyanLicenseNotice");
    public string InstallOption { get => _installOptionToken?.Resolve() ?? _installOption; private set { _installOptionToken = null; if (SetProperty(ref _installOption, value)) { OnPropertyChanged(nameof(InstallerNotice)); OnPropertyChanged(nameof(HasInstallerNotice)); } } }
    private void SetInstallOption(string value, LocalizedMessage? token)
    {
        InstallOption = value;
        _installOptionToken = token;
    }
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
    public string AnalysisStatus => L.Get(_analysisStatusKey);
    private void SetAnalysisStatus(string key)
    {
        if (_analysisStatusKey == key) return;
        _analysisStatusKey = key;
        OnPropertyChanged(nameof(AnalysisStatus));
        OnPropertyChanged(nameof(FooterStatus));
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
        && _formatsValid && _outputDirectoryInputValid
        && SelectedMedia is not null && OutputDirectory is not null
        && ExportTracks.Any(t => t.IsSelected) && ExportTracks.Where(t => t.IsSelected)
            .All(t => t.IsAttachmentGroup || t.Plan is not null);
    internal string? FfmpegExecutable => _ffmpegPath;
    internal DiagnosticLog Diagnostics => _tools.Diagnostics;
    private bool _preserveMetadata = true;
    private bool _groupByLanguage;
    private bool _createSourceFolder;
    public bool PreserveMetadata { get => _preserveMetadata; set => SetProperty(ref _preserveMetadata, value); }
    public bool GroupByLanguage { get => _groupByLanguage; set => SetProperty(ref _groupByLanguage, value); }
    public bool CreateSourceFolder { get => _createSourceFolder; set => SetProperty(ref _createSourceFolder, value); }
    private bool _formatsValid = true;
    internal void SetFormatsValid(bool value)
    {
        _formatsValid = value;
        OnPropertyChanged(nameof(CanExtract));
    }
    public bool CanChooseFile => !IsBusy && !IsExporting;
    public bool CanClearFile => (HasMedia || IsAnalyzing) && !IsExporting;
    public bool HasSourceOrAnalysis => HasMedia || IsAnalyzing;
    public bool CanChooseFolder => !IsExporting;
    public bool CanOpenOutputDirectory => OutputDirectory is not null;
    public bool CanBulkSelect => SelectedMedia is not null && !IsExporting;
    public bool AllTracksSelected => ExportTracks.Any(t => t.CanSelect) &&
        ExportTracks.Where(t => t.CanSelect).All(t => t.IsSelected);
    public void ToggleAllTracks()
    {
        if (!CanBulkSelect) return;
        var select = !AllTracksSelected;
        foreach (var track in ExportTracks.Where(t => t.CanSelect)) track.IsSelected = select;
        OnPropertyChanged(nameof(AllTracksSelected));
    }
    public bool HasMedia => SelectedMedia is not null;
    public bool HasNoMedia => !HasMedia;
    public string TrackHeading => L.Format("MainTracksHeading", ExportTracks.Count);
    public string SelectedCountDisplay => L.Format("MainSelectedCount",
        ExportTracks.Count(t => t.IsSelected && (t.IsAttachmentGroup || t.Plan is not null)));
    public string ExportActionLabel => ExportTracks.Any(t => t.IsSelected && t.Conversion is not null)
        ? L.Get("MainProcess") : L.Get("MainExtract");
    public string FooterStatus => L.Get(IsBusy ? "MainCheckingDependencies" : !IsReady
        ? "MainNeedTools" : IsAnalyzing ? "MainAnalyzingFile"
        : IsExporting ? "MainProcessing" : _analysisFailed
        ? "MainAnalysisError" : "MainReady");
    public string MediaFileName => SelectedMedia?.Name ?? L.Get("MainNoFile");
    public string MediaFilePath => SelectedMedia?.FullPath ?? L.Get("MainChooseOrDropFile");
    public string ContainerDisplay => SelectedMedia is { } media ? VideoContainerDisplay.Format(media) : "—";
    public string DurationDisplay => SelectedMedia is { } media ? FormatDuration(media.Duration) : "—";
    public string SizeDisplay => SelectedMedia is { } media ? FormatByteSize(media.Size) : "—";
    public string SizeExactDisplay => SelectedMedia is { } media ? L.Format("MainBytesExact", media.Size) : "—";
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
    internal Func<MediaInfo, string, CancellationToken, Task<byte[]?>> PreviewLoader { get; set; }
    public string AudioSummaryDisplay => SelectedMedia is { } media
        ? FormatStreamSummary(media.Streams.Where(s => s.CodecType == "audio").ToArray()) : "—";
    public string SubtitleSummaryDisplay => SelectedMedia is { } media
        ? FormatStreamSummary(media.Streams.Where(s => s.CodecType == "subtitle").ToArray()) : "—";
    public void RefreshMainLocalization()
    {
        if (_exportStatusFactory is not null) ExportStatus = _exportStatusFactory();
        if (_fileSelectionMessageKey is not null) FileSelectionMessage = L.Get(_fileSelectionMessageKey);
        if (SelectedMedia is { } media)
        {
            MediaSummary = FormatMediaSummary(media);
            StreamSummaries.Clear();
            foreach (var stream in media.Streams) StreamSummaries.Add(Describe(stream));
        }
        OnPropertyChanged(nameof(AnalysisStatus));
        OnPropertyChanged(nameof(TrackHeading));
        OnPropertyChanged(nameof(SelectedCountDisplay));
        OnPropertyChanged(nameof(ExportActionLabel));
        OnPropertyChanged(nameof(FooterStatus));
        OnPropertyChanged(nameof(MediaFileName));
        OnPropertyChanged(nameof(MediaFilePath));
        OnPropertyChanged(nameof(SizeExactDisplay));
        OnPropertyChanged(nameof(AudioSummaryDisplay));
        OnPropertyChanged(nameof(SubtitleSummaryDisplay));
        foreach (var track in ExportTracks) track.RefreshMainLocalization();
    }
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
    private void SetLocalizedExportStatus(Func<string> factory)
    {
        _exportStatusFactory = factory;
        ExportStatus = factory();
    }
    public double ExportProgress { get => _exportProgress; private set => SetProperty(ref _exportProgress, value); }
    public bool ExportProgressIndeterminate { get => _exportProgressIndeterminate; private set => SetProperty(ref _exportProgressIndeterminate, value); }
    public double Progress { get => _progress; private set { if (SetProperty(ref _progress, value)) OnPropertyChanged(nameof(ProgressPercentDisplay)); } }
    public bool ProgressIndeterminate { get => _progressIndeterminate; private set { if (SetProperty(ref _progressIndeterminate, value)) { OnPropertyChanged(nameof(ProgressPercentDisplay)); OnPropertyChanged(nameof(ProgressKnown)); OnPropertyChanged(nameof(ProgressBarIndeterminate)); } } }
    public string ProgressPercentDisplay => IsBusy && !ProgressIndeterminate ? $"{Progress:0}%" : "";
    public bool ProgressKnown => IsBusy && !ProgressIndeterminate;
    public bool ProgressBarIndeterminate => IsBusy && ProgressIndeterminate;
    public void CancelOperation() => _operation?.Cancel();
    public void CancelAnalysis() { _analysisOperation?.Cancel(); _previewOperation?.Cancel(); }
    public void CancelExport() => _exportOperation?.Cancel();
    public Task WaitForIdleAsync() => _idle?.Task ?? Task.CompletedTask;
    public Task WaitForAnalysesAsync() => _analysesIdle?.Task ?? Task.CompletedTask;
    public Task WaitForExportAsync() => _exportIdle?.Task ?? Task.CompletedTask;

    public void ReportError(string message) => Message = message;
    public void ReportExportErrorKey(string key) => SetLocalizedExportStatus(() => L.Get(key));
    public void ReportExportError(string message)
    {
        _exportStatusFactory = null;
        ExportStatus = message;
    }
    public void ReportFileError(string message)
    {
        _fileSelectionMessageKey = null;
        FileSelectionMessage = message;
        _analysisFailed = true;
        SetAnalysisStatus("InstallerError");
    }
    public void ReportFileErrorKey(string key)
    {
        _fileSelectionMessageKey = key;
        FileSelectionMessage = L.Get(key);
        _analysisFailed = true;
        SetAnalysisStatus("InstallerError");
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
        AttachmentFiles = [];
        _attachmentStreamUids = new Dictionary<int, ulong>();
        MediaSummary = "";
        _analysisFailed = false;
        SetAnalysisStatus("MainNoFile");
        _fileSelectionMessageKey = null;
        FileSelectionMessage = "";
        SetLocalizedExportStatus(() => L.Get("MainSelectFileAndTracks"));
        ExportProgress = 0;
        ExportProgressIndeterminate = false;
        OnPropertyChanged(nameof(TrackHeading));
        OnPropertyChanged(nameof(SelectedCountDisplay));
        OnPropertyChanged(nameof(AllTracksSelected));
        OnPropertyChanged(nameof(SelectAllAudio));
        OnPropertyChanged(nameof(SelectAllSubtitles));
        OnPropertyChanged(nameof(CanExtract));
    }

    private bool AllSelected(string codecType)
    {
        var group = ExportTracks.Where(t => t.Stream.CodecType == codecType && t.Plan is not null).ToArray();
        return group.Length > 0 && group.All(t => t.IsSelected);
    }

    private void SetGroupSelected(string codecType, bool selected)
    {
        if (!CanBulkSelect) return;
        foreach (var track in ExportTracks.Where(t => t.Stream.CodecType == codecType && t.CanSelect && t.Plan is not null))
            track.IsSelected = selected;
        OnPropertyChanged(nameof(SelectAllAudio));
        OnPropertyChanged(nameof(SelectAllSubtitles));
        OnPropertyChanged(nameof(SelectedCountDisplay));
    }
    public bool SelectFile()
    {
        if (IsExporting)
        {
            _fileSelectionMessageKey = "MainStopBeforeFile";
            FileSelectionMessage = L.Get(_fileSelectionMessageKey);
            return false;
        }
        if (!IsReady)
        {
            _fileSelectionMessageKey = "MainInstallBeforeFile";
            FileSelectionMessage = L.Get(_fileSelectionMessageKey);
            ShowDependencyOffer = true;
            return false;
        }
        _fileSelectionMessageKey = null;
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
        AttachmentFiles = [];
        _attachmentStreamUids = new Dictionary<int, ulong>();
        OnPropertyChanged(nameof(CanExtract));
        OnPropertyChanged(nameof(TrackHeading));
        OnPropertyChanged(nameof(SelectedCountDisplay));
        OnPropertyChanged(nameof(SelectAllAudio));
        OnPropertyChanged(nameof(SelectAllSubtitles));
        MediaSummary = "";
        _analysisFailed = false;
        SetAnalysisStatus("MainAnalyzing");
        try
        {
            var media = await Task.Run(() => _analyzer.AnalyzeAsync(path, _ffprobePath, operation.Token), operation.Token);
            if (generation != _analysisGeneration) return;
            if (MatroskaAttachments.IsMatroska(media))
            {
                var attachmentWatch = Stopwatch.StartNew();
                try
                {
                    var probeReportsAttachments = MatroskaAttachments.ProbeReportsAttachments(media);
                    var navigation = new MatroskaAttachments.NavigationStats();
                    var entries = probeReportsAttachments
                        ? await Task.Run(() => MatroskaAttachments.ReadFileAsync(media, operation.Token, navigation), operation.Token)
                        : [];
                    if (generation != _analysisGeneration) return;
                    AttachmentFiles = entries.Select(a => new AttachmentFile(a.Uid, a.Name, a.Mime, a.Size,
                        a.Description)).ToArray();
                    _attachmentStreamUids = MatroskaAttachments.MatchStreamUids(media, entries);
                    if (_attachmentStreamUids.Count > 0)
                        media = media with { Streams = media.Streams.Select(stream =>
                            stream.Index is { } index && _attachmentStreamUids.TryGetValue(index, out var uid)
                                ? stream with { AttachmentUid = uid } : stream).ToArray() };
                    _tools.Diagnostics.Record(DiagnosticLevel.Info, "Analysis",
                        $"Matroska attachment search: {attachmentWatch.Elapsed.TotalMilliseconds:0.0} ms; " +
                        $"FFprobe candidates={probeReportsAttachments}; SeekHead={navigation.UsedSeekHead}; " +
                        $"elements={navigation.Elements}; read={navigation.SearchBytesRead} bytes; " +
                        $"found={entries.Count}; matched streams={_attachmentStreamUids.Count}.",
                        userMessageKey: entries.Count > 0 ? "LogAttachmentsFound" : null,
                        userMessageArgs: entries.Count > 0 ? [entries.Count] : null);
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
                {
                    _tools.Diagnostics.Record(DiagnosticLevel.Warn, "Analysis",
                        $"Matroska attachment search failed after {attachmentWatch.Elapsed.TotalMilliseconds:0.0} ms: " +
                        $"{ex.GetType().Name}; HResult={ex.HResult}.",
                        userMessageKey: "LogAttachmentsReadFailed");
                }
            }
            SelectedMedia = media;
            MediaSummary = FormatMediaSummary(media);
            foreach (var stream in media.Streams)
            {
                if (stream.CodecType == "attachment" && !stream.IsCoverArt) continue;
                var description = Describe(stream);
                StreamSummaries.Add(description);
                var track = new ExportTrackViewModel(stream, description, TrackExporter.Decide(media, stream));
                track.PropertyChanged += (_, args) =>
                {
                    if (args.PropertyName is nameof(ExportTrackViewModel.IsSelected) or nameof(ExportTrackViewModel.Conversion))
                    {
                        if (args.PropertyName == nameof(ExportTrackViewModel.IsSelected)
                            && !_syncingAttachmentSelection && track.Stream.Index is { } streamIndex
                            && _attachmentStreamUids.TryGetValue(streamIndex, out var uid))
                        {
                            _syncingAttachmentSelection = true;
                            try { AttachmentRow?.SetAttachmentSelected(uid, track.IsSelected); }
                            finally { _syncingAttachmentSelection = false; }
                        }
                        OnPropertyChanged(nameof(CanExtract));
                        OnPropertyChanged(nameof(SelectedCountDisplay));
                        OnPropertyChanged(nameof(AllTracksSelected));
                        OnPropertyChanged(nameof(ExportActionLabel));
                        OnPropertyChanged(nameof(SelectAllAudio));
                        OnPropertyChanged(nameof(SelectAllSubtitles));
                    }
                };
                ExportTracks.Add(track);
            }
            if (AttachmentFiles.Count > 0)
            {
                var group = ExportTrackViewModel.ForAttachments(AttachmentFiles);
                group.PropertyChanged += (_, args) =>
                {
                    if (args.PropertyName != nameof(ExportTrackViewModel.IsSelected)) return;
                    if (!_syncingAttachmentSelection)
                    {
                        _syncingAttachmentSelection = true;
                        try
                        {
                            foreach (var track in ExportTracks.Where(t => !t.IsAttachmentGroup
                                && t.Stream.Index is { } index && _attachmentStreamUids.ContainsKey(index)))
                                track.IsSelected = group.SelectedAttachmentUids.Contains(
                                    _attachmentStreamUids[track.Stream.Index!.Value]);
                        }
                        finally { _syncingAttachmentSelection = false; }
                    }
                    OnPropertyChanged(nameof(CanExtract));
                    OnPropertyChanged(nameof(SelectedCountDisplay));
                    OnPropertyChanged(nameof(AllTracksSelected));
                };
                ExportTracks.Add(group);
            }
            OnPropertyChanged(nameof(CanExtract));
            OnPropertyChanged(nameof(TrackHeading));
            OnPropertyChanged(nameof(SelectedCountDisplay));
            OnPropertyChanged(nameof(AllTracksSelected));
            OnPropertyChanged(nameof(SelectAllAudio));
            OnPropertyChanged(nameof(SelectAllSubtitles));
            SetAnalysisStatus("MainDone");
            if (_ffmpegPath is { } ffmpeg) _ = LoadPreviewAsync(media, ffmpeg, generation, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            if (generation == _analysisGeneration) SetAnalysisStatus("MainAnalysisCancelled");
        }
        catch (Exception ex)
        {
            if (generation == _analysisGeneration)
            {
                _analysisFailed = true;
                SetAnalysisStatus("InstallerError");
                _tools.Diagnostics.Record(DiagnosticLevel.Error, "Analysis",
                    "File may be damaged or unsupported.",
                    $"FFprobe: {ex.GetType().Name}; {ex.Message}",
                    userMessageKey: "LogAnalysisFailed");
                _fileSelectionMessageKey = ex switch
                {
                    FileNotFoundException => "MainFileNotFound",
                    UnauthorizedAccessException => "MainFileAccessDenied",
                    Win32Exception => "MainProbeLaunchFailed",
                    IOException => "MainFileReadFailed",
                    _ => "MainFileDamaged"
                };
                FileSelectionMessage = L.Get(_fileSelectionMessageKey);
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
        operation.CancelAfter(TimeSpan.FromSeconds(30));
        var previewWatch = Stopwatch.StartNew();
        Stopwatch? decodeWatch = null;
        var outcome = "no image obtained";
        var userOutcomeKey = "LogPreviewUnavailable";
        try
        {
            var bytes = await PreviewLoader(media, ffmpegPath, operation.Token);
            if (generation != _analysisGeneration || operation.IsCancellationRequested)
            { outcome = "cancelled or replaced by another file"; userOutcomeKey = "LogPreviewCancelled"; return; }
            if (bytes is null) return;
            decodeWatch = Stopwatch.StartNew();
            await using var stream = new MemoryStream(bytes, writable: false);
            var bitmap = new Bitmap(stream);
            _tools.Diagnostics.Record(DiagnosticLevel.Info, "Preview",
                $"Image decoding: {decodeWatch.Elapsed.TotalMilliseconds:0} ms; succeeded.");
            if (generation == _analysisGeneration && !operation.IsCancellationRequested)
            { PreviewImage = bitmap; outcome = "image obtained"; userOutcomeKey = "LogPreviewReady"; }
            else { bitmap.Dispose(); userOutcomeKey = "LogPreviewCancelled"; }
        }
        catch (OperationCanceledException)
        {
            var replaced = cancellationToken.IsCancellationRequested || generation != _analysisGeneration;
            outcome = replaced ? "cancelled on file change" : "30 s timeout";
            userOutcomeKey = replaced ? "LogPreviewCancelled" : "LogPreviewUnavailable";
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException
            or ArgumentException or NotSupportedException or OutOfMemoryException)
        {
            outcome = "decoding or reading error: " + ex.GetType().Name;
            if (decodeWatch is not null)
                _tools.Diagnostics.Record(DiagnosticLevel.Info, "Preview",
                    $"Image decoding: {decodeWatch.Elapsed.TotalMilliseconds:0} ms; " +
                    $"{ex.GetType().Name}.");
            // A missing or corrupt preview must not change the analyzed media state.
        }
        finally
        {
            _tools.Diagnostics.Record(DiagnosticLevel.Info, "Preview",
                $"Image preparation: {previewWatch.Elapsed.TotalMilliseconds:0} ms; " +
                $"result: {outcome}.",
                userMessageKey: generation == _analysisGeneration ? userOutcomeKey : null,
                userSuccess: userOutcomeKey == "LogPreviewReady");
            if (_previewOperation == operation) _previewOperation = null;
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
            SetLocalizedExportStatus(() => L.Get("MainInvalidOutputFolder"));
            return;
        }
        if (!Directory.Exists(normalized))
        {
            SetLocalizedExportStatus(() => L.Get("MainOutputFolderMissingChoose"));
            return;
        }
        // Keep the trailing separator for drive and filesystem roots.
        OutputDirectory = normalized;
        SetOutputDirectoryInputValid(true);
        SetLocalizedExportStatus(() => L.Get("MainOutputFolderSelected"));
    }

    public void SetOutputDirectoryInputValid(bool valid)
    {
        if (_outputDirectoryInputValid == valid) return;
        _outputDirectoryInputValid = valid;
        OnPropertyChanged(nameof(CanExtract));
    }

    public bool TrySetUserOutputDirectory(string? path)
    {
        if (IsExporting) return false;
        if (string.IsNullOrWhiteSpace(path))
        {
            ReportExportErrorKey("MainInvalidOutputFolder");
            _tools.Diagnostics.Record(DiagnosticLevel.Warn, "Output folder", "Output folder rejected: empty path.");
            return false;
        }
        string normalized;
        try
        {
            if (OperatingSystem.IsWindows() && path.Length == 2 && path[1] == ':')
                path += Path.DirectorySeparatorChar;
            normalized = Path.GetFullPath(path);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            ReportExportErrorKey("MainInvalidOutputFolder");
            _tools.Diagnostics.Record(DiagnosticLevel.Warn, "Output folder",
                $"Output folder rejected: invalid path; {ex.GetType().Name}; code {ex.HResult}.");
            return false;
        }
        if (!Directory.Exists(normalized))
        {
            ReportExportErrorKey("MainOutputFolderMissingChoose");
            _tools.Diagnostics.Record(DiagnosticLevel.Warn, "Output folder", "Output folder rejected: directory not found.");
            return false;
        }
        try
        {
            var probePath = Path.Combine(normalized, ".mediaunseal-" + Guid.NewGuid().ToString("N") + ".tmp");
            using var probe = new FileStream(probePath, FileMode.CreateNew, FileAccess.Write,
                FileShare.None, 1, FileOptions.DeleteOnClose);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            ReportExportErrorKey("MainOutputFolderAccessDenied");
            _tools.Diagnostics.Record(DiagnosticLevel.Warn, "Output folder",
                $"Output folder rejected: write probe failed; {ex.GetType().Name}; code {ex.HResult}.",
                userMessageKey: "LogOutputFolderAccessDenied");
            return false;
        }
        var changed = !string.Equals(OutputDirectory, normalized, OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
        OutputDirectory = normalized;
        SetOutputDirectoryInputValid(true);
        SetLocalizedExportStatus(() => L.Get("MainOutputFolderSelected"));
        if (changed)
            _tools.Diagnostics.Record(DiagnosticLevel.Info, "Output folder", "Output folder changed after write probe.",
                userMessageKey: "LogOutputFolderChanged", userSuccess: true);
        return true;
    }

    public ExportTrackViewModel? AttachmentRow => ExportTracks.FirstOrDefault(t => t.IsAttachmentGroup);

    public void SetAttachmentSelection(IEnumerable<ulong> uids) => AttachmentRow?.SetAttachmentSelection(uids);

    public async Task StartExportAsync(CancellationToken cancellationToken,
        IReadOnlyCollection<ulong>? onlyAttachments = null)
    {
        if (IsExporting) return;
        if ((onlyAttachments is null ? !CanExtract : !IsReady || IsBusy || IsAnalyzing
                || AttachmentFiles.Count == 0 || onlyAttachments.Count == 0)
            || SelectedMedia is null || OutputDirectory is null || _ffmpegPath is null)
        {
            SetLocalizedExportStatus(() => L.Get("MainExportRequirements"));
            return;
        }
        var media = SelectedMedia;
        var selected = onlyAttachments is null
            ? ExportTracks.Where(t => t.IsSelected && !t.IsAttachmentGroup && t.Plan is not null).ToArray()
            : [];
        var chosenUids = onlyAttachments ?? AttachmentRow?.SelectedAttachmentUids ?? [];
        if (onlyAttachments is not null)
            _tools.Diagnostics.Record(DiagnosticLevel.Info, "Export",
                $"Extract all Matroska attachments: {chosenUids.Count} selected.",
                userMessageKey: "LogAttachmentsExportAll", userMessageArgs: [chosenUids.Count]);
        var alreadySelected = selected.Where(t => t.Conversion is null && t.Stream.Index is { } index
                && _attachmentStreamUids.ContainsKey(index))
            .Select(t => _attachmentStreamUids[t.Stream.Index!.Value]).ToHashSet();
        var plans = selected.Select(t => t.Plan!).Concat(AttachmentFiles.Where(file => chosenUids.Contains(file.Uid)
                && !alreadySelected.Contains(file.Uid)).Select((file, ordinal) =>
            new TrackExportPlan(new MediaStreamInfo(-1 - ordinal, "attachment_group", null, null,
                null, null, null, null, null, null, null, null, null, null, null, null, null,
                SubtitleKind.Unknown, new Dictionary<string, string>(), new Dictionary<string, int?>(),
                null, VisualKind.Unknown, null, false), "bin", "attachment", false, true,
                AttachmentFile: file))).ToArray();
        using var operation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _exportOperation = operation;
        _exportIdle = new(TaskCreationOptions.RunContinuationsAsynchronously);
        IsExporting = true;
        _exportWatch = Stopwatch.StartNew();
        _exportActiveToken = new("MainPreparingExport");
        _exportActiveMessage = _exportActiveToken.Resolve();
        _exportTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _exportTimer.Tick += (_, _) =>
        {
            if (IsExporting && _exportWatch is not null)
                SetLocalizedExportStatus(() => L.Format("MainStatusWithTime",
                    _exportActiveToken?.Resolve() ?? _exportActiveMessage, TrackExporter.FormatElapsed(_exportWatch.Elapsed)));
        };
        _exportTimer.Start();
        foreach (var track in ExportTracks) track.SetLocked(true);
        foreach (var track in selected) track.SetResult(ExportState.Pending, L.Get("MainAwaitingExtraction"), null,
            new("MainAwaitingExtraction"));
        ExportProgress = 0;
        ExportProgressIndeterminate = true;
        SetLocalizedExportStatus(() => L.Get("MainPreparingExport"));
        try
        {
            var updates = new Progress<ExportUpdate>(update =>
            {
                if (_exportOperation != operation || !IsExporting) return;
                var track = selected.FirstOrDefault(t => t.Stream.Index == update.StreamIndex)
                    ?? (update.StreamIndex < 0 ? AttachmentRow : null);
                track?.SetResult(update.State, update.Message, null, update.LocalizedMessage);
                ExportProgressIndeterminate = update.OverallPercent is null && ExportProgress == 0;
                if (update.OverallPercent is { } value) ExportProgress = Math.Max(ExportProgress, value);
                var ordinal = Math.Clamp(update.Processed + (update.State == ExportState.Completed ? 0 : 1), 1, update.Total);
                _exportActiveMessage = update.State == ExportState.Running
                    ? (update.StreamIndex < 0 ? update.Message : update.GroupCount > 1
                        ? L.Format("MainGroupedProgress", update.Message, ordinal, Math.Min(update.Total, ordinal + update.GroupCount - 1), update.Total)
                        : L.Format("MainStreamProgress", update.StreamIndex, ordinal, update.Total)) +
                        (update.CurrentPercent is { } percent ? $" · {percent:0.#}%" : "")
                    : update.Message;
                _exportActiveToken = update.State == ExportState.Running
                    ? update.StreamIndex < 0 ? update.LocalizedMessage
                        : update.GroupCount > 1
                        ? new LocalizedMessage("MainGroupedProgress", (object?)update.LocalizedMessage ?? update.Message,
                            ordinal, Math.Min(update.Total, ordinal + update.GroupCount - 1), update.Total)
                        : new LocalizedMessage("MainStreamProgress", update.StreamIndex, ordinal, update.Total)
                    : update.LocalizedMessage;
                if (update.State == ExportState.Running && update.CurrentPercent is { } currentPercent)
                    _exportActiveToken = new("MainProgressWithPercent", _exportActiveToken!, currentPercent);
                SetLocalizedExportStatus(() => L.Format("MainStatusWithTime",
                    _exportActiveToken?.Resolve() ?? _exportActiveMessage, TrackExporter.FormatElapsed(_exportWatch!.Elapsed)));
            });
            var options = new ExportOptions(PreserveMetadata, GroupByLanguage, CreateSourceFolder);
            var result = await Task.Run(() => _exporter.ExportAsync(media, plans, OutputDirectory,
                _ffmpegPath, updates, operation.Token, options), operation.Token);
            _exportOperation = null;
            foreach (var trackResult in result.Tracks)
                (selected.FirstOrDefault(t => t.Stream.Index == trackResult.StreamIndex)
                    ?? (trackResult.StreamIndex < 0 ? AttachmentRow : null))
                    ?.SetResult(trackResult.State, trackResult.Message, trackResult.OutputPath, trackResult.LocalizedMessage);
            ExportProgressIndeterminate = false;
            ExportProgress = result.Success ? 100 : Math.Max(ExportProgress, Math.Min(99,
                result.Tracks.Count(t => t.State is ExportState.Completed or ExportState.Failed) * 100.0 / plans.Length));
            var completionKey = result.Success ? "MainExportComplete"
                : result.WasCancelled ? "MainExportCancelled" : "MainExportPartial";
            var elapsed = TrackExporter.FormatElapsed(result.Elapsed);
            SetLocalizedExportStatus(() => L.Format("MainStatusWithTotalTime", L.Get(completionKey), elapsed));
        }
        catch (OperationCanceledException)
        {
            ExportProgressIndeterminate = false;
            foreach (var track in selected) track.SetResult(ExportState.Cancelled, L.Get("MainExtractionCancelled"), null,
                new("MainExtractionCancelled"));
            var elapsed = TrackExporter.FormatElapsed(_exportWatch!.Elapsed);
            SetLocalizedExportStatus(() => L.Format("MainStatusWithTotalTime", L.Get("MainExportCancelled"), elapsed));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            ExportProgressIndeterminate = false;
            foreach (var track in selected) track.SetResult(ExportState.Failed, L.Get("MainExportStartFailed"), null,
                new("MainExportStartFailed"));
            var reasonKey = ex switch
            {
                FileNotFoundException => "MainSourceMissing",
                DirectoryNotFoundException => "MainOutputFolderMissing",
                UnauthorizedAccessException => "MainOutputFolderAccessDenied",
                _ => "MainExportStartFailed"
            };
            var reason = L.Get(reasonKey);
            _tools.Diagnostics.Record(DiagnosticLevel.Error, "Export",
                $"Export could not start: {ex.GetType().Name}.",
                $"Operation: export start; source: {media.FullPath}; directory: {OutputDirectory}; error: {ex.GetType().Name}; code: {ex.HResult}.",
                userMessageKey: reasonKey);
            var elapsed = TrackExporter.FormatElapsed(_exportWatch!.Elapsed);
            SetLocalizedExportStatus(() => L.Format("MainStatusWithTotalTime", L.Get(reasonKey), elapsed));
        }
        catch (Exception ex)
        {
            ExportProgressIndeterminate = false;
            foreach (var track in selected) track.SetResult(ExportState.Failed, L.Get("MainExtractionError"), null,
                new("MainExtractionError"));
            var elapsed = TrackExporter.FormatElapsed(_exportWatch!.Elapsed);
            SetLocalizedExportStatus(() => L.Format("MainStatusWithTotalTime", L.Get("MainExportErrorAdvice"), elapsed));
            _tools.Diagnostics.Record(DiagnosticLevel.Error, "Export", "Failed to start extraction.",
                $"Operation: export start; source: {media.FullPath}; directory: {OutputDirectory}; error: {ex.GetType().Name}; code: {ex.HResult}.",
                userMessageKey: "MainExportStartFailed");
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
        return L.Format("MainMediaSummary", media.Name, media.FullPath, media.Size,
            media.Container ?? "—", onlyStillImages ? "—" : FormatDuration(media.Duration),
            FormatBitRate(media.BitRate), media.VideoCount, media.ImageCount, media.AnimationCount,
            media.AudioCount, media.SubtitleCount, media.UnknownVisualCount, media.Streams.Count);
    }

    internal static string Describe(MediaStreamInfo stream)
    {
        var details = new System.Collections.Generic.List<string>();
        var visual = stream.CodecType == "video" || stream.VisualKind is VisualKind.Image or VisualKind.Animation;
        if (visual)
            details.Add(stream.Width is { } width && stream.Height is { } height ? $"{width}×{height}" : L.Get("MainDescriptionResolutionUnknown"));
        if (stream.VisualKind is VisualKind.Video or VisualKind.Animation)
        {
            details.Add(stream.FrameRate is > 0 and <= 1000 ? L.Format("MainDescriptionFps", stream.FrameRate.Value) : L.Get("MainDescriptionFpsUnknown"));
            details.Add(L.Format("MainDescriptionDuration", FormatDuration(stream.Duration)));
        }
        if (stream.CodecType == "audio")
        {
            details.Add(stream.Channels is { } channels ? L.Format("MainDescriptionChannels", channels) : L.Get("MainDescriptionChannelsUnknown"));
            details.Add(stream.SampleRate is { } sampleRate ? L.Format("MainDescriptionSampleRate", sampleRate) : L.Get("MainDescriptionSampleRateUnknown"));
        }
        if (stream.Language is { } language) details.Add(language);
        if (stream.Title is { } title) details.Add(title.Length > 80 ? title[..80] + "…" : title);
        if (stream.SubtitleKind != SubtitleKind.Unknown) details.Add(L.Get(stream.SubtitleKind == SubtitleKind.Text ? "MainDescriptionText" : "MainDescriptionGraphic"));
        var type = stream.VisualKind switch
        {
            VisualKind.Video => L.Get("MainTypeVideo"),
            VisualKind.Image => L.Get(stream.IsCoverArt ? "MainDescriptionCover" : "MainTypeImage"),
            VisualKind.Animation => L.Get("MainTypeAnimation"),
            _ when stream.CodecType == "video" => L.Get("MainDescriptionUnknownVisual"),
            _ => stream.CodecType switch
            {
                "audio" => L.Get("MainTypeAudio"), "subtitle" => L.Get("MainTypeSubtitle"),
                "attachment" => L.Get("MainTypeAttachment"),
                null => L.Get("MainDescriptionUnknownType"), _ => stream.CodecType
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
        >= 1L << 30 => L.Format("MainSizeGiB", bytes / (double)(1L << 30)),
        >= 1L << 20 => L.Format("MainSizeMiB", bytes / (double)(1L << 20)),
        >= 1L << 10 => L.Format("MainSizeKiB", bytes / (double)(1L << 10)),
        _ => L.Format("MainSizeBytes", bytes)
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
        return L.Format("MainStreamSummary", FormatCount(streams.Length,
            L.Get("MainTrackOne"), L.Get("MainTrackFew"), L.Get("MainTrackMany")), codecs);
    }

    internal static string FormatBitRate(long? value) => value switch
    {
        null => "—",
        >= 1_000_000 => L.Format("MainBitrateMbps", value.Value / 1_000_000.0),
        >= 1_000 => L.Format("MainBitrateKbps", value.Value / 1_000.0),
        _ => L.Format("MainBitrateBps", value.Value)
    };

    public async Task CheckAsync(CancellationToken cancellationToken)
    {
        if (IsBusy) return;
        using var operation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _operation = operation;
        cancellationToken = operation.Token;
        ProgressIndeterminate = true;
        Progress = 0;
        IsBusy = true;
        try
        {
            try { await RefreshAsync(cancellationToken); }
            catch (OperationCanceledException) { throw; }
            catch
            {
                IsReady = false;
                KnownBuildSource = "";
                SetDependencyStatus(new ToolStatus("ffmpeg", false, L.Get("InstallerCheckError")), ffmpeg: true);
                SetDependencyStatus(new ToolStatus("ffprobe", false, L.Get("InstallerCheckError")), ffmpeg: false);
                throw;
            }
            if (!IsReady)
                SetInstallOption(await Task.Run(() => _tools.GetInstallOptionAsync(cancellationToken), cancellationToken),
                    _tools.LastInstallOptionMessage);
            FallbackNotice = "";
        }
        finally { _operation = null; IsBusy = false; }
    }

    public async Task InstallAsync(CancellationToken cancellationToken, bool? directDownload = null)
    {
        if (IsBusy || IsReady) return;
        if (!OperatingSystem.IsWindows())
        {
            SetLocalizedMessage("InstallerAutoInstallUnavailable");
            return;
        }
        var useDirectDownload = _tools.RequiredRepair switch
        {
            FfmpegTools.RepairSource.Local => true,
            FfmpegTools.RepairSource.WinGet => false,
            _ => directDownload ?? _tools.OfferDirectDownload
        };
        if (useDirectDownload && !_tools.CanDirectDownload)
        {
            SetLocalizedMessage("InstallerDirectDownloadWindowsOnly");
            return;
        }

        using var operation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _operation = operation;
        cancellationToken = operation.Token;
        InstallFailed = false;
        FallbackNotice = "";
        ProgressIndeterminate = true;
        Progress = 0;
        IsBusy = true;
        var acceptProgress = true;
        try
        {
            var progress = new Progress<InstallProgress>(value =>
            {
                if (!acceptProgress || _operation != operation || operation.IsCancellationRequested) return;
                if (value.LocalizedMessage is { } token) SetLocalizedMessage(token);
                else Message = value.Message;
                ProgressIndeterminate = !value.Percent.HasValue;
                Progress = value.Percent ?? 0;
            });
            SetLocalizedMessage(useDirectDownload ? "InstallerPreparingDirectDownload" : "InstallerInstallingWithWinget");
            var result = useDirectDownload
                ? await Task.Run(() => _tools.DownloadWindowsAsync(progress, cancellationToken), cancellationToken)
                : await Task.Run(() => _tools.InstallOnWindowsAsync(cancellationToken), cancellationToken);
            if (!result.Success && !useDirectDownload && _tools.OfferDirectDownload && _tools.CanDirectDownload)
            {
                SetFallbackNotice(_tools.WinGetAvailable == false
                    ? "InstallerDirectFallbackNoWinget" : "InstallerDirectFallbackFailedWinget");
                SetInstallOption(L.Get("InstallerDirectDownloadLocation"), new("InstallerDirectDownloadLocation"));
                SetLocalizedMessage("InstallerPreparingDirectDownload");
                result = await Task.Run(() => _tools.DownloadWindowsAsync(progress, cancellationToken), cancellationToken);
            }
            acceptProgress = false;
            await RefreshAsync(cancellationToken, new("InstallerCheckingAfterInstall"));
            if (!IsReady)
                SetInstallOption(await Task.Run(() => _tools.GetInstallOptionAsync(cancellationToken), cancellationToken),
                    _tools.LastInstallOptionMessage);
            if (result.Success && !IsReady) SetLocalizedMessage("InstallerVerificationFailed");
            else if (result.LocalizedMessage is { } token) SetLocalizedMessage(token);
            else Message = result.Message;
            InstallFailed = !IsReady;
        }
        catch (OperationCanceledException)
        {
            SetLocalizedMessage("InstallerInstallCancelled");
            throw;
        }
        catch
        {
            InstallFailed = true;
            throw;
        }
        finally { acceptProgress = false; _operation = null; IsBusy = false; }
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
                var missing = ManualPathMissing(path);
                if (name == "ffmpeg" && !FFmpegReady)
                    SetDependencyStatus(new ToolStatus(name, false, error, IsMissing: missing)
                    { LocalizedDetails = _tools.ManualPathErrorMessage }, ffmpeg: true);
                if (name == "ffprobe" && !FFprobeReady)
                    SetDependencyStatus(new ToolStatus(name, false, error, IsMissing: missing)
                    { LocalizedDetails = _tools.ManualPathErrorMessage }, ffmpeg: false);
                if (_tools.ManualPathErrorMessage is { } token) SetLocalizedMessage(token);
                else Message = error;
                if (missing) OverrideInstallerNotice("InstallerManualFileMissing");
                return;
            }
            await RefreshAsync(cancellationToken);
        }
        finally { _operation = null; IsBusy = false; }
    }

    private static bool ManualPathMissing(string path)
    {
        try { _ = File.GetAttributes(path); return false; }
        catch (FileNotFoundException) { return true; }
        catch (DirectoryNotFoundException) { return true; }
        catch { return false; }
    }

    private async Task RefreshAsync(CancellationToken cancellationToken, LocalizedMessage? stage = null)
    {
        if (stage is not null || string.IsNullOrWhiteSpace(Message))
        {
            if (stage is null) SetLocalizedMessage("InstallerCheckingTools");
            else SetLocalizedMessage(stage);
        }
        var status = await Task.Run(() => _tools.CheckAsync(cancellationToken), cancellationToken);
        FFmpegStatus = Format(status.FFmpeg);
        FFprobeStatus = Format(status.FFprobe);
        SetDependencyStatus(status.FFmpeg, ffmpeg: true);
        SetDependencyStatus(status.FFprobe, ffmpeg: false);
        UpdateKnownBuildSource(status.FFmpeg, status.FFprobe);
        IsReady = status.IsReady;
        OnPropertyChanged(nameof(InstallButtonText));
        _ffmpegPath = status.IsReady ? status.FFmpeg.Path : null;
        _ffprobePath = status.IsReady ? status.FFprobe.Path : null;
        if (!status.IsReady) { CancelAnalysis(); CancelExport(); }
        if (IsReady) { InstallOption = ""; InstallFailed = false; }
        Greeting = IsReady
            ? L.Format("InstallerReadyVersions", Version(status.FFmpeg), Version(status.FFprobe))
            : L.Get("InstallerToolsRequired");
        if (_tools.SettingsErrorMessage is { } settingsError) SetLocalizedMessage(settingsError);
        else if (_tools.SettingsError is { } error) Message = error;
        else SetLocalizedMessage(IsReady ? "InstallerBothReady" : _tools.RequiredRepair switch
        {
            FfmpegTools.RepairSource.WinGet => "ToolsWingetRepairNeeded",
            FfmpegTools.RepairSource.Local => "ToolsLocalRepairNeeded",
            _ => "InstallerMissingPrompt"
        });
        if (!IsReady && _tools.RequiredRepair == FfmpegTools.RepairSource.None
            && (FFmpegError || FFprobeError) && _tools.SettingsError is null)
            OverrideInstallerNotice("InstallerToolCheckFailed");
    }

    private void UpdateKnownBuildSource(ToolStatus ffmpeg, ToolStatus ffprobe)
    {
        var ffmpegFromGyan = ffmpeg.IsReady && ffmpeg.Details.Contains("gyan.dev", StringComparison.OrdinalIgnoreCase);
        var ffprobeFromGyan = ffprobe.IsReady && ffprobe.Details.Contains("gyan.dev", StringComparison.OrdinalIgnoreCase);
        KnownBuildSource = (ffmpegFromGyan, ffprobeFromGyan) switch
        {
            (true, true) => L.Get("InstallerBuildSourceBoth"),
            (true, false) => L.Get("InstallerBuildSourceFfmpeg"),
            (false, true) => L.Get("InstallerBuildSourceFfprobe"),
            _ => ""
        };
    }

    private void SetDependencyStatus(ToolStatus? status, bool ffmpeg)
    {
        if (ffmpeg) _lastFFmpegToolStatus = status;
        else _lastFFprobeToolStatus = status;
        OnPropertyChanged(ffmpeg ? nameof(FFmpegExecutablePath) : nameof(FFprobeExecutablePath));
        var state = status is null ? DependencyState.Checking : status.IsReady ? DependencyState.Ready
            : status.IsMissing
                ? DependencyState.Missing : DependencyState.Error;
        var summary = state switch
        {
            DependencyState.Ready => L.Format("InstallerFoundVersion", Version(status!)),
            DependencyState.Missing => L.Get("InstallerNotFound"),
            DependencyState.Error => L.Get("InstallerError"),
            _ => L.Get("InstallerCheckingBrief")
        };
        if (ffmpeg)
        {
            _ffmpegState = state;
            FFmpegBriefStatus = summary;
            OnPropertyChanged(nameof(FFmpegReady));
            OnPropertyChanged(nameof(FFmpegMissing));
            OnPropertyChanged(nameof(FFmpegError));
        }
        else
        {
            _ffprobeState = state;
            FFprobeBriefStatus = summary;
            OnPropertyChanged(nameof(FFprobeReady));
            OnPropertyChanged(nameof(FFprobeMissing));
            OnPropertyChanged(nameof(FFprobeError));
        }
        OnPropertyChanged(nameof(InstallerNotice));
        OnPropertyChanged(nameof(HasInstallerNotice));
    }

    private static string Format(ToolStatus status) =>
        status.IsReady ? $"{status.Name}: {status.Details}\n{status.Path}"
            : $"{status.Name}: {status.LocalizedDetails?.Resolve() ?? status.Details}";

    private static string Version(ToolStatus status) => status.Details.Split(' ', StringSplitOptions.RemoveEmptyEntries)[2];
}

public sealed class ExportTrackViewModel : ViewModelBase
{
    private static LocalizationManager L => LocalizationManager.Active;
    private static readonly IBrush ReadyBackground = new SolidColorBrush(Color.Parse("#175541"));
    private static readonly IBrush RunningBackground = new SolidColorBrush(Color.Parse("#19477B"));
    private static readonly IBrush ErrorBackground = new SolidColorBrush(Color.Parse("#633042"));
    private static readonly IBrush MutedBackground = new SolidColorBrush(Color.Parse("#344359"));
    public ExportTrackViewModel(MediaStreamInfo stream, string caption, ExportDecision decision)
    {
        Stream = stream;
        Caption = caption;
        Decision = decision;
        _statusFactory = () => decision.Plan is null ? (CanConvert ? L.Get("MainConversionAfterSetup")
            : L.Format("MainUnavailableReason", decision.LocalizedReason?.Resolve() ?? decision.Reason)) : L.Get("MainNotSelected");
        _status = _statusFactory();
    }

    private IReadOnlyList<AttachmentFile> _attachmentFiles = [];
    private readonly HashSet<ulong> _selectedAttachmentUids = new();
    public bool IsAttachmentGroup => _attachmentFiles.Count > 0;
    public IReadOnlyCollection<ulong> SelectedAttachmentUids => _selectedAttachmentUids;
    public bool AllAttachmentsSelected => IsAttachmentGroup
        && _selectedAttachmentUids.Count == _attachmentFiles.Count;
    public bool PartAttachmentsSelected => _selectedAttachmentUids.Count > 0 && !AllAttachmentsSelected;
    public string AttachmentStateGlyph => _selectedAttachmentUids.Count == 0 ? "○"
        : _selectedAttachmentUids.Count == _attachmentFiles.Count ? "●" : "◐";
    public static ExportTrackViewModel ForAttachments(IReadOnlyList<AttachmentFile> files)
    {
        var row = new ExportTrackViewModel(new MediaStreamInfo(null, "attachment_group", null, null,
            null, null, null, null, null, null, null, null, null, null, null, null, null,
            SubtitleKind.Unknown, new Dictionary<string, string>(), new Dictionary<string, int?>(),
            null, VisualKind.Unknown, null, false), "", new ExportDecision(null, null));
        row._attachmentFiles = files;
        row.SetLocalizedStatus(() => L.Format("MainAttachmentCount", files.Count));
        return row;
    }

    public void ToggleAttachments() => SetAttachmentSelection(
        _selectedAttachmentUids.Count == _attachmentFiles.Count ? [] : _attachmentFiles.Select(f => f.Uid));

    public void SetAttachmentSelected(ulong uid, bool selected)
    {
        if (!IsAttachmentGroup || _locked || !_attachmentFiles.Any(f => f.Uid == uid)) return;
        if (selected ? _selectedAttachmentUids.Add(uid) : _selectedAttachmentUids.Remove(uid))
            NotifyAttachmentSelection();
    }

    public void SetAttachmentSelection(IEnumerable<ulong> uids)
    {
        if (!IsAttachmentGroup || _locked) return;
        var allowed = _attachmentFiles.Select(f => f.Uid).ToHashSet();
        var next = uids.Where(allowed.Contains).ToHashSet();
        if (_selectedAttachmentUids.SetEquals(next)) return;
        _selectedAttachmentUids.Clear();
        _selectedAttachmentUids.UnionWith(next);
        NotifyAttachmentSelection();
    }

    private void NotifyAttachmentSelection()
    {
        _isSelected = _selectedAttachmentUids.Count > 0;
        OnPropertyChanged(nameof(IsSelected));
        OnPropertyChanged(nameof(AttachmentStateGlyph));
        OnPropertyChanged(nameof(AllAttachmentsSelected));
        OnPropertyChanged(nameof(PartAttachmentsSelected));
        OnPropertyChanged(nameof(StatusLabel));
        OnPropertyChanged(nameof(Status));
    }

    private bool _isSelected;
    private bool _locked;
    private ExportState _state = ExportState.Pending;
    private string _status;
    private Func<string>? _statusFactory;
    private string? _resultPath;
    private ConversionSettings? _conversion;
    private TrackExportPlan? _copyOverride;
    private string? _configurationError;
    public MediaStreamInfo Stream { get; }
    public string Caption { get; }
    public ExportDecision Decision { get; }
    public bool CanConvert => Stream.Index is >= 0 &&
        (Stream.VisualKind is VisualKind.Video or VisualKind.Image or VisualKind.Animation || Stream.CodecType == "audio"
            || Stream.CodecType == "subtitle" && Stream.SubtitleKind == SubtitleKind.Text);
    public bool IsSupported => Decision.Plan is not null;
    public ConversionSettings? Conversion
    {
        get => _conversion;
        private set
        {
            if (!SetProperty(ref _conversion, value)) return;
            OnPropertyChanged(nameof(Plan));
            OnPropertyChanged(nameof(ModeDisplay));
            OnPropertyChanged(nameof(OutputPreview));
            OnPropertyChanged(nameof(StatusLabel));
        }
    }
    public TrackExportPlan? Plan => IsAttachmentGroup ? null : Conversion is { } options
        ? new(Stream, options.Format.Extension, options.Format.Muxer, false, false, options)
        : _copyOverride ?? Decision.Plan;
    public string ModeDisplay => L.Get(Conversion is null ? "MainModeExtract" : "MainModeConvert");
    public string OutputPreview => Conversion is null
        ? Plan is { } copy ? L.Format("MainCopyPreview", copy.Extension) : L.Get("MainCopyFormatUnknown")
        : $".{Conversion.Format.Extension} · {Conversion.Encoder.Name}";
    public void SetConversion(ConversionSettings? options)
    {
        if (_locked || options is not null && (!CanConvert || options.Validate(Stream) is not null)) return;
        var hadCopyOverride = _copyOverride is not null;
        var changed = hadCopyOverride || !Equals(_conversion, options);
        Conversion = options;
        _copyOverride = null;
        _configurationError = null;
        if (changed) { State = ExportState.Pending; ResultPath = null; }
        if (hadCopyOverride)
        {
            OnPropertyChanged(nameof(Plan));
            OnPropertyChanged(nameof(OutputPreview));
        }
        SetLocalizedStatus(() => options is null ? Decision.Plan is null
            ? L.Get("MainConversionAfterSetup") : L.Get("MainNotSelected")
            : L.Format("MainConvertingTo", options.Format.Extension.ToUpperInvariant(), options.Encoder.Name));
        OnPropertyChanged(nameof(StatusLabel));
    }
    public void SetCopyOverride(TrackExportPlan? plan)
    {
        if (_locked || plan is not null && (plan.Stream != Stream || !plan.VerifiedCopy)) return;
        var changed = _conversion is not null || _copyOverride != plan;
        Conversion = null;
        _copyOverride = plan;
        _configurationError = null;
        if (changed) { State = ExportState.Pending; ResultPath = null; }
        SetLocalizedStatus(() => plan is null ? L.Get("MainNotSelected")
            : L.Format("MainCopyingTo", plan.Extension.ToUpperInvariant()));
        OnPropertyChanged(nameof(Plan));
        OnPropertyChanged(nameof(OutputPreview));
        OnPropertyChanged(nameof(StatusLabel));
    }
    public void SetConfigurationError(string reason)
    {
        if (_locked) return;
        _configurationError = reason;
        State = ExportState.Pending;
        ResultPath = null;
        _statusFactory = null;
        Status = reason;
        OnPropertyChanged(nameof(StatusLabel));
    }
    internal void SetConfigurationErrorKey(string key)
    {
        SetConfigurationError(L.Get(key));
        _statusFactory = () => L.Get(key);
    }
    public bool CanSelect => (IsAttachmentGroup || IsSupported || CanConvert) && !_locked;
    public string IndexDisplay => Stream.Index?.ToString(CultureInfo.InvariantCulture) ?? "—";
    public string TypeDisplay => IsAttachmentGroup ? L.Get("MainAttachments") : Stream.VisualKind switch
    {
        VisualKind.Image => L.Get(Stream.IsCoverArt ? "MainDescriptionCover" : "MainTypeImage"),
        VisualKind.Animation => L.Get("MainTypeAnimation"),
        VisualKind.Video => L.Get("MainTypeVideo"),
        _ => Stream.CodecType switch
        {
            "audio" => L.Get("MainTypeAudio"), "subtitle" => L.Get("MainTypeSubtitle"), "attachment" => L.Get("MainTypeAttachment"),
            _ => L.Get("MainTypeOther")
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
            ? MainViewModel.FormatCount(channels, L.Get("MainChannelOne"), L.Get("MainChannelFew"), L.Get("MainChannelMany"))
                + (string.IsNullOrWhiteSpace(Stream.ChannelLayout) ? "" : $" ({Stream.ChannelLayout})")
            : L.Get("MainChannelsUnknown"),
        "subtitle" => Stream.SubtitleKind switch
        {
            SubtitleKind.Text => L.Get("MainSubtitleText"), SubtitleKind.Graphic => L.Get("MainSubtitleGraphic"), _ => "—"
        },
        _ => Stream.Width is { } width && Stream.Height is { } height ? $"{width} × {height}" : "—"
    };
    public ExportState State { get => _state; private set => SetProperty(ref _state, value); }
    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (IsAttachmentGroup) { SetAttachmentSelection(value ? _attachmentFiles.Select(f => f.Uid) : []); return; }
            if (CanSelect && SetProperty(ref _isSelected, value))
            {
                OnPropertyChanged(nameof(StatusLabel));
                OnPropertyChanged(nameof(StatusBadgeBackground));
                OnPropertyChanged(nameof(StatusForeground));
            }
        }
    }
    public string Status { get => _status; private set => SetProperty(ref _status, value); }
    private void SetLocalizedStatus(Func<string> factory)
    {
        _statusFactory = factory;
        Status = factory();
    }
    public string StatusLabel => IsAttachmentGroup
        ? L.Format("MainAttachmentCount", _attachmentFiles.Count) : !IsSupported && !CanConvert && !IsAttachmentGroup
        ? L.Get("MainStatusUnavailable") : State switch
    {
        ExportState.Running => L.Get(Conversion is null ? "MainStatusExtracting" : "MainStatusConverting"),
        ExportState.Completed => L.Get(Conversion is null ? "MainStatusExtracted" : "MainStatusConverted"),
        ExportState.Failed => L.Get("InstallerError"), ExportState.Cancelled => L.Get("MainStatusCancelled"),
        _ when !IsSelected => L.Get(Decision.Plan is null ? "MainStatusConversionAvailable" : "MainStatusAvailable"),
        _ when _configurationError is not null => L.Get("MainStatusNeedsSetup"),
        _ when Plan is null => L.Get("MainStatusNeedsSetup"),
        _ when Conversion is not null => L.Get("MainModeConvert"),
        _ => L.Get("MainStatusAvailable")
    };
    public void RefreshMainLocalization()
    {
        if (_statusFactory is not null) Status = _statusFactory();
        OnPropertyChanged(nameof(TypeDisplay));
        OnPropertyChanged(nameof(LanguageDisplay));
        OnPropertyChanged(nameof(DetailsDisplay));
        OnPropertyChanged(nameof(BitRateDisplay));
        OnPropertyChanged(nameof(StatusLabel));
        OnPropertyChanged(nameof(AttachmentStateGlyph));
        OnPropertyChanged(nameof(ModeDisplay));
        OnPropertyChanged(nameof(OutputPreview));
    }
    public IBrush StatusBadgeBackground => !IsSupported && !CanConvert && !IsAttachmentGroup ? MutedBackground : State switch
    {
        ExportState.Running => RunningBackground,
        ExportState.Failed or ExportState.Cancelled => ErrorBackground,
        _ => ReadyBackground
    };
    public IBrush StatusForeground => !IsSupported && !CanConvert && !IsAttachmentGroup
        ? new SolidColorBrush(Color.Parse("#F0989D")) : State switch
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

    public void SetResult(ExportState state, string message, string? path, LocalizedMessage? localizedMessage = null)
    {
        State = state;
        _statusFactory = localizedMessage is null ? null : localizedMessage.Resolve;
        Status = localizedMessage?.Resolve() ?? message;
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
        if (entry.UserMessageKey is not { } key) return null;
        var localizer = LocalizationManager.Active;
        var message = entry.UserMessageArgs is { Length: > 0 } args
            ? localizer.Format(key, args)
            : localizer.Get(key);
        return new UserLogEntry(entry.Time.ToString("HH:mm:ss"), message, entry.Level,
            entry.UserSuccess && entry.Level == DiagnosticLevel.Info);
    }
}
