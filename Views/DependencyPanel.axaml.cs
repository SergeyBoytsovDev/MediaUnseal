using System;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using TrackExtractor.Services;
using TrackExtractor.ViewModels;

namespace TrackExtractor.Views;

public partial class DependencyPanel : UserControl
{
    private readonly MainViewModel _viewModel;
    private readonly CancellationToken _cancellationToken;
    private readonly LocalizationManager _fallbackLocalization = new();
    private LocalizationManager Localization => (Application.Current as App)?.Localization ?? _fallbackLocalization;
    public event EventHandler? ContinueRequested;

    public DependencyPanel() : this(new MainViewModel(), CancellationToken.None) { }

    public DependencyPanel(MainViewModel viewModel, CancellationToken cancellationToken)
    {
        InitializeComponent();
        _viewModel = viewModel;
        _cancellationToken = cancellationToken;
        DataContext = viewModel;
    }

    internal void SetShowContinue(bool value) => ContinueArea.IsVisible = value;

    private void Cancel_Click(object? sender, RoutedEventArgs e) => _viewModel.CancelOperation();

    private void Continue_Click(object? sender, RoutedEventArgs e)
    {
        if (_viewModel.IsReady) ContinueRequested?.Invoke(this, EventArgs.Empty);
    }

    private async void Retry_Click(object? sender, RoutedEventArgs e) =>
        await RunAsync(() => _viewModel.CheckAsync(_cancellationToken));

    private async void Install_Click(object? sender, RoutedEventArgs e) =>
        await RunAsync(() => _viewModel.InstallAsync(_cancellationToken));

    private async void SelectFFmpeg_Click(object? sender, RoutedEventArgs e) =>
        await SelectPathAsync("ffmpeg");

    private async void SelectFFprobe_Click(object? sender, RoutedEventArgs e) =>
        await SelectPathAsync("ffprobe");

    private async Task SelectPathAsync(string name)
    {
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
            await RunAsync(() => _viewModel.SetManualPathAsync(name, files[0].Path.LocalPath, _cancellationToken));
        }
        catch (OperationCanceledException) when (_cancellationToken.IsCancellationRequested) { }
        catch (Exception) { _viewModel.ReportError(Localization.Get("InstallerSelectFileFailed")); }
    }

    private async Task RunAsync(Func<Task> action)
    {
        try { await action(); }
        catch (OperationCanceledException) when (_cancellationToken.IsCancellationRequested) { }
        catch (OperationCanceledException) { _viewModel.ReportError(Localization.Get("InstallerOperationCancelled")); }
        catch (Exception) { _viewModel.ReportError(Localization.Get("InstallerOperationFailed")); }
    }
}
