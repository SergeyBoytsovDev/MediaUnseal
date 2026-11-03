using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;
using TrackExtractor.Services;

namespace TrackExtractor.Views;

public partial class DiagnosticReportWindow : Window
{
    private IReadOnlyList<DiagnosticReportRow> _rows = [];
    private DiagnosticLog? _log;
    private Action<string> _reportError = _ => { };

    public DiagnosticReportWindow()
    {
        AvaloniaXamlLoader.Load(this);
        LocalizationManager.Active.PropertyChanged += Localization_PropertyChanged;
        Opened += (_, _) => Dispatcher.UIThread.Post(ScrollToLatest, DispatcherPriority.Loaded);
        Closed += (_, _) => LocalizationManager.Active.PropertyChanged -= Localization_PropertyChanged;
    }

    private void ScrollToLatest()
    {
        this.FindControl<ListBox>("Entries")?.GetVisualDescendants()
            .OfType<ScrollViewer>().FirstOrDefault()?.ScrollToEnd();
    }

    internal DiagnosticReportWindow(IReadOnlyList<DiagnosticReportRow> rows, DiagnosticLog log,
        Action<string> reportError) : this()
    {
        _rows = rows;
        _log = log;
        _reportError = reportError;
        this.FindControl<ListBox>("Entries")!.ItemsSource = rows;
        UpdateCounts();
    }

    private void Localization_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == "Item[]") UpdateCounts();
    }

    private void UpdateCounts()
    {
        var events = _rows.Count(row => row.IsEntry);
        this.FindControl<TextBlock>("EventsCount")!.Text = LocalizationManager.Active.Format("DiagnosticEventsCount", events);
        this.FindControl<TextBlock>("WarningsCount")!.Text = LocalizationManager.Active.Format("DiagnosticWarningsCount",
            _rows.Count(row => row.IsWarn));
        this.FindControl<TextBlock>("ErrorsCount")!.Text = LocalizationManager.Active.Format("DiagnosticErrorsCount",
            _rows.Count(row => row.IsError));
    }

    private async void Clear_Click(object? sender, RoutedEventArgs e)
    {
        if (_log is null) return;
        try
        {
            await Task.Run(_log.Clear);
            _rows = [];
            this.FindControl<ListBox>("Entries")!.ItemsSource = _rows;
            UpdateCounts();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { _reportError(LocalizationManager.Active.Get("DiagnosticPreviewClearFailed")); }
    }

    private async void Save_Click(object? sender, RoutedEventArgs e)
    {
        try
        {
            var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = LocalizationManager.Active.Get("DiagnosticPreviewSaveTitle"),
                SuggestedFileName = "TrackExtractor-diagnostic.txt",
                FileTypeChoices = [new FilePickerFileType("TXT") { Patterns = ["*.txt"] }]
            });
            if (file is null) return;
            await using var stream = await file.OpenWriteAsync();
            stream.SetLength(0);
            await using var writer = new StreamWriter(stream);
            await writer.WriteAsync(DiagnosticReportPresentation.ToText(_rows));
            Close();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        { _reportError(LocalizationManager.Active.Get("DiagnosticPreviewSaveFailed")); }
    }
}
