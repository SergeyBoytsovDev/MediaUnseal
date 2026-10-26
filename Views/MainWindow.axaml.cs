using System;
using System.Threading;
using Avalonia.Controls;
using TrackExtractor.ViewModels;

namespace TrackExtractor.Views;

public partial class MainWindow : Window
{
    private readonly CancellationTokenSource _shutdown = new();

    public MainWindow()
    {
        InitializeComponent();
        Opened += MainWindow_Opened;
        Closing += MainWindow_Closing;
        Closed += (_, _) => _shutdown.Cancel();
    }

    private async void MainWindow_Closing(object? sender, WindowClosingEventArgs e)
    {
        if (DataContext is not MainViewModel { IsBusy: true } viewModel) return;
        e.Cancel = true;
        _shutdown.Cancel();
        viewModel.CancelOperation();
        await viewModel.WaitForIdleAsync();
        Close();
    }

    private async void MainWindow_Opened(object? sender, EventArgs e)
    {
        Opened -= MainWindow_Opened;
        if (DataContext is not MainViewModel viewModel) return;
        try
        {
            await viewModel.CheckAsync(_shutdown.Token);
            if (!viewModel.IsReady && !_shutdown.IsCancellationRequested)
            {
                var dialog = new DependencyDialog(viewModel, _shutdown.Token);
                await dialog.ShowDialog(this);
                if (!viewModel.IsReady) Close();
            }
        }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested) { }
        catch (Exception ex)
        {
            viewModel.ReportError($"Не удалось проверить утилиты: {ex.Message}");
            var dialog = new DependencyDialog(viewModel, _shutdown.Token);
            await dialog.ShowDialog(this);
            if (!viewModel.IsReady) Close();
        }
    }
}
