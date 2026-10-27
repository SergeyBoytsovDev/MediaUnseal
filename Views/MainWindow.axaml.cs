using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Platform.Storage;
using TrackExtractor.ViewModels;

namespace TrackExtractor.Views;

public partial class MainWindow : Window
{
    private readonly CancellationTokenSource _shutdown = new();
    private DependencyDialog? _dependencyDialog;

    public MainWindow()
    {
        InitializeComponent();
        Opened += MainWindow_Opened;
        Closing += MainWindow_Closing;
        Closed += (_, _) => _shutdown.Cancel();
        DragDrop.SetAllowDrop(this, true);
        DragDrop.AddDragOverHandler(this, DragOver);
        DragDrop.AddDropHandler(this, Drop);
    }

    private async void MainWindow_Closing(object? sender, WindowClosingEventArgs e)
    {
        if (DataContext is not MainViewModel viewModel ||
            (!viewModel.IsBusy && !viewModel.HasActiveAnalyses && !viewModel.IsExporting)) return;
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
        if (DataContext is not MainViewModel viewModel) return;
        try
        {
            await viewModel.CheckAsync(_shutdown.Token);
            if (!viewModel.IsReady && !_shutdown.IsCancellationRequested) ShowDependencyDialog();
        }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested) { }
        catch (Exception ex)
        {
            viewModel.ReportError($"Не удалось проверить утилиты: {ex.Message}");
            if (!_shutdown.IsCancellationRequested) ShowDependencyDialog();
        }
    }

    private void Dependencies_Click(object? sender, RoutedEventArgs e) => ShowDependencyDialog();

    private void ShowDependencyDialog()
    {
        if (_shutdown.IsCancellationRequested || DataContext is not MainViewModel viewModel) return;
        if (_dependencyDialog is not null) { _dependencyDialog.Activate(); return; }
        var dialog = new DependencyDialog(viewModel, _shutdown.Token);
        _dependencyDialog = dialog;
        dialog.Closed += (_, _) => _dependencyDialog = null;
        dialog.Show(this);
    }

    private async void SelectFile_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel viewModel || !viewModel.SelectFile()) return;
        try
        {
            var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = "Выберите медиафайл", AllowMultiple = false
            });
            if (files.Count == 1) await AddFileAsync(files[0].Path);
        }
        catch (Exception)
        {
            if (!_shutdown.IsCancellationRequested)
                viewModel.ReportFileError("Не удалось выбрать файл. Проверьте доступ и повторите попытку.");
        }
    }

    private void DragOver(object? sender, DragEventArgs e) => e.DragEffects = DragDropEffects.Copy;

    private async void Drop(object? sender, DragEventArgs e)
    {
        if (DataContext is not MainViewModel viewModel || !viewModel.SelectFile()) return;
        try
        {
            var files = e.DataTransfer.TryGetFiles()?.Take(2).ToArray();
            if (files is null || files.Length != 1)
            {
                viewModel.ReportFileError("Перетащите один локальный файл.");
                return;
            }
            await AddFileAsync(files[0].Path);
        }
        catch (Exception)
        {
            if (!_shutdown.IsCancellationRequested)
                viewModel.ReportFileError("Не удалось открыть перетащенный файл.");
        }
    }

    private async Task AddFileAsync(Uri uri)
    {
        if (DataContext is not MainViewModel viewModel) return;
        if (!uri.IsFile) { viewModel.ReportFileError("Выберите локальный файл."); return; }
        await viewModel.AnalyzeFileAsync(uri.LocalPath, _shutdown.Token);
    }

    private async void ChooseFolder_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel viewModel || viewModel.IsExporting) return;
        try
        {
            var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
            {
                Title = "Выберите папку сохранения", AllowMultiple = false
            });
            if (folders.Count == 1)
            {
                var path = folders[0].TryGetLocalPath();
                if (string.IsNullOrWhiteSpace(path) && folders[0].Path.IsFile)
                    path = folders[0].Path.LocalPath;
                if (!string.IsNullOrWhiteSpace(path)) viewModel.SetOutputDirectory(path);
                else viewModel.ReportExportError("Выберите локальную папку сохранения.");
            }
        }
        catch (Exception)
        {
            if (!_shutdown.IsCancellationRequested)
                viewModel.ReportExportError("Не удалось выбрать папку. Проверьте доступ к ней.");
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

    private async void SaveLog_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel viewModel) return;
        try
        {
            var report = await Task.Run(viewModel.DiagnosticReport);
            var preview = new Window { Title = "Диагностический отчёт", Width = 760, Height = 540,
                WindowStartupLocation = WindowStartupLocation.CenterOwner };
            var panel = new DockPanel { Margin = new Avalonia.Thickness(12) };
            var save = new Button { Content = "Сохранить", HorizontalAlignment = HorizontalAlignment.Right };
            DockPanel.SetDock(save, Dock.Bottom);
            panel.Children.Add(save);
            panel.Children.Add(new ScrollViewer
            {
                HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
                VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
                Content = new TextBox { Text = report, IsReadOnly = true,
                    AcceptsReturn = true, TextWrapping = Avalonia.Media.TextWrapping.NoWrap }
            });
            preview.Content = panel;
            save.Click += async (_, _) =>
            {
                try
                {
                    var file = await preview.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
                    {
                        Title = "Сохранить диагностический отчёт",
                        SuggestedFileName = "TrackExtractor-diagnostic.txt"
                    });
                    if (file is null) return;
                    await using var stream = await file.OpenWriteAsync();
                    stream.SetLength(0);
                    await using var writer = new StreamWriter(stream);
                    await writer.WriteAsync(report);
                    preview.Close();
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
                {
                    viewModel.ReportError("Не удалось сохранить лог. Проверьте доступ к выбранному файлу.");
                }
            };
            preview.Show(this);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            viewModel.ReportError("Не удалось прочитать диагностический лог.");
        }
    }
}
