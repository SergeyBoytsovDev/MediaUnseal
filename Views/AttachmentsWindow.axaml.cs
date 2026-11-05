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
        UiScale.Attach(this);
        Height = Math.Clamp(210 + Math.Min(files.Count, 9) * 37, 300, 550);
        var chosen = selected.ToHashSet();
        _choices = files.Select(file => new AttachmentChoiceViewModel(file, chosen.Contains(file.Uid))).ToArray();
        foreach (var choice in _choices) choice.PropertyChanged += ChoiceChanged;
        FilesList.ItemsSource = _choices;
        RefreshCount();
        LocalizationManager.Active.PropertyChanged += LocalizationChanged;
        Closed += (_, _) =>
        {
            LocalizationManager.Active.PropertyChanged -= LocalizationChanged;
            foreach (var choice in _choices) choice.PropertyChanged -= ChoiceChanged;
        };
    }

    public HashSet<ulong> SelectedAttachmentUids =>
        _choices.Where(choice => choice.IsSelected).Select(choice => choice.File.Uid).ToHashSet();

    private void ChoiceChanged(object? sender, PropertyChangedEventArgs e) => RefreshCount();
    private void LocalizationChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != "Item[]") return;
        foreach (var choice in _choices) choice.RefreshLocalization();
        RefreshCount();
    }
    private void RefreshCount()
    {
        FoundCount.Text = LocalizationManager.Active.Format("AttachmentFoundCount", _choices.Length);
        SelectionCount.Text = LocalizationManager.Active.Format(
            "AttachmentSelectedCount", _choices.Count(choice => choice.IsSelected), _choices.Length);
    }
    private void Close_Click(object? sender, RoutedEventArgs e) => Close();
}
