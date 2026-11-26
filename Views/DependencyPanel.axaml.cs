using System;
using System.ComponentModel;
using System.IO;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using MediaUnseal.Services;
using MediaUnseal.ViewModels;

namespace MediaUnseal.Views;

public partial class DependencyPanel : UserControl
{
    private readonly MainViewModel _viewModel;
    private readonly CancellationToken _cancellationToken;
    private readonly LocalizationManager _fallbackLocalization = new();
    private string? _lastOcrProblem;
    private TesseractPackage? _availableOcr;
    private bool _ocrBusy;
    private bool _ocrChecking;
    private bool _panelOperation;
    private bool _ocrOnline;
    private CancellationTokenSource? _ocrOperation;
    private LocalizationManager Localization => (Application.Current as App)?.Localization ?? _fallbackLocalization;
    public event EventHandler? ContinueRequested;

    public DependencyPanel() : this(new MainViewModel(), CancellationToken.None) { }

    public DependencyPanel(MainViewModel viewModel, CancellationToken cancellationToken)
    {
        InitializeComponent();
        _viewModel = viewModel;
        _cancellationToken = cancellationToken;
        DataContext = viewModel;
        AttachedToVisualTree += (_, _) =>
        {
            _viewModel.PropertyChanged += ViewModel_PropertyChanged;
            UpdateRetryPosition();
            _ = CheckOcrAsync();
        };
        DetachedFromVisualTree += (_, _) => _viewModel.PropertyChanged -= ViewModel_PropertyChanged;
    }

    internal void SetShowContinue(bool value)
    {
        ContinueArea.IsVisible = value;
        UpdateRetryPosition();
    }

