using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using Avalonia.Platform;

namespace MediaUnseal.Services;

internal sealed class MediaGuideDiagramCatalog
{
    private static readonly Regex Marker = new(@"^\{\{diagram:([a-z0-9-]+)\}\}$", RegexOptions.Compiled);
    private readonly Dictionary<string, MediaGuideDiagram> _diagrams;

    private MediaGuideDiagramCatalog(IEnumerable<MediaGuideDiagram> diagrams)
    {
        _diagrams = diagrams.ToDictionary(diagram => diagram.Id, StringComparer.Ordinal);
        foreach (var diagram in _diagrams.Values)
        {
            if (diagram.Width <= 0 || diagram.Height <= 0 || diagram.Nodes.Count == 0)
                throw new InvalidDataException($"Invalid geometry in diagram {diagram.Id}.");
            var ids = diagram.Nodes.Select(node => node.Id).ToHashSet(StringComparer.Ordinal);
            if (ids.Count != diagram.Nodes.Count || diagram.Links.Any(link => !ids.Contains(link.From) || !ids.Contains(link.To)))
                throw new InvalidDataException($"Invalid links in diagram {diagram.Id}.");
            if (diagram.Nodes.Any(node => string.IsNullOrWhiteSpace(node.Label) || node.Width <= 0 || node.Height <= 0 ||
                node.X < 0 || node.Y < 0 || node.X + node.Width > diagram.Width || node.Y + node.Height > diagram.Height))
                throw new InvalidDataException($"Invalid nodes in diagram {diagram.Id}.");
        }
    }

    internal static MediaGuideDiagramCatalog Load(string language = LocalizationManager.DefaultLanguage)
    {
        if (LanguagePackCatalog.ValidateGuideFiles(language).Count != 0)
            throw new InvalidDataException($"Media guide diagram language is unavailable: {language}.");
        using var stream = AssetLoader.Open(new Uri($"avares://MediaUnseal/docs/media-guide-diagrams.{language}.json"));
        var data = JsonSerializer.Deserialize<MediaGuideDiagramFile>(stream,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        if (data?.Diagrams is null) throw new InvalidDataException("Could not load the media guide diagrams.");
        return new MediaGuideDiagramCatalog(data.Diagrams);
    }

    internal static string? MarkerId(string text)
    {
        var match = Marker.Match(text.Trim());
        return match.Success ? match.Groups[1].Value : null;
    }

    internal MediaGuideDiagram Get(string id) => _diagrams.TryGetValue(id, out var diagram)
        ? diagram : throw new InvalidDataException($"Unknown media guide diagram: {id}.");
}

internal sealed class MediaGuideDiagramFile
{
    public List<MediaGuideDiagram> Diagrams { get; set; } = [];
}

internal sealed class MediaGuideDiagram
{
    public string Id { get; set; } = "";
    public double Width { get; set; }
    public double Height { get; set; }
    public List<MediaGuideDiagramNode> Nodes { get; set; } = [];
    public List<MediaGuideDiagramLink> Links { get; set; } = [];
}

internal sealed class MediaGuideDiagramNode
{
    public string Id { get; set; } = "";
    public string Label { get; set; } = "";
    public string Kind { get; set; } = "node";
    public double X { get; set; }
    public double Y { get; set; }
    public double Width { get; set; }
    public double Height { get; set; }
}

internal sealed class MediaGuideDiagramLink
{
    public string From { get; set; } = "";
    public string To { get; set; } = "";
}

internal sealed record MediaGuideDiagramLabel(string DiagramId, string NodeId);
