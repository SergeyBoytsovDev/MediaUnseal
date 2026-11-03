using System;
using System.Collections.Specialized;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Animation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;
using TrackExtractor.Services;
using TrackExtractor.Models;
using TrackExtractor.ViewModels;

namespace TrackExtractor.Views;

public partial class MainWindow : Window
{
    private readonly CancellationTokenSource _shutdown = new();
    private int _folderFeedbackVersion;
    private SettingsWindow? _settingsWindow;
    private FfmpegConversionCatalog? _conversionCatalog;
    private string? _conversionCatalogPath;
    private bool _conversionLoading;
    private bool _refreshingFormats;
    private bool _applyingFormats;
    private bool _reapplyFormats;
    private CancellationTokenSource? _formatChange;
    private readonly Dictionary<string, FormatChoice> _formatChoices = new();
    private readonly HashSet<ExportTrackViewModel> _previouslySelected = new();
    private sealed record FormatChoice(string Display, ConversionFormat? Format = null,
        ConversionSettings? Settings = null, bool SeparatorBefore = false, bool CopyRequested = false)
    {
        public override string ToString() => Display;
    }
    private MainViewModel? _logViewModel;
    private static readonly double[] MinimumColumnWidths = [36, 65, 54, 65, 65, 72, 95, 75];
    private UiAppearance? Appearance => (Avalonia.Application.Current as App)?.Appearance;

    public MainWindow()
    {
        InitializeComponent();
        UpdateWindowStateIcon();
        PropertyChanged += (_, args) =>
        {
            if (args.Property == WindowStateProperty) UpdateWindowStateIcon();
        };
        if (Appearance is { } appearance)
        {
            Width = appearance.Preferences.Width;
            Height = appearance.Preferences.Height;
            for (var i = 0; i < StreamsTable.Columns.Count; i++)
                StreamsTable.Columns[i].Width = new GridLength(i == 0 ? 36 : appearance.Preferences.ColumnWidths[i]);
        }
        for (var i = 0; i < StreamsTable.Columns.Count; i++)
        {
            var index = i;
            StreamsTable.Columns[i].PropertyChanged += (_, args) =>
            {
                if (args.Property == TableViewColumn.WidthProperty &&
                    StreamsTable.Columns[index].Width.IsAbsolute &&
                    StreamsTable.Columns[index].Width.Value < MinimumColumnWidths[index])
                    StreamsTable.Columns[index].Width = new GridLength(MinimumColumnWidths[index]);
            };
        }
        RefreshColumnHeaders();
        StreamsTable.AddHandler(InputElement.PointerPressedEvent, StreamsTable_PointerPressed,
            RoutingStrategies.Tunnel, handledEventsToo: true);
        Opened += MainWindow_Opened;
        LocalizationManager.Active.PropertyChanged += Localization_PropertyChanged;
        Closing += MainWindow_Closing;
        Closed += (_, _) =>
        {
            _shutdown.Cancel();
            LocalizationManager.Active.PropertyChanged -= Localization_PropertyChanged;
            if (_logViewModel is not null)
            {
                _logViewModel.UserEvents.CollectionChanged -= Events_CollectionChanged;
                _logViewModel.PropertyChanged -= ViewModel_PropertyChanged;
            }
        };
        DataContextChanged += (_, _) =>
        {
            if (_logViewModel is not null)
            {
                _logViewModel.UserEvents.CollectionChanged -= Events_CollectionChanged;
                _logViewModel.PropertyChanged -= ViewModel_PropertyChanged;
            }
            _logViewModel = DataContext as MainViewModel;
            if (_logViewModel is not null)
            {
                _logViewModel.UserEvents.CollectionChanged += Events_CollectionChanged;
                _logViewModel.PropertyChanged += ViewModel_PropertyChanged;
            }
            RefreshFormatControls();
        };
        DragDrop.SetAllowDrop(this, true);
        this.AddHandler(DragDrop.DragOverEvent, DragOver, RoutingStrategies.Bubble, handledEventsToo: true);
        this.AddHandler(DragDrop.DropEvent, Drop, RoutingStrategies.Bubble, handledEventsToo: true);
        DragDrop.AddDragLeaveHandler(this, (_, _) => DropZone.Classes.Remove("dragging"));
    }

