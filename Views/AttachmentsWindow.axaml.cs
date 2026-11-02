using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Interactivity;
using TrackExtractor.Models;
using TrackExtractor.Services;
using TrackExtractor.ViewModels;

namespace TrackExtractor.Views;

public partial class AttachmentsWindow : Window
{
    private readonly AttachmentChoiceViewModel[] _choices;

    public AttachmentsWindow() : this([], []) { }

    public AttachmentsWindow(IReadOnlyList<AttachmentFile> files, IEnumerable<ulong> selected)
    {
        InitializeComponent();
        var chosen = selected.ToHashSet();
        _choices = files.Select(file => new AttachmentChoiceViewModel(file, chosen.Contains(file.Uid))).ToArray();
        foreach (var choice in _choices) choice.PropertyChanged += ChoiceChanged;
        FilesList.ItemsSource = _choices;
        RefreshCount();
        LocalizationManager.Active.PropertyChanged += LocalizationChanged;
        Closed += (_, _) => LocalizationManager.Active.PropertyChanged -= LocalizationChanged;
    }

    private void ChoiceChanged(object? sender, PropertyChangedEventArgs e) => RefreshCount();
    private void LocalizationChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != "Item[]") return;
        foreach (var choice in _choices) choice.RefreshLocalization();
        RefreshCount();
    }
    private void RefreshCount() => SelectionCount.Text = LocalizationManager.Active.Format(
        "AttachmentSelectedCount", _choices.Count(choice => choice.IsSelected), _choices.Length);
    private void Cancel_Click(object? sender, RoutedEventArgs e) => Close(null);
    private void Apply_Click(object? sender, RoutedEventArgs e) =>
        Close(_choices.Where(choice => choice.IsSelected).Select(choice => choice.File.Uid).ToHashSet());
}
