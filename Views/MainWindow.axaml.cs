using System;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;
using TrackExtractor.Services;
using TrackExtractor.ViewModels;

namespace TrackExtractor.Views;

public partial class MainWindow : Window
{
    private readonly CancellationTokenSource _shutdown = new();
    private SettingsWindow? _settingsWindow;
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
                StreamsTable.Columns[i].Width = new GridLength(appearance.Preferences.ColumnWidths[i]);
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
        Opened += MainWindow_Opened;
        Closing += MainWindow_Closing;
        Closed += (_, _) =>
        {
            _shutdown.Cancel();
            if (_logViewModel is not null) _logViewModel.UserEvents.CollectionChanged -= Events_CollectionChanged;
        };
        DataContextChanged += (_, _) =>
        {
            if (_logViewModel is not null) _logViewModel.UserEvents.CollectionChanged -= Events_CollectionChanged;
            _logViewModel = DataContext as MainViewModel;
            if (_logViewModel is not null) _logViewModel.UserEvents.CollectionChanged += Events_CollectionChanged;
        };
        DragDrop.SetAllowDrop(DropZone, true);
        DragDrop.AddDragOverHandler(DropZone, DragOver);
        DragDrop.AddDropHandler(DropZone, Drop);
        DragDrop.AddDragLeaveHandler(DropZone, (_, _) => DropZone.Classes.Remove("dragging"));
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
            viewModel.ReportError($"Не удалось проверить утилиты: {ex.Message}");
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
            .Select((column, index) => double.IsFinite(column.ActualWidth)
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

    private void StreamsTable_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(StreamsTable).Properties.IsRightButtonPressed) return;
        if (e.Source is Avalonia.Visual visual && visual.FindAncestorOfType<TableViewRow>() is { } row)
            StreamsTable.SelectedItem = row.DataContext;
    }

    private void Properties_Click(object? sender, RoutedEventArgs e)
    {
        if (StreamsTable.SelectedItem is ExportTrackViewModel track)
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

    private void DragOver(object? sender, DragEventArgs e)
    {
        e.DragEffects = DataContext is MainViewModel { CanChooseFile: true }
            ? DragDropEffects.Copy : DragDropEffects.None;
        DropZone.Classes.Set("dragging", e.DragEffects == DragDropEffects.Copy);
    }

    private async void Drop(object? sender, DragEventArgs e)
    {
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
                if (!string.IsNullOrWhiteSpace(path))
                {
                    var previous = viewModel.OutputDirectory;
                    viewModel.SetOutputDirectory(path);
                    if (Appearance is { } appearance && viewModel.OutputDirectory is { } selected
                        && !string.Equals(selected, previous, OperatingSystem.IsWindows()
                            ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
                    {
                        appearance.Preferences.OutputDirectory = selected;
                        appearance.Save();
                    }
                }
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
            viewModel.ReportExportError("Папка результатов не найдена. Выберите существующую папку.");
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
            viewModel.ReportExportError("Не удалось открыть папку результатов. Проверьте доступ к ней.");
        }
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
