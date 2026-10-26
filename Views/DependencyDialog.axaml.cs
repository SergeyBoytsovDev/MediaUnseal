using System;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using TrackExtractor.ViewModels;

namespace TrackExtractor.Views;

public partial class DependencyDialog : Window
{
    private readonly MainViewModel _viewModel;
    private readonly CancellationToken _cancellationToken;

    public DependencyDialog() : this(new MainViewModel(), CancellationToken.None) { }

    public DependencyDialog(MainViewModel viewModel, CancellationToken cancellationToken)
    {
        InitializeComponent();
        _viewModel = viewModel;
        _cancellationToken = cancellationToken;
        DataContext = viewModel;
        Closing += Dialog_Closing;
    }

    private async void Dialog_Closing(object? sender, WindowClosingEventArgs e)
    {
        if (!_viewModel.IsBusy) return;
        e.Cancel = true;
        _viewModel.CancelOperation();
        await _viewModel.WaitForIdleAsync();
        Close();
    }

    private void Cancel_Click(object? sender, RoutedEventArgs e) => _viewModel.CancelOperation();

    private void Continue_Click(object? sender, RoutedEventArgs e)
    {
        if (_viewModel.IsReady) Close();
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
            var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = $"Выберите исполняемый файл {name}",
                AllowMultiple = false
            });
            if (files.Count == 0) return;
            if (!files[0].Path.IsFile)
            {
                _viewModel.ReportError("Выберите локальный исполняемый файл.");
                return;
            }
            await RunAsync(() => _viewModel.SetManualPathAsync(name, files[0].Path.LocalPath, _cancellationToken));
        }
        catch (OperationCanceledException) when (_cancellationToken.IsCancellationRequested) { }
        catch (Exception ex) { _viewModel.ReportError($"Не удалось выбрать файл: {ex.Message}"); }
    }

    private async Task RunAsync(Func<Task> action)
    {
        try { await action(); }
        catch (OperationCanceledException) when (_cancellationToken.IsCancellationRequested) { }
        catch (OperationCanceledException) { _viewModel.ReportError("Операция отменена. Можно повторить проверку или установку."); }
        catch (Exception ex) { _viewModel.ReportError($"Не удалось завершить операцию: {ex.Message}"); }
    }
}
