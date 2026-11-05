using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Avalonia.Platform;

namespace MediaUnseal.Services;

internal sealed record MediaGuideArticle(string Title, string Markdown, int SourceStart, int SourceLength);
internal sealed record MediaGuideSection(string Title, IReadOnlyList<MediaGuideArticle> Articles);

internal sealed class MediaGuideContent
{
    private static readonly Regex HeadingPattern = new(@"^(##|###) ([^\r\n]+)\r?$", RegexOptions.Multiline | RegexOptions.Compiled);

    internal string Source { get; }
    internal IReadOnlyList<MediaGuideSection> Sections { get; }

    private MediaGuideContent(string source, IReadOnlyList<MediaGuideSection> sections)
    {
        Source = source;
        Sections = sections;
    }

    internal static MediaGuideContent Load(string language = LocalizationManager.DefaultLanguage)
    {
        if (LanguagePackCatalog.ValidateGuideFiles(language).Count != 0)
            throw new InvalidDataException($"Недоступен язык медиасправочника: {language}.");
        var name = language == LocalizationManager.DefaultLanguage
            ? "media-unseal-media-guide.md" : $"media-unseal-media-guide.{language}.md";
        using var stream = AssetLoader.Open(new Uri($"avares://MediaUnseal/docs/{name}"));
        using var reader = new StreamReader(stream);
        return Parse(reader.ReadToEnd());
    }

    internal static MediaGuideContent Parse(string source)
    {
        ArgumentNullException.ThrowIfNull(source);
        var matches = HeadingPattern.Matches(source);
        var sections = new List<MediaGuideSection>();
        var articles = new List<MediaGuideArticle>();
        string? sectionTitle = null;
        var prefixStart = 0;
        var articleStart = -1;
        string? articleTitle = null;

        void FinishArticle(int end)
        {
            if (articleTitle is null) return;
            var markdown = source.Substring(articleStart, end - articleStart);
            // Keep the guide preface and any text before the first article of a section.
            if (articles.Count == 0)
                markdown = source.Substring(prefixStart, articleStart - prefixStart) + markdown;
            articles.Add(new MediaGuideArticle(articleTitle, markdown, articleStart, end - articleStart));
        }

        void FinishSection()
        {
            if (sectionTitle is null) return;
            sections.Add(new MediaGuideSection(sectionTitle, articles.ToArray()));
            articles.Clear();
        }

        foreach (Match match in matches)
        {
            if (match.Groups[1].Value == "##")
            {
                FinishArticle(match.Index);
                FinishSection();
                sectionTitle = match.Groups[2].Value.Trim();
                prefixStart = sections.Count == 0 ? 0 : match.Index;
                articleTitle = null;
            }
            else if (sectionTitle is not null)
            {
                FinishArticle(match.Index);
                articleStart = match.Index;
                articleTitle = match.Groups[2].Value.Trim();
            }
        }
        FinishArticle(source.Length);
        FinishSection();
        if (sections.Count == 0 || sections.Any(section => section.Articles.Count == 0))
            throw new InvalidDataException("Медиасправочник не содержит ожидаемые разделы и статьи.");
        return new MediaGuideContent(source, sections);
    }
}