    private void Localization_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != "Item[]") return;
        _logViewModel?.RefreshMainLocalization();
        RefreshColumnHeaders();
        RefreshFormatControls();
    }

    private void RefreshColumnHeaders()
    {
        var keys = new[] { "MainColumnType", "MainColumnIndex", "MainColumnLanguage",
            "MainColumnCodec", "MainColumnBitrate", "MainColumnDetails", "MainColumnStatus" };
        for (var i = 0; i < keys.Length; i++)
            StreamsTable.Columns[i + 1].Header = LocalizationManager.Active.Get(keys[i]);
    }

    private void Events_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action == NotifyCollectionChangedAction.Add &&
            LogScroll.Offset.Y >= LogScroll.Extent.Height - LogScroll.Viewport.Height - 35)
            Dispatcher.UIThread.Post(() => LogScroll.ScrollToEnd());
    }

    private void TitleBar_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        if (e.ClickCount == 2) ToggleMaximize();
        else BeginMoveDrag(e);
    }

    private void ResizeGrip_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed && WindowState != WindowState.Maximized)
            BeginResizeDrag(WindowEdge.SouthEast, e);
    }

    private void ResizeEdge_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed && WindowState != WindowState.Maximized
            && sender is Control { Tag: string edge } && Enum.TryParse<WindowEdge>(edge, out var direction))
            BeginResizeDrag(direction, e);
    }

    private void Minimize_Click(object? sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    private void Maximize_Click(object? sender, RoutedEventArgs e) => ToggleMaximize();
    private void Close_Click(object? sender, RoutedEventArgs e) => Close();
    private void UpdateWindowStateIcon() => WindowStateIcon.Data = Geometry.Parse(
        WindowState == WindowState.Maximized
            ? "M 7,3 L 20,3 L 20,17 L 7,17 Z M 3,7 L 3,21 L 17,21 L 17,19"
            : "M 4,4 L 20,4 L 20,20 L 4,20 Z");
    private void ToggleMaximize() => WindowState = WindowState == WindowState.Maximized
        ? WindowState.Normal : WindowState.Maximized;

    private async void MainWindow_Closing(object? sender, WindowClosingEventArgs e)
    {
        if (DataContext is not MainViewModel viewModel ||
            (!viewModel.IsBusy && !viewModel.HasActiveAnalyses && !viewModel.IsExporting))
        {
            SaveWindowPreferences();
            return;
        }
        e.Cancel = true;
        _shutdown.Cancel();
        viewModel.CancelOperation();
        viewModel.CancelAnalysis();
        viewModel.CancelExport();
        await Task.WhenAll(viewModel.WaitForIdleAsync(), viewModel.WaitForAnalysesAsync(), viewModel.WaitForExportAsync());
        Close();
    }

    private async void MainWindow_Opened(object? sender, EventArgs e)
    {
        Opened -= MainWindow_Opened;
        RestoreWindowPosition();
        if (DataContext is not MainViewModel viewModel) return;
        try
        {
            await viewModel.CheckAsync(_shutdown.Token);
            if (!viewModel.IsReady && !_shutdown.IsCancellationRequested) ShowSettings(0, showContinue: true);
        }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested) { }
        catch (Exception ex)
        {
            viewModel.ReportError(LocalizationManager.Active.Format("MainToolsCheckFailed", ex.Message));
            if (!_shutdown.IsCancellationRequested) ShowSettings(0, showContinue: true);
        }
    }

    private void Settings_Click(object? sender, RoutedEventArgs e) => ShowSettings(0);

    private void SaveWindowPreferences()
    {
        if (Appearance is not { } appearance) return;
        if (WindowState == WindowState.Normal)
        {
            appearance.Preferences.Width = Width;
            appearance.Preferences.Height = Height;
            appearance.Preferences.X = Position.X;
            appearance.Preferences.Y = Position.Y;
        }
        appearance.Preferences.ColumnWidths = StreamsTable.Columns
            .Select((column, index) => index == 0 ? 36 : double.IsFinite(column.ActualWidth)
                ? Math.Clamp(column.ActualWidth, MinimumColumnWidths[index], 1000)
                : appearance.Preferences.ColumnWidths[index]).ToArray();
        appearance.Save();
    }

    private void RestoreWindowPosition()
    {
        if (Appearance?.Preferences is not { X: { } x, Y: { } y } preferences) return;
        var screen = Screens.All.FirstOrDefault(item =>
            x >= item.WorkingArea.X && x < item.WorkingArea.Right &&
            y >= item.WorkingArea.Y && y < item.WorkingArea.Bottom) ?? Screens.Primary;
        if (screen is null) return;
        var area = screen.WorkingArea;
        var scale = screen.Scaling;
        Width = Math.Min(preferences.Width, area.Width / scale);
        Height = Math.Min(preferences.Height, area.Height / scale);
        MinWidth = Math.Min(MinWidth, Width);
        MinHeight = Math.Min(MinHeight, Height);
        var width = (int)Math.Ceiling(Width * scale);
        var height = (int)Math.Ceiling(Height * scale);
        Position = new Avalonia.PixelPoint(
            Math.Clamp(x, area.X, Math.Max(area.X, area.Right - width)),
            Math.Clamp(y, area.Y, Math.Max(area.Y, area.Bottom - height)));
    }

    private void ClearFile_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is MainViewModel viewModel) viewModel.ClearCurrentFile();
    }

    private void ToggleAllTracks_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is MainViewModel viewModel) viewModel.ToggleAllTracks();
    }

    private void StreamsTable_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.Source is not Avalonia.Visual visual
            || (visual as TableViewRow ?? visual.FindAncestorOfType<TableViewRow>())?.DataContext
                is not ExportTrackViewModel track) return;
        var point = e.GetCurrentPoint(StreamsTable).Properties;
        if (point.IsRightButtonPressed)
            SelectStreamContext(track);
        else if (point.IsLeftButtonPressed && e.ClickCount >= 2 && e.ClickCount % 2 == 0)
        {
            ToggleRowSelection(track);
            e.Handled = true;
        }
    }

    internal void SelectStreamContext(ExportTrackViewModel track)
    {
        StreamsTable.SelectedItem = track;
        PropertiesMenuItem.IsVisible = !track.IsAttachmentGroup;
        ViewAttachmentsMenuItem.IsVisible = track.IsAttachmentGroup;
        ExtractAllAttachmentsMenuItem.IsVisible = track.IsAttachmentGroup;
    }

    internal static void ToggleRowSelection(ExportTrackViewModel track)
    {
        if (track.IsAttachmentGroup) track.ToggleAttachments();
        else track.IsSelected = !track.IsSelected;
    }

    private void ToggleAttachment_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: ExportTrackViewModel track }) track.ToggleAttachments();
    }

    private async void ViewAttachments_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel { AttachmentFiles: { Count: > 0 } } viewModel) return;
        var media = viewModel.SelectedMedia;
        var dialog = new AttachmentsWindow(viewModel.AttachmentFiles,
            viewModel.AttachmentRow?.SelectedAttachmentUids ?? []);
        var selection = await dialog.ShowDialog<HashSet<ulong>?>(this);
        if (selection is not null && ReferenceEquals(viewModel.SelectedMedia, media))
            viewModel.SetAttachmentSelection(selection);
    }

    private async void ExtractAllAttachments_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is MainViewModel { AttachmentFiles: { Count: > 0 } } viewModel)
            await viewModel.StartExportAsync(_shutdown.Token,
                viewModel.AttachmentFiles.Select(file => file.Uid).ToArray());
    }

    private void StreamsTable_SelectionChanged(object? sender, SelectionChangedEventArgs e) { }

    private void ViewModel_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(MainViewModel.IsExporting) or nameof(MainViewModel.SelectedMedia)
            or nameof(MainViewModel.IsReady) or nameof(MainViewModel.IsAnalyzing)
            or nameof(MainViewModel.SelectedCountDisplay) or nameof(MainViewModel.TrackHeading))
        {
            if (e.PropertyName == nameof(MainViewModel.SelectedMedia))
            {
                _formatChange?.Cancel();
                _formatChoices.Clear();
                _previouslySelected.Clear();
            }
            if (e.PropertyName == nameof(MainViewModel.SelectedCountDisplay) &&
                DataContext is MainViewModel selectionModel)
            {
                var current = selectionModel.ExportTracks.Where(t => t.IsSelected).ToHashSet();
                foreach (var kind in new[] { "video", "audio", "image", "animation" })
                    if (ShouldResetFormat(kind, _previouslySelected, current)) _formatChoices.Remove(kind);
                _previouslySelected.Clear();
                _previouslySelected.UnionWith(current);
            }
            if (!_applyingFormats) RefreshFormatControls();
            if (e.PropertyName == nameof(MainViewModel.TrackHeading)) _ = LoadFormatsAsync();
            if (e.PropertyName == nameof(MainViewModel.SelectedCountDisplay) && !_applyingFormats)
                _ = ApplyAllFormatsAsync();
        }
    }

    private static string Kind(ExportTrackViewModel track) => track.Stream.VisualKind switch
    {
        VisualKind.Video => "video", VisualKind.Image => "image",
        VisualKind.Animation => "animation", _ => track.Stream.CodecType ?? ""
    };

    internal static bool ShouldResetFormat(string kind, IEnumerable<ExportTrackViewModel> previous,
        IEnumerable<ExportTrackViewModel> current) =>
        !previous.Any(track => Kind(track) == kind) && current.Any(track => Kind(track) == kind);

    private static readonly Dictionary<string, (string Muxer, string LabelKey)[]> PopularFormats = new()
    {
        ["video"] = [("matroska", "MainFormatMkv"), ("mp4", "MainFormatMp4"),
            ("mov", "MainFormatMov"), ("webm", "MainFormatWebm"), ("avi", "MainFormatAvi")],
        ["audio"] = [("mp3", "MainFormatMp3"), ("wav", "MainFormatWav"),
            ("flac", "MainFormatFlac"), ("adts", "MainFormatAac"),
            ("ac3", "MainFormatAc3"), ("ogg", "MainFormatOgg"), ("opus", "MainFormatOpus")],
        ["subtitle"] = [("srt", "MainFormatSrt"), ("ass", "MainFormatAss"),
            ("webvtt", "MainFormatWebvtt")]
    };

    private ComboBox? FormatBox(string kind) => kind switch
    { "video" => VideoFormatBox, "audio" => AudioFormatBox, "subtitle" => SubtitleFormatBox,
      "image" => ImageFormatBox, "animation" => AnimationFormatBox, _ => null };
    private Button? FormatButton(string kind) => kind switch
    { "video" => VideoConfigureButton, "audio" => AudioConfigureButton,
      "subtitle" => SubtitleConfigureButton, "image" => ImageConfigureButton,
      "animation" => AnimationConfigureButton, _ => null };

    private void RefreshFormatControls()
    {
        if (VideoFormatBox is null || AudioFormatBox is null || SubtitleFormatBox is null ||
            ImageFormatBox is null || AnimationFormatBox is null || VideoConfigureButton is null ||
            AudioConfigureButton is null || SubtitleConfigureButton is null ||
            ImageConfigureButton is null || AnimationConfigureButton is null)
            return;
        _refreshingFormats = true;
        try
        {
            foreach (var kind in new[] { "video", "audio", "subtitle", "image", "animation" })
            {
                var tracks = (DataContext as MainViewModel)?.ExportTracks
                    .Where(t => t.IsSelected && Kind(t) == kind).ToArray() ?? [];
                var loc = LocalizationManager.Active;
                var sourceFormats = tracks.Select(t => t.Decision.Plan?.Extension)
                    .Where(extension => extension is not null)
                    .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
                var source = sourceFormats.Length == 1
                    ? loc.Format("MainSourceFormatOriginal", SourceFormatName(sourceFormats[0]!))
                    : tracks.Length > 0 ? loc.Get("MainMixedFormatsOriginal")
                        : loc.Get("MainOriginalNoTranscode");
                _formatChoices.TryGetValue(kind, out var selected);
                var customOriginal = selected is { Format: not null, Settings: not null }
                    && IsDuplicateOriginal(tracks, selected.Format);
                if (selected is { Format: not null, Settings: not null })
                {
                    selected = selected with { Display = customOriginal
                        ? loc.Format("MainSourceCustomFormat", SourceFormatName(selected.Format.Extension))
                        : loc.Format("MainCustomFormat", selected.Format.Extension.ToUpperInvariant()) };
                    _formatChoices[kind] = selected;
                }
                var choices = new List<FormatChoice> { customOriginal ? selected! : new(source) };
                if (_conversionCatalog is { } catalog)
                {
                    var secondaryAdded = false;
                    foreach (var format in catalog.FormatsFor(kind))
                    {
                        if (IsDuplicateOriginal(tracks, format))
                            continue;
                        var labelKey = PopularFormats.GetValueOrDefault(kind)?
                            .FirstOrDefault(item => item.Muxer == format.Muxer).LabelKey;
                        var secondary = kind is "video" or "animation" &&
                            FfmpegConversionCatalog.Category(format) != kind;
                        choices.Add(selected is { Settings: not null } && selected.Format == format
                            ? selected with { SeparatorBefore = secondary && !secondaryAdded }
                            : new(labelKey is null ? format.Display : loc.Get(labelKey), format,
                                SeparatorBefore: secondary && !secondaryAdded));
                        if (secondary) secondaryAdded = true;
                    }
                }
                if (selected is { Format: not null, Settings: null }
                    && IsDuplicateOriginal(tracks, selected.Format))
                {
                    _formatChoices.Remove(kind);
                    selected = null;
                }
                if (selected is { Format: not null }
                    && !choices.Any(c => c.Format == selected.Format && c.Settings == selected.Settings))
                    choices.Add(selected);
                var box = FormatBox(kind)!;
                box.ItemsSource = choices;
                box.SelectedItem = selected is null ? choices[0] :
                    choices.FirstOrDefault(c => c.Format == selected.Format && c.Settings == selected.Settings) ?? choices[0];
                var enabled = tracks.Length > 0 && DataContext is MainViewModel
                    { IsReady: true, IsExporting: false, IsAnalyzing: false } && !_conversionLoading;
                box.IsEnabled = enabled && tracks.All(t => t.CanConvert)
                    && (choices.Count > 1 || selected?.Format is not null);
                FormatButton(kind)!.IsEnabled = enabled && _conversionCatalog is not null
                    && tracks.All(t => t.CanConvert);
            }
        }
        finally { _refreshingFormats = false; }
    }

    private static string SourceFormatName(string extension) => extension.ToLowerInvariant() switch
    {
        "jpg" or "jpeg" => "JPEG", "webp" => "WebP", "vtt" => "WebVTT",
        _ => extension.ToUpperInvariant()
    };

    internal static bool IsDuplicateOriginal(IReadOnlyList<ExportTrackViewModel> tracks,
        ConversionFormat format) => tracks.Count > 0 && tracks.All(t => t.Decision.Plan is { } original
            && original.Muxer == format.Muxer && original.Extension == format.Extension);

    private async Task LoadFormatsAsync()
    {
        if (_conversionLoading || DataContext is not MainViewModel { FfmpegExecutable: { } ffmpeg }) return;
        if (_conversionCatalog is not null && _conversionCatalogPath == ffmpeg) { RefreshFormatControls(); return; }
        _conversionLoading = true;
        try
        {
            _conversionCatalog = await FfmpegConversionCatalog.DiscoverAsync(ffmpeg, _shutdown.Token);
            _conversionCatalogPath = ffmpeg;
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception
            or TimeoutException or InvalidDataException)
        { (DataContext as MainViewModel)?.ReportExportError(LocalizationManager.Active.Get("MainFormatsReadFailed")); }
        finally
        {
            _conversionLoading = false;
            RefreshFormatControls();
            if (DataContext is MainViewModel { SelectedMedia: not null }) _ = ApplyAllFormatsAsync();
        }
    }

    private void Format_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_refreshingFormats || sender is not ComboBox box || box.SelectedItem is not FormatChoice choice) return;
        var kind = box == VideoFormatBox ? "video" : box == AudioFormatBox ? "audio"
            : box == SubtitleFormatBox ? "subtitle" : box == ImageFormatBox ? "image" : "animation";
        _formatChoices[kind] = choice;
        _ = ApplyAllFormatsAsync();
    }

    private async Task ApplyAllFormatsAsync()
    {
        if (_applyingFormats)
        {
            _reapplyFormats = true;
            _formatChange?.Cancel();
            return;
        }
        if (_conversionLoading || DataContext is not MainViewModel
            { SelectedMedia: { } media, FfmpegExecutable: not null } viewModel) return;
        _formatChange?.Cancel();
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_shutdown.Token);
        _formatChange = cancellation;
        _applyingFormats = true;
        viewModel.SetFormatsValid(false);
        var valid = true;
        try
        {
            foreach (var track in viewModel.ExportTracks.Where(t => t.IsSelected))
            {
                cancellation.Token.ThrowIfCancellationRequested();
                var kind = Kind(track);
                if (!_formatChoices.TryGetValue(kind, out var choice) || choice.Format is null)
                { track.SetConversion(null); continue; }
                if (!track.CanConvert || _conversionCatalog is null)
                { track.SetConfigurationErrorKey("MainConversionUnavailable"); valid = false; continue; }
                if (choice.Settings is { } settings)
                {
                    if (settings.ValidationKey(track.Stream) is { } reasonKey)
                    { track.SetConfigurationErrorKey(reasonKey); valid = false; continue; }
                    if (kind == "video" && CanCopyVideoSettings(settings, track.Stream)
                        && await _conversionCatalog.CheckCopyAsync(media.FullPath, track.Stream.Index!.Value,
                            settings.Format, cancellation.Token, track.Stream))
                    {
                        if (track.Decision.Plan is { } source
                            && source.Muxer == settings.Format.Muxer
                            && source.Extension == settings.Format.Extension)
                            track.SetConversion(null);
                        else track.SetCopyOverride(new(track.Stream, settings.Format.Extension,
                            settings.Format.Muxer, false, VerifiedCopy: true));
                        continue;
                    }
                    track.SetConversion(settings);
                    continue;
                }
                var format = choice.Format;
                if (track.Decision.Plan is { } original && original.Muxer == format.Muxer
                    && original.Extension == format.Extension)
                { track.SetConversion(null); continue; }
                if (choice.CopyRequested)
                {
                    if (await _conversionCatalog.CheckCopyAsync(media.FullPath, track.Stream.Index!.Value,
                        format, cancellation.Token, track.Stream))
                        track.SetCopyOverride(new(track.Stream, format.Extension, format.Muxer, false,
                            VerifiedCopy: true));
                    else { track.SetConfigurationErrorKey("ConversionCopyIncompatible"); valid = false; }
                    continue;
                }
                var encoder = kind == "video"
                    ? ConversionWindow.PrioritizeVideoEncoders(
                        ConversionWindow.InitialEncoders(_conversionCatalog, format, kind, null),
                        format, track.Stream.Codec).FirstOrDefault()
                    : _conversionCatalog.SuggestedEncoder(format, kind);
                if (ShouldTryCopy(kind, format, track.Stream.Codec)
                    && (kind != "video" || encoder is not null
                        && string.Equals(track.Stream.Codec, encoder.Codec, StringComparison.OrdinalIgnoreCase))
                    && await _conversionCatalog.CheckCopyAsync(media.FullPath, track.Stream.Index!.Value,
                    format, cancellation.Token, track.Stream))
                {
                    track.SetCopyOverride(new(track.Stream, format.Extension, format.Muxer, false,
                        VerifiedCopy: true));
                    continue;
                }
                if (encoder is null)
                { track.SetConfigurationErrorKey("MainNoSuitableEncoder"); valid = false; continue; }
                var check = await _conversionCatalog.CheckCombinationAsync(format, encoder, cancellation.Token);
                if (!check.Compatible)
                { track.SetConfigurationError(check.Reason); valid = false; continue; }
                track.SetConversion(new(format, encoder));
            }
        }
        catch (OperationCanceledException) { valid = false; }
        finally
        {
            _applyingFormats = false;
            if (_formatChange == cancellation)
            {
                viewModel.SetFormatsValid(valid && viewModel.SelectedMedia == media);
                _formatChange = null;
            }
            RefreshFormatControls();
            if (_reapplyFormats)
            {
                _reapplyFormats = false;
                _ = ApplyAllFormatsAsync();
            }
        }
    }

    internal static bool ShouldTryCopy(string kind, ConversionFormat format, string? sourceCodec = null) =>
        kind != "image" && format.Muxer != "caf"
            && (kind != "audio"
                || string.Equals(sourceCodec, format.AudioCodec, StringComparison.OrdinalIgnoreCase))
            && !(kind == "animation" && FfmpegConversionCatalog.Category(format) == "video");

    internal static bool CanCopyVideoSettings(ConversionSettings settings, MediaStreamInfo stream) =>
        stream.VisualKind == VisualKind.Video && !settings.ForceReencode
        && settings.Crf is null && settings.BitRateKbps is null
        && settings.Width is null && settings.Height is null && settings.FrameRate is null
        && settings.SampleRate is null && settings.Channels is null && settings.Quality is null
        && string.Equals(stream.Codec, settings.Encoder.Codec, StringComparison.OrdinalIgnoreCase);

    private async void ConfigureFormat_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string kind } || _conversionLoading ||
            DataContext is not MainViewModel { SelectedMedia: { } media } viewModel) return;
        var track = viewModel.ExportTracks.FirstOrDefault(t => t.IsSelected && Kind(t) == kind && t.CanConvert);
        if (track is null) return;
        if (_conversionCatalog is null) await LoadFormatsAsync();
        if (_conversionCatalog is null) return;
        var choice = _formatChoices.GetValueOrDefault(kind);
        var initialFormat = choice?.Format;
        if (initialFormat is null && track.Decision.Plan is null)
        {
            var sourceExtension = Path.GetExtension(media.FullPath).TrimStart('.');
            initialFormat = _conversionCatalog.FormatsFor(kind).FirstOrDefault(format =>
                format.Extension.Equals(sourceExtension, StringComparison.OrdinalIgnoreCase));
        }
        var dialog = new ConversionWindow(_conversionCatalog, viewModel.Diagnostics, track.Stream, choice?.Settings,
            _shutdown.Token, initialFormat, track.Decision.Plan, media.FullPath, choice?.CopyRequested == true);
        var settings = await dialog.ShowDialog<ConversionSettings?>(this);
        if (dialog.AppliedCopy && viewModel.SelectedMedia == media)
        {
            _formatChoices[kind] = new FormatChoice(LocalizationManager.Active.Get("MainOriginalNoTranscode"));
            RefreshFormatControls();
            await ApplyAllFormatsAsync();
            return;
        }
        if (dialog.AppliedCopyFormat is { } copiedFormat && viewModel.SelectedMedia == media)
        {
            _formatChoices[kind] = new(LocalizationManager.Active.Format("MainCustomFormat",
                copiedFormat.Extension.ToUpperInvariant()), copiedFormat, CopyRequested: true);
            RefreshFormatControls();
            await ApplyAllFormatsAsync();
            return;
        }
        if (dialog.AppliedDefaultFormat is { } defaultFormat && viewModel.SelectedMedia == media)
        {
            _formatChoices[kind] = new(defaultFormat.Display, defaultFormat);
            RefreshFormatControls();
            await ApplyAllFormatsAsync();
            return;
        }
        if (settings is null || viewModel.SelectedMedia != media) return;
        _formatChoices[kind] = new(LocalizationManager.Active.Format("MainCustomFormat", settings.Format.Extension.ToUpperInvariant()),
            settings.Format, settings);
        RefreshFormatControls();
        await ApplyAllFormatsAsync();
    }

    private void OutputFormatDivider_Loaded(object? sender, RoutedEventArgs e)
    {
        if (sender is Border divider)
            divider.IsVisible = divider.DataContext is FormatChoice { SeparatorBefore: true }
                && divider.FindAncestorOfType<ComboBoxItem>() is not null;
    }

    private void Properties_Click(object? sender, RoutedEventArgs e)
    {
        if (StreamsTable.SelectedItem is ExportTrackViewModel { IsAttachmentGroup: false } track)
            StreamPropertiesWindow.Show(this, track.Stream);
    }

    private void ShowSettings(int section, bool showContinue = false)
    {
        if (_shutdown.IsCancellationRequested || DataContext is not MainViewModel viewModel || Appearance is not { } appearance) return;
        if (_settingsWindow is not null)
        {
            _settingsWindow.SelectSection(section);
            if (showContinue) _settingsWindow.SetShowContinue(true);
            _settingsWindow.Activate();
            return;
        }
        var window = new SettingsWindow(viewModel, appearance, _shutdown.Token);
        window.SelectSection(section);
        window.SetShowContinue(showContinue);
        _settingsWindow = window;
        window.Closed += (_, _) => _settingsWindow = null;
        window.Show(this);
    }

    private async void SelectFile_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel viewModel) return;
        if (!viewModel.SelectFile())
        {
            if (!viewModel.IsReady && !viewModel.IsBusy && !viewModel.IsExporting) ShowSettings(0);
            return;
        }
        try
        {
            var startDirectory = Appearance?.SourcePickerDirectory();
            Avalonia.Platform.Storage.IStorageFolder? startFolder = null;
            if (startDirectory is not null)
                try { startFolder = await StorageProvider.TryGetFolderFromPathAsync(startDirectory); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { }
            if (startFolder is null && Appearance?.Preferences.OutputDirectory is { } outputDirectory
                && Directory.Exists(outputDirectory) && outputDirectory != startDirectory)
                try { startFolder = await StorageProvider.TryGetFolderFromPathAsync(outputDirectory); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { }
            var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = LocalizationManager.Active.Get("MainPickMediaFile"), AllowMultiple = false,
                SuggestedStartLocation = startFolder
            });
            if (files.Count == 1) await AddFileAsync(files[0].Path);
        }
        catch (Exception)
        {
            if (!_shutdown.IsCancellationRequested)
                viewModel.ReportFileErrorKey("MainFilePickFailed");
        }
    }

    private void DragOver(object? sender, DragEventArgs e)
    {
        e.DragEffects = DataContext is MainViewModel { CanChooseFile: true }
            ? DragDropEffects.Copy : DragDropEffects.None;
        DropZone.Classes.Set("dragging", e.DragEffects == DragDropEffects.Copy);
    }

    private async void Drop(object? sender, DragEventArgs e)
    {
        e.Handled = true;
        DropZone.Classes.Remove("dragging");
        if (DataContext is not MainViewModel viewModel) return;
        if (!viewModel.SelectFile())
        {
            if (!viewModel.IsReady && !viewModel.IsBusy && !viewModel.IsExporting) ShowSettings(0);
            return;
        }
        try
        {
            var files = e.DataTransfer.TryGetFiles()?.Take(2).ToArray();
            if (files is null || files.Length != 1)
            {
                viewModel.ReportFileErrorKey("MainDropOneLocalFile");
                return;
            }
            await AddFileAsync(files[0].Path);
        }
        catch (Exception)
        {
            if (!_shutdown.IsCancellationRequested)
                viewModel.ReportFileErrorKey("MainDropOpenFailed");
        }
    }

    private async Task AddFileAsync(Uri uri)
    {
        if (DataContext is not MainViewModel viewModel) return;
        if (!uri.IsFile) { viewModel.ReportFileErrorKey("MainPickLocalFile"); return; }
        await viewModel.AnalyzeFileAsync(uri.LocalPath, _shutdown.Token);
        if (viewModel.SelectedMedia is { } media && string.Equals(media.FullPath,
            Path.GetFullPath(uri.LocalPath), OperatingSystem.IsWindows()
                ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
        {
            Appearance?.RememberSourceFile(media.FullPath);
            viewModel.Diagnostics.Record(DiagnosticLevel.Info, "File selection",
                "Last successfully loaded source directory saved in UI preferences.");
        }
    }

    private void OutputFolder_TextChanged(object? sender, TextChangedEventArgs e)
    {
        if (DataContext is MainViewModel viewModel)
            viewModel.SetOutputDirectoryInputValid(string.Equals(OutputFolderBox.Text,
                viewModel.OutputDirectory, OperatingSystem.IsWindows()
                    ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal));
    }

    private void OutputFolder_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        CommitOutputFolder();
        e.Handled = true;
    }

    private void OutputFolder_LostFocus(object? sender, RoutedEventArgs e) => CommitOutputFolder();

    private void CommitOutputFolder()
    {
        if (DataContext is not MainViewModel viewModel || viewModel.IsExporting) return;
        if (string.Equals(OutputFolderBox.Text, viewModel.OutputDirectory,
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)) return;
        var accepted = viewModel.TrySetUserOutputDirectory(OutputFolderBox.Text);
        if (accepted)
        {
            if (Appearance is { } appearance && viewModel.OutputDirectory is { } selected)
            {
                appearance.Preferences.OutputDirectory = selected;
                appearance.Save();
            }
        }
        OutputFolderBox.Text = viewModel.OutputDirectory;
        viewModel.SetOutputDirectoryInputValid(true);
        FlashOutputFolder(accepted);
    }

    private void FlashOutputFolder(bool accepted)
    {
        var border = OutputFolderFeedbackBorder;
        var version = ++_folderFeedbackVersion;
        border.Transitions = null;
        border.BorderBrush = Application.Current?.Resources[accepted ? "SuccessBrush" : "ErrorBrush"] as IBrush;
        border.Opacity = 1;
        DispatcherTimer.RunOnce(() =>
        {
            if (version != _folderFeedbackVersion || _shutdown.IsCancellationRequested) return;
            border.Transitions = new Transitions
            {
                new DoubleTransition { Property = Visual.OpacityProperty, Duration = TimeSpan.FromMilliseconds(850) }
            };
            border.Opacity = 0;
        }, TimeSpan.FromMilliseconds(120));
    }

    private async void ChooseFolder_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel viewModel || viewModel.IsExporting) return;
        try
        {
            var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
            {
                Title = LocalizationManager.Active.Get("MainPickSaveFolder"), AllowMultiple = false
            });
            if (folders.Count == 1)
            {
                var path = folders[0].TryGetLocalPath();
                if (string.IsNullOrWhiteSpace(path) && folders[0].Path.IsFile)
                    path = folders[0].Path.LocalPath;
                if (!string.IsNullOrWhiteSpace(path))
                {
                    var previous = viewModel.OutputDirectory;
                    var accepted = viewModel.TrySetUserOutputDirectory(path);
                    FlashOutputFolder(accepted);
                    if (accepted && Appearance is { } appearance && viewModel.OutputDirectory is { } selected
                        && !string.Equals(selected, previous, OperatingSystem.IsWindows()
                            ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
                    {
                        appearance.Preferences.OutputDirectory = selected;
                        appearance.Save();
                    }
                }
                else viewModel.ReportExportErrorKey("MainPickLocalFolder");
            }
        }
        catch (Exception)
        {
            if (!_shutdown.IsCancellationRequested)
                viewModel.ReportExportErrorKey("MainFolderPickFailed");
        }
    }

    private async void Extract_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is MainViewModel viewModel) await viewModel.StartExportAsync(_shutdown.Token);
    }

    private void Stop_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is MainViewModel viewModel) viewModel.CancelExport();
    }

    private void ClearLog_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is MainViewModel viewModel) viewModel.ClearVisibleLog();
    }

    private void OpenOutputFolder_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel viewModel) return;
        var folder = viewModel.OutputDirectory;
        if (folder is null || !Directory.Exists(folder))
        {
            viewModel.ReportExportErrorKey("MainResultFolderMissing");
            return;
        }
        try
        {
            ProcessStartInfo start;
            if (OperatingSystem.IsWindows())
                start = new ProcessStartInfo(folder) { UseShellExecute = true };
            else
            {
                start = new ProcessStartInfo(OperatingSystem.IsMacOS() ? "open" : "xdg-open")
                { UseShellExecute = false, CreateNoWindow = true };
                start.ArgumentList.Add(folder);
            }
            Process.Start(start);
        }
        catch (Exception ex) when (ex is Win32Exception or IOException or UnauthorizedAccessException)
        {
            viewModel.ReportExportErrorKey("MainResultFolderOpenFailed");
        }
    }

    private async void SaveLog_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel viewModel) return;
        try
        {
            var report = await Task.Run(viewModel.DiagnosticReport);
            var rows = await Task.Run(() => DiagnosticReportPresentation.Parse(report));
            new DiagnosticReportWindow(rows, viewModel.Diagnostics, viewModel.ReportError).Show(this);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            viewModel.ReportError(LocalizationManager.Active.Get("DiagnosticPreviewReadFailed"));
        }
    }
}
