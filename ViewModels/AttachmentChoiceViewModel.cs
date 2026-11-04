using System.IO;
using TrackExtractor.Models;
using TrackExtractor.Services;

namespace TrackExtractor.ViewModels;

public sealed class AttachmentChoiceViewModel(AttachmentFile file, bool selected) : ViewModelBase
{
    private bool _selected = selected;
    public AttachmentFile File { get; } = file;
    public bool IsSelected { get => _selected; set => SetProperty(ref _selected, value); }
    public string Name => File.Name;
    public string TypeDisplay => string.IsNullOrWhiteSpace(Path.GetExtension(File.Name))
        ? "—" : Path.GetExtension(File.Name).TrimStart('.').ToUpperInvariant();
    public string MimeDisplay => string.IsNullOrWhiteSpace(File.Mime) ? "—" : File.Mime;
    public string SizeDisplay => TrackExporter.FormatByteSize(File.Size);
    public string Description => File.Description ?? "—";
    public void RefreshLocalization() => OnPropertyChanged(nameof(SizeDisplay));
}
