using System;
using System.Collections.Generic;
using System.Linq;
using ColorDocument.Avalonia;
using ColorDocument.Avalonia.DocumentElements;

namespace MediaUnseal.Services;

internal sealed record MediaGuideMatch(
    MediaGuideSection Section, MediaGuideArticle Article, int BlockIndex,
    int Start, int Length, string Text, string Snippet, string? DiagramId = null, string? NodeId = null);

internal sealed record MediaGuideSearchResult(
    MediaGuideSection Section, MediaGuideArticle Article, IReadOnlyList<MediaGuideMatch> Matches)
{
    internal MediaGuideMatch First => Matches[0];
}

internal sealed class MediaGuideSearch
{
    private sealed record SearchBlock(string Text, int BlockIndex, string? DiagramId = null, string? NodeId = null);
    private sealed record ArticleIndex(MediaGuideSection Section, MediaGuideArticle Article,
        string[] Blocks, SearchBlock[] SearchBlocks);
    private readonly IReadOnlyList<ArticleIndex> _articles;

    internal MediaGuideSearch(MediaGuideContent guide, MediaGuideDiagramCatalog? diagrams = null)
    {
        diagrams ??= MediaGuideDiagramCatalog.Load();
        var renderer = new Markdown.Avalonia.Markdown();
        var articles = new List<ArticleIndex>();
        foreach (var section in guide.Sections)
        foreach (var article in section.Articles)
        {
            var blocks = new List<string>();
            var searchable = new List<SearchBlock>();
            foreach (var text in ReadBlocks(renderer.TransformElement(article.Markdown)))
            {
                var diagramId = MediaGuideDiagramCatalog.MarkerId(text);
                if (diagramId is not null)
                {
                    foreach (var node in diagrams.Get(diagramId).Nodes)
                        searchable.Add(new SearchBlock(node.Label, -1, diagramId, node.Id));
                }
                else
                {
                    searchable.Add(new SearchBlock(text, blocks.Count));
                    blocks.Add(text);
                }
            }
            articles.Add(new ArticleIndex(section, article, blocks.ToArray(), searchable.ToArray()));
        }
        _articles = articles;
    }

    internal IReadOnlyList<MediaGuideSearchResult> Find(string? query)
    {
        if (string.IsNullOrWhiteSpace(query)) return [];
        query = query.Trim();
        var results = new List<MediaGuideSearchResult>();
        foreach (var entry in _articles)
        {
            var matches = new List<MediaGuideMatch>();
            foreach (var block in entry.SearchBlocks)
            {
                var text = block.Text;
                for (var offset = 0; offset < text.Length;)
                {
                    var start = text.IndexOf(query, offset, StringComparison.OrdinalIgnoreCase);
                    if (start < 0) break;
                    matches.Add(new MediaGuideMatch(entry.Section, entry.Article, block.BlockIndex, start,
                        query.Length, text.Substring(start, query.Length), Snippet(text, start, query.Length),
                        block.DiagramId, block.NodeId));
                    offset = start + query.Length;
                }
            }
            if (matches.Count > 0)
                results.Add(new MediaGuideSearchResult(entry.Section, entry.Article, matches));
        }
        return results;
    }

    internal IReadOnlyList<string> BlocksFor(MediaGuideArticle article) =>
        _articles.First(entry => ReferenceEquals(entry.Article, article)).Blocks;

    private static IEnumerable<string> ReadBlocks(DocumentElement element)
    {
        if (element is CTextBlockElement textBlock)
            yield return textBlock.Text;
        foreach (var child in element.Children)
            foreach (var text in ReadBlocks(child))
                yield return text;
    }

    private static string Snippet(string text, int start, int length)
    {
        var from = Math.Max(0, start - 42);
        var to = Math.Min(text.Length, start + length + 58);
        var excerpt = string.Join(' ', text[from..to].Split((char[]?)null,
            StringSplitOptions.RemoveEmptyEntries));
        return (from > 0 ? "…" : "") + excerpt + (to < text.Length ? "…" : "");
    }
}
