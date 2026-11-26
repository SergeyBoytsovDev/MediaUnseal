using System;
using System.IO;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MediaUnseal.Models;

namespace MediaUnseal.Services;

internal sealed record OcrExportSettings(string? Language, OcrModelSet Model = OcrModelSet.Fast,
    string? TesseractPath = null, string? DataRoot = null, char CsvDelimiter = ',',
    string CsvDestination = "universal", string AdrFps = "24",
    string AdrStartTimecode = "00:00:00:00");

internal sealed class OcrExportException(string key, string message) : IOException(message)
{
    internal string Key => key;
}

internal static class SubtitleOcrExport
{
    internal static TrackExportPlan CreatePlan(MediaInfo media, MediaStreamInfo stream,
        string format, OcrExportSettings settings)
    {
        var (muxer, extension) = format switch
        {
            "srt" => ("srt", "srt"), "webvtt" => ("webvtt", "vtt"),
            "ass" => ("ass", "ass"), "ssa" => ("ssa", "ssa"),
            "csv" => ("csv", "csv"),
            _ => throw new ArgumentException("The requested OCR text format is unsupported.", nameof(format))
        };
        var plan = new TrackExportPlan(stream, extension, muxer, false) { Ocr = settings };
        if (!IsValidPlan(media, plan))
            throw new ArgumentException("The graphical subtitle stream or OCR output settings are invalid.", nameof(stream));
        ResolveLanguage(settings.Language, stream.Language);
        return plan;
    }

    internal static bool IsValidPlan(MediaInfo media, TrackExportPlan plan)
    {
        if (plan.Ocr is not { } settings || !media.Streams.Contains(plan.Stream)
            || plan.Stream.Index is not >= 0 || plan.Stream.SubtitleKind != SubtitleKind.Graphic
            || plan.Stream.Codec is not ("hdmv_pgs_subtitle" or "dvd_subtitle")
            || plan.Conversion is not null || plan.Attachment || plan.StillImage
            || plan.VerifiedCopy || plan.AttachmentFile is not null || plan.ChapterData is not null
            || settings.Model is not (OcrModelSet.Fast or OcrModelSet.Best)
            || settings.CsvDelimiter is not (',' or ';' or '\t' or '|')
            || settings.CsvDestination is not ("universal" or "adr")
            || settings.CsvDestination == "adr" && !SubtitleCsvExporter.IsValidAdrTiming(
                settings.AdrFps, settings.AdrStartTimecode)) return false;
        return (plan.Muxer, plan.Extension) is ("srt", "srt") or ("webvtt", "vtt")
            or ("ass", "ass") or ("ssa", "ssa") or ("csv", "csv");
    }

    internal static string ResolveLanguage(string? selected, string? trackLanguage)
    {
        var language = selected ?? SuggestedLanguage(trackLanguage);
        if (string.IsNullOrWhiteSpace(language))
            throw string.IsNullOrWhiteSpace(trackLanguage)
                ? new OcrExportException("MainOcrLanguageRequired", "Choose an OCR language for this subtitle track.")
                : new OcrExportException("MainOcrLanguageUnsupported",
                    "The track language is unsupported for OCR. Choose a language explicitly.");
        if (!SubtitleOcr.Languages.Contains(language, StringComparer.Ordinal))
            throw new OcrExportException("MainOcrLanguageUnsupported",
                "This OCR language is unsupported. Choose one of the ten supported languages explicitly.");
        return language;
    }

    internal static string? SuggestedLanguage(string? trackLanguage)
    {
        if (trackLanguage is null) return null;
        var code = trackLanguage.Trim().ToLowerInvariant();
        return code switch
        {
            "en" => "eng", "ru" => "rus", "de" or "ger" => "deu",
            "fr" or "fre" => "fra", "es" => "spa", "it" => "ita",
            "pt" => "por", "ja" => "jpn", "ko" => "kor",
            "zh-hans" or "zh-cn" => "chi_sim",
            _ when SubtitleOcr.Languages.Contains(code, StringComparer.Ordinal) => code,
            _ => null
        };
    }

