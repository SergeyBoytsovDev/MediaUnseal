using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Resources;
using System.Text;
using System.Text.RegularExpressions;

namespace TrackExtractor.Services;

internal static class LanguagePackCatalog
{
    private static readonly Assembly Assembly = typeof(LocalizationManager).Assembly;
    private static readonly ResourceManager Strings = new("TrackExtractor.Resources.Strings", Assembly);
    private static readonly Lazy<IReadOnlyDictionary<string, string>> BaseStrings = new(() =>
        Read(Strings.GetResourceSet(CultureInfo.InvariantCulture, true, true)
            ?? throw new MissingManifestResourceException("Base localization is missing.")));
    private static readonly Lazy<string[]> PackagedLanguages = new(FindPackagedLanguages);
    private static readonly Lazy<IReadOnlySet<string>> GuideResources = new(() =>
        Assembly.GetManifestResourceNames().ToHashSet(StringComparer.Ordinal));

    internal static IReadOnlyDictionary<string, string> RussianStrings => BaseStrings.Value;
    internal static string[] AvailableLanguages => PackagedLanguages.Value;

    internal static IReadOnlyList<string> ValidateInterface(string cultureCode,
        IReadOnlyDictionary<string, string> translations)
    {
        var problems = new List<string>();
        CultureInfo culture;
        try { culture = CultureInfo.GetCultureInfo(cultureCode); }
        catch (CultureNotFoundException) { return ["invalid-culture"]; }
        if (culture.Name.Length == 0 || !string.Equals(culture.Name, cultureCode, StringComparison.OrdinalIgnoreCase)
            || !Regex.IsMatch(culture.Name, @"\A[a-z]{2,3}(?:-[A-Za-z0-9]{2,8})*\z"))
            problems.Add("invalid-culture");
        var source = BaseStrings.Value;
        foreach (var (key, template) in source)
        {
            if (!translations.TryGetValue(key, out var translated))
            { problems.Add("missing-key:" + key); continue; }
            if (string.IsNullOrWhiteSpace(translated))
            { problems.Add("empty-key:" + key); continue; }
            try
            {
                if (!Placeholders(template).SequenceEqual(Placeholders(translated)))
                    problems.Add("placeholder-mismatch:" + key);
            }
            catch (FormatException) { problems.Add("invalid-template:" + key); }
        }
        foreach (var key in translations.Keys.Except(source.Keys, StringComparer.Ordinal))
            problems.Add("unexpected-key:" + key);
        return problems;
    }

    internal static IReadOnlyList<string> ValidateGuideFiles(string cultureCode,
        IReadOnlySet<string>? resources = null)
    {
        resources ??= GuideResources.Value;
        var guideName = cultureCode == LocalizationManager.DefaultLanguage
            ? "TrackExtractor.docs.track-extractor-media-guide.md"
            : $"TrackExtractor.docs.track-extractor-media-guide.{cultureCode}.md";
        var diagramsName = $"TrackExtractor.docs.media-guide-diagrams.{cultureCode}.json";
        var problems = new List<string>();
        if (!resources.Contains(guideName)) problems.Add("missing-guide");
        if (!resources.Contains(diagramsName)) problems.Add("missing-diagrams");
        return problems;
    }

    internal static string GuideLanguageFor(string language, IReadOnlySet<string>? resources = null) =>
        ValidateGuideFiles(language, resources).Count == 0 ? language : LocalizationManager.DefaultLanguage;

    private static int[] Placeholders(string template)
    {
        _ = CompositeFormat.Parse(template);
        var withoutEscapes = template.Replace("{{", "").Replace("}}", "");
        return Regex.Matches(withoutEscapes, @"\{(\d+)(?:,[^}:]+)?(?::[^}]*)?\}")
            .Select(match => int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture))
            .Distinct().Order().ToArray();
    }

    private static string[] FindPackagedLanguages()
    {
        var ready = new List<string> { LocalizationManager.DefaultLanguage };
        string[] directories;
        try { directories = Directory.GetDirectories(AppContext.BaseDirectory); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { return ready.ToArray(); }
        foreach (var directory in directories)
        {
            var code = Path.GetFileName(directory);
            if (!File.Exists(Path.Combine(directory, Assembly.GetName().Name + ".resources.dll"))) continue;
            try
            {
                var culture = CultureInfo.GetCultureInfo(code);
                var exact = Strings.GetResourceSet(culture, true, false);
                if (exact is not null && ValidateInterface(code, Read(exact)).Count == 0)
                    ready.Add(culture.Name);
            }
            catch (Exception ex) when (ex is CultureNotFoundException or MissingManifestResourceException
                or MissingSatelliteAssemblyException or IOException) { }
        }
        return ready.Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.Ordinal).ToArray();
    }

    private static IReadOnlyDictionary<string, string> Read(ResourceSet set) =>
        set.Cast<DictionaryEntry>().Where(entry => entry.Key is string && entry.Value is string)
            .ToDictionary(entry => (string)entry.Key, entry => (string)entry.Value!, StringComparer.Ordinal);
}
