using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using MediaUnseal.Models;
using SkiaSharp;

namespace MediaUnseal.Services;

internal enum OcrModelSet { Fast, Best }
internal sealed record OcrEngine(string Executable, string Version, string FastData, string BestData,
    IReadOnlyList<string> FastLanguages, IReadOnlyList<string> BestLanguages);
internal sealed record OcrCue(TimeSpan Start, TimeSpan End, string Language, string Text,
    string? Error, int SourceFrame, string? Diagnostic);
internal sealed class OcrTimingException(int cue, TimeSpan start, string eventType, string detail)
    : IOException($"Graphical cue {cue} at {start:c} ({eventType}) has invalid timing: {detail}")
{
    internal int Cue => cue;
    internal TimeSpan Start => start;
    internal string EventType => eventType;
}

// OCR is optional. The caller owns no image files and receives one result per rendered cue.
// The managed directories are provisioned externally; this service never downloads models.
internal static class SubtitleOcr
{
    internal static readonly string[] Languages =
        ["rus", "eng", "deu", "fra", "spa", "ita", "por", "chi_sim", "jpn", "kor"];
    private const int MaxOutput = 1024 * 1024;
    private const int MaxCues = 100_000;

    internal static string ManagedRoot => Path.Combine(Environment.GetFolderPath(
        Environment.SpecialFolder.LocalApplicationData), "MediaUnseal", "ocr");