    private void ViewModel_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(MainViewModel.IsReady) or nameof(MainViewModel.NeedsTools))
            UpdateRetryPosition();
        if (e.PropertyName is nameof(MainViewModel.IsBusy) or nameof(MainViewModel.CanAct)
            or nameof(MainViewModel.CanAutoInstall)) UpdateActionAvailability();
    }

    private void UpdateRetryPosition()
    {
        var column = _viewModel.NeedsTools || ContinueArea.IsVisible ? 1 : 0;
        Grid.SetColumn(RetryButton, column);
        Grid.SetColumn(OcrAction, column + 1);
        Grid.SetColumn(OcrSelect, column + 1);
    }

    private void Cancel_Click(object? sender, RoutedEventArgs e)
    {
        if (_ocrBusy) _ocrOperation?.Cancel();
        else _viewModel.CancelOperation();
    }

    private void UpdateActionAvailability()
    {
        var ffmpegAvailable = !_ocrBusy && !_panelOperation;
        InstallButton.SetCurrentValue(IsEnabledProperty, ffmpegAvailable && _viewModel.CanAutoInstall);
        RetryButton.SetCurrentValue(IsEnabledProperty, ffmpegAvailable && _viewModel.CanAct);
        SelectFFmpegButton.SetCurrentValue(IsEnabledProperty, ffmpegAvailable && _viewModel.CanAct);
        SelectFFprobeButton.SetCurrentValue(IsEnabledProperty, ffmpegAvailable && _viewModel.CanAct);
        ContinueButton.SetCurrentValue(IsEnabledProperty, ffmpegAvailable && _viewModel.CanAct);
        var ocrAvailable = !_ocrBusy && !_panelOperation && !_viewModel.IsBusy && !_ocrChecking;
        OcrAction.IsEnabled = ocrAvailable && _ocrOnline;
        OcrSelect.IsEnabled = ocrAvailable;
        InstallerCancel.SetCurrentValue(IsVisibleProperty, _ocrBusy || _viewModel.IsBusy);
    }

    private void Continue_Click(object? sender, RoutedEventArgs e)
    {
        if (!_ocrBusy && !_panelOperation && _viewModel.IsReady)
            ContinueRequested?.Invoke(this, EventArgs.Empty);
    }

    private async void Retry_Click(object? sender, RoutedEventArgs e)
    {
        await RunAsync(async () =>
        {
            await _viewModel.CheckAsync(_cancellationToken);
            await CheckOcrAsync();
        });
    }

    private async Task CheckOcrAsync()
    {
        if (_ocrBusy || _ocrChecking) return;
        _ocrChecking = true;
        UpdateActionAvailability();
        OcrEngine? engine = null;
        OcrStatus.Text = Localization.Get("InstallerCheckingBrief");
        OcrIndicator.Classes.Set("ready", false);
        OcrIndicator.Classes.Set("missing", false);
        OcrIndicator.Classes.Set("error", false);
        try
        {
            engine = await SubtitleOcr.DiscoverAsync(null, null, _cancellationToken);
            var result = await TesseractInstaller.CheckAsync(_cancellationToken);
            _availableOcr = result.Package;
            _ocrOnline = result.Online;
            OcrDownloadSize.Text = Localization.Format("InstallerOcrDownloadSize",
                (result.Package.Size / 1_000_000.0).ToString("F1", CultureInfo.CurrentCulture));
            OcrDownloadSize.IsVisible = engine is null && result.Online;
            var incomplete = engine is not null && (engine.FastLanguages.Count != SubtitleOcr.Languages.Length
                || engine.BestLanguages.Count != SubtitleOcr.Languages.Length);
            var newerAvailable = engine is not null && Version.TryParse(
                engine.Version.Split(' ', StringSplitOptions.RemoveEmptyEntries).ElementAtOrDefault(1), out var installed)
                && Version.TryParse(result.Package.Version, out var available) && installed < available;
            OcrAction.IsVisible = engine is null || incomplete;
            OcrSelect.IsVisible = engine is null || incomplete;
            var actionKey = engine is null ? "InstallerOcrInstall"
                : incomplete ? "InstallerOcrRepair" : "InstallerOcrUpdate";
            OcrAction.Content = Localization.Get(actionKey);
            if (engine is null)
            {
                OcrStatus.Text = Localization.Format(result.Online
                    ? "InstallerOcrAvailableStatus" : "InstallerOcrOfflineStatus", result.Package.Version);
                OcrIndicator.Classes.Set("missing", true);
                ReportOcrProblem("Tesseract 5.x was not found.");
            }
            else if (engine.FastLanguages.Count != SubtitleOcr.Languages.Length
                || engine.BestLanguages.Count != SubtitleOcr.Languages.Length)
            {
                OcrStatus.Text = Localization.Get("InstallerOcrModelsMissing");
                OcrIndicator.Classes.Set("error", true);
                ReportOcrProblem($"OCR models incomplete: Fast {engine.FastLanguages.Count}/10, Best {engine.BestLanguages.Count}/10.");
            }
            else
            {
                OcrStatus.Text = newerAvailable && result.Online
                    ? Localization.Format("InstallerOcrUpdateStatus", engine.Version, result.Package.Version)
                    : engine.Version;
                OcrIndicator.Classes.Set("ready", true);
                _lastOcrProblem = null;
            }
        }
        catch (OperationCanceledException) when (_cancellationToken.IsCancellationRequested) { }
        catch (Exception ex) when (ex is InvalidDataException or JsonException or PlatformNotSupportedException)
        {
            _ocrOnline = false;
            OcrDownloadSize.IsVisible = false;
            OcrAction.IsVisible = false;
            var modelsMissing = engine is not null && (engine.FastLanguages.Count != 10
                || engine.BestLanguages.Count != 10);
            OcrSelect.IsVisible = engine is null || modelsMissing;
            OcrStatus.Text = engine is null ? Localization.Get("InstallerOcrOfflineUnknown")
                : modelsMissing ? Localization.Get("InstallerOcrModelsMissing") : engine.Version;
            OcrIndicator.Classes.Set(engine is null ? "missing" : modelsMissing ? "error" : "ready", true);
            _viewModel.Diagnostics.Record(DiagnosticLevel.Warn, "OCR dependency check",
                "Tesseract manifest could not be checked.", ex.Message);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _ocrOnline = false;
            OcrDownloadSize.IsVisible = false;
            OcrAction.IsVisible = false;
            OcrSelect.IsVisible = engine is null;
            OcrStatus.Text = Localization.Get("InstallerError");
            OcrIndicator.Classes.Set("error", true);
            _viewModel.Diagnostics.Record(DiagnosticLevel.Warn, "OCR dependency check", "Tesseract check failed.", ex.Message);
        }
        finally { _ocrChecking = false; UpdateActionAvailability(); }
    }

    private async void OcrAction_Click(object? sender, RoutedEventArgs e)
    {
        if (_ocrBusy || _panelOperation || _viewModel.IsBusy || _ocrChecking
            || !_ocrOnline || _availableOcr is null) return;
        using var operation = CancellationTokenSource.CreateLinkedTokenSource(_cancellationToken);
        _ocrOperation = operation;
        _ocrBusy = true;
        UpdateActionAvailability();
        InstallerProgress.SetCurrentValue(Avalonia.Controls.Primitives.RangeBase.ValueProperty, 0d);
        InstallerProgress.SetCurrentValue(ProgressBar.IsIndeterminateProperty, false);
        InstallerProgressPercent.SetCurrentValue(Visual.IsVisibleProperty, true);
        InstallerProgressPercent.SetCurrentValue(TextBlock.TextProperty, "0%");
        OcrStatus.Text = Localization.Get("InstallerOcrDownloading");
        var failed = false;
        try
        {
            var progress = new Progress<double>(value =>
            {
                if (!_ocrBusy) return;
                InstallerProgress.SetCurrentValue(Avalonia.Controls.Primitives.RangeBase.ValueProperty, value);
                InstallerProgressPercent.SetCurrentValue(TextBlock.TextProperty, $"{value:0}%");
            });
            await TesseractInstaller.InstallAsync(_availableOcr, progress, operation.Token,
                retry: (attempt, details) => _viewModel.Diagnostics.Record(DiagnosticLevel.Warn,
                    "OCR download", $"Transient network failure; retry {attempt} of 2.", details,
                    userMessageKey: "InstallerOcrRetry"));
            operation.Token.ThrowIfCancellationRequested();
            _viewModel.Diagnostics.Record(DiagnosticLevel.Info, "OCR installation",
                $"Tesseract {_availableOcr.Version} installed and validated.",
                userMessageKey: "InstallerOcrInstalled");
        }
        catch (OperationCanceledException) when (operation.IsCancellationRequested)
        {
            _viewModel.Diagnostics.Record(DiagnosticLevel.Info, "OCR download", "Installation cancelled.",
                userMessageKey: "InstallerOcrCancelled");
        }
        catch (TesseractDownloadException ex)
        {
            failed = true;
            var key = ex.Interrupted ? "InstallerOcrInterrupted" : "InstallerOcrNetworkError";
            _viewModel.Diagnostics.Record(DiagnosticLevel.Error, "OCR download",
                $"Download failed at {ex.Stage} after {ex.Attempts} attempts; final endpoint: {ex.Endpoint}; " +
                $"exception: {ex.InnerException?.GetType().Name}.", ex.ToString(), userMessageKey: key);
            OcrStatus.Text = Localization.Get("InstallerError");
        }
        catch (Exception ex)
        {
            failed = true;
            _viewModel.Diagnostics.Record(DiagnosticLevel.Error, "OCR installation",
                "Tesseract installation failed; previous managed installation retained where possible.",
                ex.ToString(), userMessageKey: "InstallerOcrInstallFailed");
            OcrStatus.Text = Localization.Get("InstallerError");
        }
        finally
        {
            _ocrBusy = false;
            _ocrOperation = null;
            InstallerProgress.SetCurrentValue(Avalonia.Controls.Primitives.RangeBase.ValueProperty, 0d);
            InstallerProgress.SetCurrentValue(ProgressBar.IsIndeterminateProperty,
                _viewModel.ProgressBarIndeterminate);
            InstallerProgressPercent.SetCurrentValue(Visual.IsVisibleProperty, false);
            await CheckOcrAsync();
            if (failed)
            {
                OcrStatus.Text = Localization.Get("InstallerError");
                OcrDownloadSize.IsVisible = false;
                OcrIndicator.Classes.Set("ready", false);
                OcrIndicator.Classes.Set("missing", false);
                OcrIndicator.Classes.Set("error", true);
            }
        }
    }

    private void ReportOcrProblem(string details)
    {
        if (_lastOcrProblem == details) return;
        _lastOcrProblem = details;
        _viewModel.Diagnostics.Record(DiagnosticLevel.Warn, "OCR dependency check", details,
            userMessageKey: "LogOcrDependencyUnavailable");
    }

    private async void Install_Click(object? sender, RoutedEventArgs e) =>
        await RunAsync(() => _viewModel.InstallAsync(_cancellationToken));

    private async void SelectFFmpeg_Click(object? sender, RoutedEventArgs e) =>
        await SelectPathAsync("ffmpeg");

    private async void SelectFFprobe_Click(object? sender, RoutedEventArgs e) =>
        await SelectPathAsync("ffprobe");

    private async void SelectOcr_Click(object? sender, RoutedEventArgs e)
    {
        if (_ocrBusy || _panelOperation || _viewModel.IsBusy) return;
        _panelOperation = true;
        UpdateActionAvailability();
        try
        {
            var storage = TopLevel.GetTopLevel(this)?.StorageProvider;
            if (storage is null) return;
            var files = await storage.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = Localization.Format("InstallerSelectExecutableFormat", "Tesseract OCR"),
                AllowMultiple = false
            });
            if (files.Count == 0 || !files[0].Path.IsFile) return;
            var path = files[0].Path.LocalPath;
            var engine = await SubtitleOcr.DiscoverAsync(path, null, _cancellationToken);
            if (engine is null || !string.Equals(engine.Executable, path, StringComparison.OrdinalIgnoreCase))
            {
                _viewModel.Diagnostics.Record(DiagnosticLevel.Warn, "OCR dependency check",
                    "The selected executable is not Tesseract 5.x.", userMessageKey: "InstallerOcrInvalidPath");
                return;
            }
            OcrPathSelection.Save(path);
            await CheckOcrAsync();
        }
        catch (OperationCanceledException) when (_cancellationToken.IsCancellationRequested) { }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _viewModel.Diagnostics.Record(DiagnosticLevel.Warn, "OCR dependency check",
                "Could not save the selected Tesseract path.", ex.Message,
                userMessageKey: "InstallerOcrPathSaveFailed");
        }
        finally { _panelOperation = false; UpdateActionAvailability(); }
    }

    private async Task SelectPathAsync(string name)
    {
        if (_ocrBusy || _panelOperation || _viewModel.IsBusy) return;
        _panelOperation = true;
        UpdateActionAvailability();
        try
        {
            var storage = TopLevel.GetTopLevel(this)?.StorageProvider;
            if (storage is null)
            {
                _viewModel.ReportError(Localization.Get("InstallerFilePickerUnavailable"));
                return;
            }
            var files = await storage.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = Localization.Format("InstallerSelectExecutableFormat", name),
                AllowMultiple = false
            });
            if (files.Count == 0) return;
            if (!files[0].Path.IsFile)
            {
                _viewModel.ReportError(Localization.Get("InstallerSelectLocalExecutable"));
                return;
            }
            await _viewModel.SetManualPathAsync(name, files[0].Path.LocalPath, _cancellationToken);
        }
        catch (OperationCanceledException) when (_cancellationToken.IsCancellationRequested) { }
        catch (Exception) { _viewModel.ReportError(Localization.Get("InstallerSelectFileFailed")); }
        finally { _panelOperation = false; UpdateActionAvailability(); }
    }

    private async Task RunAsync(Func<Task> action)
    {
        if (_ocrBusy || _panelOperation || _viewModel.IsBusy) return;
        _panelOperation = true;
        UpdateActionAvailability();
        try { await action(); }
        catch (OperationCanceledException) when (_cancellationToken.IsCancellationRequested) { }
        catch (OperationCanceledException) { _viewModel.ReportError(Localization.Get("InstallerOperationCancelled")); }
        catch (Exception) { _viewModel.ReportError(Localization.Get("InstallerOperationFailed")); }
        finally { _panelOperation = false; UpdateActionAvailability(); }
    }
}
