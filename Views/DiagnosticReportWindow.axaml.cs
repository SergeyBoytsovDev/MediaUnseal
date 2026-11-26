using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MediaUnseal.Services;

namespace MediaUnseal.Views;

public partial class DiagnosticReportWindow : Window
{
    private IReadOnlyList<DiagnosticReportRow> _rows = [];
    private DiagnosticLog? _log;
    private Action<string> _reportError = _ => { };

    public DiagnosticReportWindow()
    {
        AvaloniaXamlLoader.Load(this);
        this.FindControl<ListBox>("Entries")!.AddHandler(InputElement.PointerPressedEvent,
            Entries_PointerPressed, RoutingStrategies.Tunnel, handledEventsToo: true);
        UiScale.Attach(this);
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

    private void Entries_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(this).Properties.IsRightButtonPressed) return;
        var item = (e.Source as Avalonia.Visual)?.GetVisualAncestors()
            .OfType<ListBoxItem>().FirstOrDefault();
        if (item?.DataContext is not DiagnosticReportRow { IsEntry: true } row) return;
        var entries = this.FindControl<ListBox>("Entries")!;
        if (entries.SelectedItems?.Contains(row) == true) return;
        entries.SelectedItems?.Clear();
        entries.SelectedItems?.Add(row);
    }

    private void Entries_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        var entries = this.FindControl<ListBox>("Entries")!;
        var copy = entries.ContextMenu?.Items.OfType<MenuItem>().FirstOrDefault();
        if (copy is not null)
            copy.IsEnabled = entries.SelectedItems?.OfType<DiagnosticReportRow>().Any(row => row.IsEntry) == true;
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.C && e.KeyModifiers.HasFlag(KeyModifiers.Control)
            && e.Source is not TextBox
            && (e.Source as Avalonia.Visual)?.GetVisualAncestors().OfType<TextBox>().Any() != true)
        {
            var entries = this.FindControl<ListBox>("Entries")!;
            if (entries.SelectedItems?.OfType<DiagnosticReportRow>().Any(row => row.IsEntry) == true)
            { e.Handled = true; _ = CopyEntriesAsync(); return; }
        }
        base.OnKeyDown(e);
    }

    private async void CopyEntries_Click(object? sender, RoutedEventArgs e) => await CopyEntriesAsync();

    private async Task CopyEntriesAsync()
    {
        var entries = this.FindControl<ListBox>("Entries")!;
        var selected = entries.SelectedItems?.OfType<DiagnosticReportRow>() ?? [];
        var text = DiagnosticReportPresentation.CopySelected(_rows, selected);
        if (text.Length > 0 && Clipboard is not null) await Clipboard.SetTextAsync(text);
    }

    private async void Clear_Click(object? sender, RoutedEventArgs e)
    {
        if (_log is null) return;
        try
        {
            await Task.Run(_log.Clear);
            _rows = await Task.Run(() => DiagnosticReportPresentation.Parse(_log.ReadReportLines()));
            this.FindControl<ListBox>("Entries")!.ItemsSource = _rows;
            UpdateCounts();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { _log.Record(DiagnosticLevel.Warn, "Diagnostic storage",
            $"Log clear refresh failed: {ex.GetType().Name}; HResult 0x{ex.HResult:X8}."); }
    }

    private async void Save_Click(object? sender, RoutedEventArgs e)
    {
        try
        {
            var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = LocalizationManager.Active.Get("DiagnosticPreviewSaveTitle"),
                SuggestedFileName = "MediaUnseal-diagnostic.txt",
                FileTypeChoices = [new FilePickerFileType("TXT") { Patterns = ["*.txt"] }]
            });
            if (file is null) return;
            await using var stream = await file.OpenWriteAsync();
            stream.SetLength(0);
            await using var writer = new StreamWriter(stream);
            var currentRows = _log is null ? _rows : await Task.Run(() => RowsForExport(_log));
            foreach (var line in DiagnosticReportPresentation.TextLines(currentRows))
                await writer.WriteLineAsync(line);
            Close();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            _log?.Record(DiagnosticLevel.Error, "Diagnostic report",
                "Report save failed.", DiagnosticLog.SafeError(ex));
            _reportError(LocalizationManager.Active.Get("DiagnosticPreviewSaveFailed"));
        }
    }

    internal static IReadOnlyList<DiagnosticReportRow> RowsForExport(DiagnosticLog log) =>
        DiagnosticReportPresentation.Parse(log.ReadReportLines());
}