    internal static async Task<OcrEngine?> DiscoverAsync(string? executable, string? dataRoot,
        CancellationToken token)
    {
        var root = dataRoot ?? ManagedRoot;
        var installedRoot = Path.Combine(root, "current");
        var managed = Path.Combine(Directory.Exists(installedRoot) ? installedRoot : root,
            "bin", OperatingSystem.IsWindows() ? "tesseract.exe" : "tesseract");
        var candidates = new[] { executable, OcrPathSelection.Read(), managed,
            OperatingSystem.IsWindows() ? Path.Combine(Environment.GetFolderPath(
                Environment.SpecialFolder.ProgramFiles), "Tesseract-OCR", "tesseract.exe") : null,
            "tesseract" }.Where(p => !string.IsNullOrWhiteSpace(p)).Distinct(StringComparer.OrdinalIgnoreCase);
        foreach (var candidate in candidates)
        {
            token.ThrowIfCancellationRequested();
            try
            {
                var result = await RunAsync(candidate!, ["--version"], token).ConfigureAwait(false);
                var first = (result.Output + result.Error).Split('\n')[0].Trim();
                if (result.ExitCode != 0 || !Regex.IsMatch(first, @"\Atesseract\s+v?5\.",
                    RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
                    continue;
                var modelRoot = candidate == managed && Directory.Exists(installedRoot) ? installedRoot : root;
                var fast = Path.Combine(modelRoot, "tessdata_fast");
                var best = Path.Combine(modelRoot, "tessdata_best");
                return new(candidate!, first, fast, best,
                    await ListLanguagesAsync(candidate!, fast, token).ConfigureAwait(false),
                    await ListLanguagesAsync(candidate!, best, token).ConfigureAwait(false));
            }
            catch (Exception ex) when (ex is Win32Exception or IOException or OperationCanceledException
                && !token.IsCancellationRequested) { }
        }
        return null;
    }

    private static string[] Available(string directory) => Languages.Where(language =>
        File.Exists(Path.Combine(directory, language + ".traineddata"))).ToArray();

    private static async Task<string[]> ListLanguagesAsync(string executable, string directory,
        CancellationToken token)
    {
        if (!Directory.Exists(directory)) return [];
        var result = await RunAsync(executable, ["--list-langs", "--tessdata-dir", directory], token)
            .ConfigureAwait(false);
        if (result.ExitCode != 0) return [];
        var listed = (result.Output + result.Error).Split('\n', StringSplitOptions.TrimEntries);
        return Available(directory).Where(language => listed.Contains(language, StringComparer.Ordinal)).ToArray();
    }

    internal static async Task<IReadOnlyList<OcrCue>> RecognizeAsync(MediaInfo media,
        MediaStreamInfo stream, OcrEngine engine, OcrModelSet model, string language,
        string ffmpeg, string ffprobe, DiagnosticLog diagnostics, IProgress<double>? progress,
        CancellationToken token)
    {
        if (stream.Codec is not ("hdmv_pgs_subtitle" or "dvd_subtitle") || stream.Index is not >= 0)
            throw new ArgumentException("OCR requires a graphical subtitle stream.", nameof(stream));
        if (!Languages.Contains(language, StringComparer.Ordinal))
            throw new ArgumentException("Unsupported OCR language.", nameof(language));
        if (Path.GetExtension(media.FullPath).Equals(".vob", StringComparison.OrdinalIgnoreCase)
            && media.DvdIfo is not { PrivateData: not null })
            throw new InvalidDataException("DVD VobSub OCR requires unambiguous palette metadata from the matching IFO.");
        var data = model == OcrModelSet.Fast ? engine.FastData : engine.BestData;
        if (!(model == OcrModelSet.Fast ? engine.FastLanguages : engine.BestLanguages)
            .Contains(language, StringComparer.Ordinal))
            throw new FileNotFoundException($"The {model} OCR model for {language} is unavailable.");
        var root = Path.GetTempPath();
        using var stage = TemporaryDirectory.Create(root, "mediaunseal-ocr-");
        try
        {
            var index = stream.Index.Value.ToString(CultureInfo.InvariantCulture);
            var frames = await ReadFramesAsync(ffprobe, media.FullPath, index, token).ConfigureAwait(false);
            if (frames.Count == 0) throw new InvalidDataException("No graphical subtitle frames were decoded.");
            var timing = ResolveTimeline(frames);
            if (timing.Fallbacks.Count > 0)
                diagnostics.Record(DiagnosticLevel.Warn, "OCR timing",
                    $"Calculated end times for {timing.Fallbacks.Count} of {frames.Count(f => f.Visible)} visible cues. " +
                    "First cases: " + string.Join("; ", timing.Fallbacks.Take(30)),
                    userMessageKey: "LogOcrTimingFallback");
            var intervals = timing.Ends;
            var template = Path.Combine(stage.Path, "cue-%06d.png");
            var renderArgs = new List<string> { "-hide_banner", "-loglevel", "info", "-nostdin", "-copyts" };
            if (Path.GetExtension(media.FullPath).Equals(".vob", StringComparison.OrdinalIgnoreCase)
                && media.DvdIfo?.PrivateData is { } ifoData)
            {
                var palette = ifoData.Split('\n').First(line => line.StartsWith("palette: ", StringComparison.Ordinal));
                renderArgs.AddRange(["-palette", palette[9..].Replace(" ", "", StringComparison.Ordinal)]);
            }
            renderArgs.AddRange(["-i", media.FullPath, "-filter_complex", $"[0:{index}]format=rgba,showinfo[ocr]",
                "-map", "[ocr]", "-fps_mode:v", "passthrough", "-start_number", "0", "-y", template]);
            var rendered = await RunAsync(ffmpeg, renderArgs.ToArray(), token, 16 * 1024 * 1024)
                .ConfigureAwait(false);
            if (rendered.ExitCode != 0 || !Directory.EnumerateFiles(stage.Path, "cue-*.png").Any())
                throw new InvalidDataException("FFmpeg could not render graphical subtitle cues: " + rendered.Error);
            var renderedFrames = new Dictionary<long, List<string>>();
            foreach (Match match in Regex.Matches(rendered.Error,
                @"n:\s*(\d+)\s+pts:\s*\S+\s+pts_time:\s*([\d.]+)", RegexOptions.CultureInvariant))
            {
                if (!int.TryParse(match.Groups[1].Value, out var number)
                    || !decimal.TryParse(match.Groups[2].Value, NumberStyles.Float,
                        CultureInfo.InvariantCulture, out var seconds)) continue;
                var ms = (long)decimal.Round(seconds * 1000, 0);
                var image = Path.Combine(stage.Path, $"cue-{number:000000}.png");
                if (!File.Exists(image)) continue;
                if (!renderedFrames.TryGetValue(ms, out var images))
                    renderedFrames[ms] = images = [];
                images.Add(image);
            }
            var results = new List<OcrCue>();
            for (var i = 0; i < frames.Count; i++)
            {
                token.ThrowIfCancellationRequested();
                var current = frames[i];
                if (!current.Visible) continue;
                var end = intervals[i]!.Value;
                if (!renderedFrames.TryGetValue((long)current.Start.TotalMilliseconds, out var images))
                    throw new InvalidDataException($"Graphical cue {i + 1} has no rendered image.");
                var image = images.FirstOrDefault(CropVisibleImage);
                if (image is null)
                    throw new InvalidDataException($"Graphical cue {i + 1} rendered only empty frames.");
                var cue = await RecognizeImageAsync(image, current.Start, end, language,
                    i, engine, data, token).ConfigureAwait(false);
                if (cue.Diagnostic is not null)
                    diagnostics.Record(DiagnosticLevel.Warn, "OCR recognition",
                        $"Cue {i + 1} failed.", cue.Diagnostic);
                results.Add(cue);
                progress?.Report((double)(i + 1) / frames.Count);
            }
            diagnostics.Record(DiagnosticLevel.Info, "OCR", $"Processed {results.Count} graphical cues; " +
                $"model {model}; language {language}; errors {results.Count(c => c.Error is not null)}.");
            return results;
        }
        finally { stage.Dispose(); await TemporaryDirectory.DeleteAsync(stage.Path,
            message => diagnostics.Record(DiagnosticLevel.Warn, "OCR cleanup", message)).ConfigureAwait(false); }
    }

    internal static async Task<OcrCue> RecognizeImageAsync(string image, TimeSpan start, TimeSpan end,
        string language, int frame, OcrEngine engine, string dataDirectory, CancellationToken token)
    {
        if (end <= start || !Languages.Contains(language, StringComparer.Ordinal) || !File.Exists(image))
            throw new ArgumentException("The OCR image or cue timing is invalid.");
        var ocr = await RunAsync(engine.Executable,
            [image, "stdout", "--tessdata-dir", dataDirectory, "-l", language, "--psm", "6"], token)
            .ConfigureAwait(false);
        return new(start, end, language,
            ocr.ExitCode == 0 ? ocr.Output.TrimEnd('\r', '\n') : "",
            ocr.ExitCode == 0 ? null : "Tesseract failed to recognize this cue.", frame,
            ocr.ExitCode == 0 ? null : DiagnosticLog.RedactSensitive(ocr.Error));
    }

    internal sealed record Frame(TimeSpan Start, TimeSpan? End, bool Visible, string EventType);
    internal sealed record TimingResult(TimeSpan?[] Ends, IReadOnlyList<string> Fallbacks);
    internal static TimeSpan?[] ValidateTimeline(IReadOnlyList<Frame> frames) => ResolveTimeline(frames).Ends;
    internal static TimingResult ResolveTimeline(IReadOnlyList<Frame> frames)
    {
        var ends = new TimeSpan?[frames.Count];
        var fallbacks = new List<string>();
        for (var i = 0; i < frames.Count; i++)
        {
            var current = frames[i];
            if (i > 0 && current.Start < frames[i - 1].Start)
                throw new OcrTimingException(i + 1, current.Start, current.EventType,
                    $"timeline moved backward from {frames[i - 1].Start:c}; DVD navigation may be required");
            if (!current.Visible) continue;
            var next = i + 1 < frames.Count ? frames[i + 1] : null;
            if (next is { Visible: false } && next.Start == current.Start)
                throw new OcrTimingException(i + 1, current.Start, current.EventType,
                    "clear composition has the same timestamp as the presentation");
            var end = current.End;
            if (end is null && next is { Visible: false } && next.Start > current.Start)
                end = next.Start;
            if (end is null)
            {
                Frame? nextLater = null;
                for (var j = i + 1; j < frames.Count; j++)
                    if (frames[j].Start > current.Start) { nextLater = frames[j]; break; }
                end = current.Start + TimeSpan.FromSeconds(3);
                if (nextLater is not null && nextLater.Start < end) end = nextLater.Start;
                fallbacks.Add($"cue {i + 1} at {current.Start:c}: {current.EventType}, " +
                    (nextLater is null ? "end of stream" : $"next {nextLater.EventType} at {nextLater.Start:c}") +
                    $", calculated end {end.Value:c}");
            }
            if (end <= current.Start || next is not null && next.Start > current.Start && end > next.Start)
                throw new OcrTimingException(i + 1, current.Start, current.EventType,
                    next is null ? "end of stream without a clear composition" :
                    $"next event: {next.EventType} at {next.Start:c}; duration/end_display_time: " +
                    (current.End?.ToString("c") ?? "unknown"));
            ends[i] = end;
        }
        return new(ends, fallbacks);
    }
    internal static async Task<TimingResult> ValidateSourceTimingAsync(string ffprobe, string path, string index,
        CancellationToken token) => ResolveTimeline(await ReadFramesAsync(ffprobe, path, index, token)
            .ConfigureAwait(false));
    private static async Task<List<Frame>> ReadFramesAsync(string ffprobe, string path, string index,
        CancellationToken token)
    {
        var result = await RunAsync(ffprobe, ["-v", "error", "-select_streams", index,
            "-show_frames", "-show_entries", "frame=pts_time,duration_time,start_display_time,end_display_time,num_rects", "-of", "json", path],
            token, 32 * 1024 * 1024).ConfigureAwait(false);
        if (result.ExitCode != 0) throw new InvalidDataException("FFprobe could not inspect graphical cues: " + result.Output);
        using var json = JsonDocument.Parse(result.Output);
        var frames = new List<Frame>();
        foreach (var item in json.RootElement.GetProperty("frames").EnumerateArray())
        {
            if (frames.Count >= MaxCues) throw new InvalidDataException("OCR cue limit exceeded.");
            if (!item.TryGetProperty("pts_time", out var pts) || !decimal.TryParse(pts.GetString(),
                NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds) || seconds < 0) continue;
            var start = TimeSpan.FromMilliseconds((double)decimal.Round(seconds * 1000, 0));
            TimeSpan? end = null;
            if (item.TryGetProperty("duration_time", out var duration)
                && decimal.TryParse(duration.GetString(), NumberStyles.Float,
                    CultureInfo.InvariantCulture, out var length) && length > 0)
                end = start + TimeSpan.FromSeconds((double)length);
            if (end is null && item.TryGetProperty("end_display_time", out var displayEnd)
                && displayEnd.ValueKind == JsonValueKind.Number
                && displayEnd.TryGetUInt32(out var endMilliseconds)
                && endMilliseconds is > 0 and < uint.MaxValue)
            {
                var startMilliseconds = item.TryGetProperty("start_display_time", out var displayStart)
                    && displayStart.ValueKind == JsonValueKind.Number
                    && displayStart.TryGetUInt32(out var offset) ? offset : 0;
                if (endMilliseconds > startMilliseconds)
                    end = start + TimeSpan.FromMilliseconds(endMilliseconds);
            }
            var visible = item.TryGetProperty("num_rects", out var rectangles)
                && rectangles.ValueKind == JsonValueKind.Number && rectangles.GetInt32() > 0;
            frames.Add(new(start, end, visible, visible ? "presentation" : "clear composition"));
        }
        return frames;
    }

    internal static bool CropVisibleImage(string path)
    {
        using var bitmap = SKBitmap.Decode(path) ?? throw new InvalidDataException("FFmpeg produced an invalid OCR image.");
        var left = bitmap.Width; var top = bitmap.Height; var right = -1; var bottom = -1;
        for (var y = 0; y < bitmap.Height; y++)
            for (var x = 0; x < bitmap.Width; x++)
                if (bitmap.GetPixel(x, y).Alpha > 0)
                { left = Math.Min(left, x); top = Math.Min(top, y); right = Math.Max(right, x); bottom = Math.Max(bottom, y); }
        if (right < left) return false;
        const int margin = 12;
        using var cropped = new SKBitmap(right - left + 1 + margin * 2,
            bottom - top + 1 + margin * 2);
        using (var canvas = new SKCanvas(cropped))
        {
            canvas.Clear(SKColors.Black);
            canvas.DrawBitmap(bitmap, new SKRect(left, top, right + 1, bottom + 1),
                new SKRect(margin, margin, cropped.Width - margin, cropped.Height - margin));
        }
        using var image = SKImage.FromBitmap(cropped);
        using var encoded = image.Encode(SKEncodedImageFormat.Png, 100);
        using var output = File.Create(path);
        encoded.SaveTo(output);
        return true;
    }

    private static async Task<(int ExitCode, string Output, string Error)> RunAsync(string executable, string[] args,
        CancellationToken token, int limit = MaxOutput)
    {
        using var process = new Process { StartInfo = new ProcessStartInfo(executable)
        { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true,
          RedirectStandardError = true, StandardOutputEncoding = Encoding.UTF8,
          StandardErrorEncoding = Encoding.UTF8 } };
        foreach (var arg in args) process.StartInfo.ArgumentList.Add(arg);
        process.Start();
        try
        {
            var stdout = ReadAsync(process.StandardOutput, limit, token);
            var stderr = ReadAsync(process.StandardError, limit, token);
            await Task.WhenAll(stdout, stderr, process.WaitForExitAsync(token)).ConfigureAwait(false);
            return (process.ExitCode, await stdout.ConfigureAwait(false), await stderr.ConfigureAwait(false));
        }
        finally
        {
            if (!process.HasExited)
            { process.Kill(entireProcessTree: true); await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false); }
        }
    }

    private static async Task<string> ReadAsync(StreamReader reader, int limit, CancellationToken token)
    {
        var result = new StringBuilder();
        var buffer = new char[4096];
        while (true)
        {
            var count = await reader.ReadAsync(buffer.AsMemory(), token).ConfigureAwait(false);
            if (count == 0) return result.ToString();
            if (result.Length + count > limit) throw new InvalidDataException("OCR process output exceeds the safety limit.");
            result.Append(buffer, 0, count);
        }
    }
}