    internal static async Task<string?> UnavailableReasonAsync(MediaStreamInfo stream,
        OcrExportSettings settings, CancellationToken token)
    {
        string language;
        try { language = ResolveLanguage(settings.Language, stream.Language); }
        catch (OcrExportException ex) { return ex.Message; }
        var engine = await SubtitleOcr.DiscoverAsync(settings.TesseractPath, settings.DataRoot, token)
            .ConfigureAwait(false);
        if (engine is null) return "Tesseract 5.x is unavailable. Install or select Tesseract.";
        var models = settings.Model == OcrModelSet.Fast ? engine.FastLanguages : engine.BestLanguages;
        return models.Contains(language, StringComparer.Ordinal) ? null
            : $"The {settings.Model} OCR model for {language} is unavailable.";
    }

    internal static async Task<int> ExportAsync(MediaInfo media, MediaStreamInfo stream,
        OcrExportSettings settings, string format, string output, string ffmpeg, string ffprobe,
        DiagnosticLog diagnostics, IProgress<double>? progress, CancellationToken token)
    {
        var language = ResolveLanguage(settings.Language, stream.Language);
        var engine = await SubtitleOcr.DiscoverAsync(settings.TesseractPath, settings.DataRoot, token)
            .ConfigureAwait(false) ?? throw new OcrExportException("MainOcrUnavailable",
                "Tesseract 5.x is unavailable. Install or select Tesseract to export text from graphical subtitles.");
        var models = settings.Model == OcrModelSet.Fast ? engine.FastLanguages : engine.BestLanguages;
        if (!models.Contains(language, StringComparer.Ordinal))
            throw new OcrExportException("MainOcrModelMissing",
                $"The {settings.Model} OCR model for {language} is unavailable.");
        diagnostics.Record(DiagnosticLevel.Info, "OCR export",
            $"Stream #{stream.Index}: {stream.Codec} to {format}; language {language}; " +
            $"model {settings.Model}; engine {engine.Version}.");
        var cues = await SubtitleOcr.RecognizeAsync(media, stream, engine, settings.Model,
            language, ffmpeg, ffprobe, diagnostics, progress, token).ConfigureAwait(false);
        var exportable = SelectExportable(cues, diagnostics, stream.Index!.Value);
        var data = new SubtitleCsvImporter.Data(false, ',', exportable.Select(cue =>
            new SubtitleCsvImporter.Cue(SubtitleCsvExporter.FormatTime(cue.Start),
                SubtitleCsvExporter.FormatTime(cue.End), "", cue.Text)).ToArray());
        var written = await SubtitleCsvImporter.WriteAsync(data, output, format, null,
            "00:00:00:00", token, settings.CsvDelimiter, settings.CsvDestination,
            settings.AdrFps, settings.AdrStartTimecode).ConfigureAwait(false);
        diagnostics.Record(DiagnosticLevel.Info, "OCR export",
            $"Stream #{stream.Index}: source {cues.Count}, saved {written}, skipped {cues.Count - written}; " +
            $"format {format}; graphical styling is not retained.",
            userMessageKey: cues.Count == written ? null : "LogOcrPartialCompleted",
            userMessageArgs: [cues.Count, written, cues.Count - written]);
        return written;
    }

    internal static IReadOnlyList<OcrCue> SelectExportable(IReadOnlyList<OcrCue> cues,
        DiagnosticLog diagnostics, int streamIndex)
    {
        var technical = cues.FirstOrDefault(cue => cue.Error is not null);
        if (technical is not null)
            throw new OcrExportException("LogOcrFailed",
                $"Tesseract failed for cue {technical.SourceFrame + 1} at {technical.Start:c}: " +
                technical.Error + " " + technical.Diagnostic);
        var accepted = cues.Where(cue => !string.IsNullOrWhiteSpace(cue.Text)).ToArray();
        if (accepted.Length == 0)
        {
            diagnostics.Record(DiagnosticLevel.Warn, "OCR export",
                $"Stream #{streamIndex}: no text recognized in {cues.Count} graphical cues; no output published.");
            throw new OcrExportException("LogOcrFailed", "No graphical subtitle text was recognized.");
        }
        var skipped = cues.Where(cue => string.IsNullOrWhiteSpace(cue.Text)).ToArray();
        if (skipped.Length > 0)
            diagnostics.Record(DiagnosticLevel.Warn, "OCR export",
                $"Stream #{streamIndex}: {skipped.Length} of {cues.Count} cues contain no recognizable text. " +
                string.Join("; ", skipped.Select(cue =>
                    $"cue {cue.SourceFrame + 1} at {cue.Start:c}-{cue.End:c}: " +
                    (cue.Diagnostic ?? "Tesseract returned no text from a decoded image"))));
        return accepted;
    }
}
