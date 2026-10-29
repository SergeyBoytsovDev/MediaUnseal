using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using TrackExtractor.Models;

namespace TrackExtractor.Services;

public sealed class MediaAnalyzer(DiagnosticLog diagnostics)
{
    private const int OutputLimit = 32 * 1024 * 1024;
    private const int ErrorLimit = 8192;
    internal Func<string, string[], CancellationToken, Task<ProbeResult>>? ProcessRunner { get; init; }
    public TimeSpan Timeout { get; set; } = TimeSpan.FromMinutes(5);

    public async Task<MediaInfo> AnalyzeAsync(string path, string ffprobePath, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(path) || Uri.IsWellFormedUriString(path, UriKind.Absolute))
            throw new ArgumentException("Выберите локальный файл.", nameof(path));
        var fullPath = Path.GetFullPath(path);
        if (Timeout <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(Timeout));
        var watch = Stopwatch.StartNew();
        var root = Path.GetPathRoot(fullPath);
        var fileDetails = $"Volume: {(root?.StartsWith("\\\\", StringComparison.Ordinal) == true ? "[network share]" : root)}; " +
            $"extension: {Path.GetExtension(fullPath)}";
        diagnostics.Record(DiagnosticLevel.Info, "Analysis", "File analysis started.", fileDetails,
            userMessageKey: "LogAnalysisStarted");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(Timeout);
        try
        {
            if (!File.Exists(fullPath)) throw new FileNotFoundException("Файл не найден. Выберите другой файл.", fullPath);
            var size = new FileInfo(fullPath).Length;
            var arguments = new[] { "-v", "error", "-show_format", "-show_streams", "-of", "json", "-i", fullPath };
            var result = ProcessRunner is null
                ? await RunProbeAsync(ffprobePath, arguments, deadline.Token).ConfigureAwait(false)
                : await ProcessRunner(ffprobePath, arguments, deadline.Token).WaitAsync(deadline.Token).ConfigureAwait(false);
            if (result.ExitCode != 0)
                throw new InvalidDataException($"FFprobe завершился с кодом {result.ExitCode}. Файл может быть повреждён или не поддерживается.");
            var media = ParseJson(result.Stdout, fullPath, size);
            diagnostics.Record(DiagnosticLevel.Info, "Analysis",
                $"Analysis completed in {watch.Elapsed.TotalSeconds:F1} s; streams: {media.Streams.Count}.", fileDetails,
                userMessageKey: "LogAnalysisCompleteCount", userMessageArgs: [media.Streams.Count], userSuccess: true);
            return media;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && deadline.IsCancellationRequested)
        {
            diagnostics.Record(DiagnosticLevel.Warn, "Analysis", $"FFprobe timed out after {watch.Elapsed.TotalSeconds:F1} s.", fileDetails,
                userMessageKey: "LogAnalysisTimeout");
            throw new TimeoutException("FFprobe не завершил анализ за отведённое время.");
        }
        catch (OperationCanceledException)
        {
            diagnostics.Record(DiagnosticLevel.Warn, "Analysis", $"Analysis cancelled after {watch.Elapsed.TotalSeconds:F1} s.", fileDetails,
                userMessageKey: "LogAnalysisCancelled");
            throw;
        }
        catch (Exception ex)
        {
            diagnostics.Record(DiagnosticLevel.Error, "Analysis",
                $"Analysis failed after {watch.Elapsed.TotalSeconds:F1} s: {ex.GetType().Name}" +
                (ex is InvalidDataException or TimeoutException ? $" ({ex.Message})" : ""), fileDetails,
                userMessageKey: "LogAnalysisFailed");
            throw;
        }
    }

    internal static MediaInfo ParseJson(string json, string path, long size)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) throw new InvalidDataException("FFprobe вернул неверный JSON.");
            var format = Property(root, "format");
            var streamsJson = Property(root, "streams");
            if (format.ValueKind != JsonValueKind.Object && streamsJson.ValueKind != JsonValueKind.Array)
                throw new InvalidDataException("FFprobe не вернул сведения о файле и потоках.");
            var formatName = ReadString(Property(format, "format_name"));
            var formatDuration = ReadDuration(Property(format, "duration"));
            var streams = new List<MediaStreamInfo>();
            if (streamsJson.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in streamsJson.EnumerateArray())
                {
                    if (item.ValueKind != JsonValueKind.Object)
                        throw new InvalidDataException("FFprobe вернул неверное описание потока.");
                    var tags = ReadTags(Property(item, "tags"));
                    var disposition = ReadDisposition(Property(item, "disposition"));
                    var type = ReadString(Property(item, "codec_type"));
                    var codec = ReadString(Property(item, "codec_name"));
                    var streamDuration = ReadDuration(Property(item, "duration"));
                    var frameCount = ReadLong(Property(item, "nb_frames")) ?? ReadLong(Property(item, "nb_read_frames"));
                    if (frameCount <= 0) frameCount = null;
                    var visual = ClassifyVisual(type, codec, formatName, tags, disposition, frameCount, streamDuration);
                    streams.Add(new MediaStreamInfo(
                        ReadInt(Property(item, "index")), type, codec,
                        ReadString(Property(item, "profile")),
                        ReadInt(Property(item, "width")), ReadInt(Property(item, "height")),
                        ReadRate(Property(item, "avg_frame_rate")) ?? ReadRate(Property(item, "r_frame_rate")),
                        ReadLong(Property(item, "bit_rate")), ReadString(Property(item, "pix_fmt")),
                        streamDuration, ReadInt(Property(item, "sample_rate")),
                        ReadInt(Property(item, "channels")), ReadString(Property(item, "channel_layout")),
                        tags.GetValueOrDefault("language"), tags.GetValueOrDefault("title"),
                        disposition.GetValueOrDefault("default"), disposition.GetValueOrDefault("forced"),
                        SubtitleType(type, codec), tags, disposition,
                        frameCount, visual.Kind, visual.ImageFormat, visual.IsCoverArt,
                        ReadExtraProperties(item)));
                }
            }
            return new MediaInfo(Path.GetFileName(path), path, size,
                formatName, formatDuration,
                ReadLong(Property(format, "bit_rate")), ReadTags(Property(format, "tags")), streams);
        }
        catch (JsonException ex) { throw new InvalidDataException("FFprobe вернул повреждённый JSON.", ex); }
    }

    private static JsonElement Property(JsonElement item, string name) =>
        item.ValueKind == JsonValueKind.Object && item.TryGetProperty(name, out var value) ? value : default;

    private static Dictionary<string, string> ReadExtraProperties(JsonElement item)
    {
        var extra = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var key in new[] { "codec_long_name", "codec_tag_string", "codec_tag", "level",
            "sample_fmt", "bits_per_sample", "bits_per_raw_sample", "display_aspect_ratio",
            "sample_aspect_ratio", "field_order", "color_range", "color_space", "color_transfer",
            "color_primaries", "chroma_location", "start_time" })
            if (ReadString(Property(item, key)) is { } text) extra[key] = text;
        var sideData = Property(item, "side_data_list");
        if (sideData.ValueKind == JsonValueKind.Array)
        {
            foreach (var side in sideData.EnumerateArray().Take(16))
            {
                if (side.ValueKind != JsonValueKind.Object) continue;
                var type = ReadString(Property(side, "side_data_type")) ?? "side_data";
                foreach (var field in side.EnumerateObject().Take(24))
                {
                    if (field.Name == "side_data_type") continue;
                    if (ReadString(field.Value) is { } value)
                        extra[$"{type}: {field.Name}"] = value;
                }
            }
        }
        return extra;
    }

    private static string? ReadString(JsonElement value)
    {
        var text = value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Number => value.GetRawText(),
            _ => null
        };
        return string.IsNullOrWhiteSpace(text) || text == "N/A" ? null : text;
    }

    private static int? ReadInt(JsonElement value) =>
        int.TryParse(ReadString(value), NumberStyles.Integer, CultureInfo.InvariantCulture, out var number) ? number : null;

    private static long? ReadLong(JsonElement value) =>
        long.TryParse(ReadString(value), NumberStyles.Integer, CultureInfo.InvariantCulture, out var number) ? number : null;

    private static double? ReadDouble(JsonElement value) =>
        double.TryParse(ReadString(value), NumberStyles.Float, CultureInfo.InvariantCulture, out var number)
        && double.IsFinite(number) && number >= 0 ? number : null;

    private static TimeSpan? ReadDuration(JsonElement value)
    {
        var seconds = ReadDouble(value);
        if (seconds is null || seconds > TimeSpan.MaxValue.TotalSeconds) return null;
        return TimeSpan.FromSeconds(seconds.Value);
    }

    private static double? ReadRate(JsonElement value)
    {
        var text = ReadString(value);
        if (text is null) return null;
        var parts = text.Split('/');
        if (parts.Length == 1) return ReadDouble(value);
        if (parts.Length != 2 || !double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var numerator)
            || !double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var denominator)
            || denominator <= 0 || numerator < 0) return null;
        var rate = numerator / denominator;
        return double.IsFinite(rate) ? rate : null;
    }

    private static Dictionary<string, string> ReadTags(JsonElement value)
    {
        var tags = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (value.ValueKind == JsonValueKind.Object)
            foreach (var property in value.EnumerateObject())
                if (ReadString(property.Value) is { } text) tags[property.Name] = text;
        return tags;
    }

    private static Dictionary<string, int?> ReadDisposition(JsonElement value)
    {
        var flags = new Dictionary<string, int?>(StringComparer.OrdinalIgnoreCase);
        if (value.ValueKind == JsonValueKind.Object)
            foreach (var property in value.EnumerateObject())
                flags[property.Name] = property.Value.ValueKind switch
                {
                    JsonValueKind.True => 1,
                    JsonValueKind.False => 0,
                    _ => ReadInt(property.Value)
                };
        return flags;
    }

    private static SubtitleKind SubtitleType(string? type, string? codec)
    {
        if (type != "subtitle" || codec is null) return SubtitleKind.Unknown;
        return codec.ToLowerInvariant() switch
        {
            "subrip" or "ass" or "ssa" or "mov_text" or "webvtt" or "text" or "ttml" or "srt" => SubtitleKind.Text,
            "dvd_subtitle" or "dvb_subtitle" or "hdmv_pgs_subtitle" or "xsub" => SubtitleKind.Graphic,
            _ => SubtitleKind.Unknown
        };
    }

    private static (VisualKind Kind, string? ImageFormat, bool IsCoverArt) ClassifyVisual(
        string? type, string? codec, string? formatName, IReadOnlyDictionary<string, string> tags,
        IReadOnlyDictionary<string, int?> disposition, long? frames, TimeSpan? duration)
    {
        var imageFormat = ImageFormat(codec, tags) ?? ImageFormatFromContainer(formatName);
        if (type is not ("video" or "attachment")) return (VisualKind.Unknown, null, false);
        var attached = disposition.GetValueOrDefault("attached_pic") == 1;
        var still = disposition.GetValueOrDefault("still_image") == 1;
        var imageMime = tags.GetValueOrDefault("mimetype")?.StartsWith("image/", StringComparison.OrdinalIgnoreCase) == true;
        var filename = tags.GetValueOrDefault("filename");
        var imageFilename = filename is not null && IsImageFilename(filename);
        var coverMarker = HasCoverMarker(filename) || HasCoverMarker(tags.GetValueOrDefault("title"));
        if (type == "attachment")
            return imageMime || imageFormat is not null
                ? (VisualKind.Image, imageFormat, coverMarker)
                : (VisualKind.Unknown, imageFormat, false);
        if (attached) return (VisualKind.Image, imageFormat, true);
        if (still) return (VisualKind.Image, imageFormat, coverMarker);

        var formats = formatName?.Split(',', StringSplitOptions.TrimEntries) ?? [];
        var animationContainer = formats.Any(f => f is "gif" or "apng" or "gif_pipe" or "apng_pipe" or "webp_pipe");
        if (animationContainer && imageFormat is not null)
            return frames switch
            {
                > 1 => (VisualKind.Animation, imageFormat, false),
                1 => (VisualKind.Image, imageFormat, false),
                _ => (VisualKind.Unknown, imageFormat, false)
            };

        if (imageFormat is not null)
        {
            var stillContainer = formats.Any(f => f.EndsWith("_pipe", StringComparison.Ordinal)
                && f is not ("image2pipe" or "gif_pipe" or "apng_pipe"));
            if (stillContainer && frames is null or 1)
                return (VisualKind.Image, imageFormat, false);
            if (formats.Contains("image2") && frames == 1)
                return (VisualKind.Image, imageFormat, false);
            if ((imageMime || imageFilename || coverMarker) && frames is null or 1 && duration is null)
                return (VisualKind.Image, imageFormat, coverMarker);
            if (frames > 1)
                return (codec is "gif" or "apng" ? VisualKind.Animation : VisualKind.Video, imageFormat, false);
            if (duration > TimeSpan.Zero && frames != 1)
                return (VisualKind.Video, imageFormat, false);
            return (VisualKind.Unknown, imageFormat, false);
        }
        return (VisualKind.Video, null, false);
    }

    private static string? ImageFormat(string? codec, IReadOnlyDictionary<string, string> tags)
    {
        var name = codec?.ToLowerInvariant() switch
        {
            "mjpeg" or "jpeg" => "JPEG",
            "png" => "PNG", "webp" => "WebP", "bmp" => "BMP",
            "gif" => "GIF", "apng" => "APNG", "tiff" => "TIFF",
            "jpeg2000" => "JPEG 2000", "jpegxl" => "JPEG XL",
            "qoi" => "QOI", "svg" => "SVG", "ppm" => "PPM", "pgm" => "PGM",
            "pbm" => "PBM", "pam" => "PAM", "exr" => "OpenEXR", "avif" => "AVIF",
            _ => null
        };
        if (name is not null) return name;
        return tags.GetValueOrDefault("mimetype")?.ToLowerInvariant() switch
        {
            "image/jpeg" => "JPEG", "image/png" => "PNG", "image/webp" => "WebP",
            "image/bmp" => "BMP", "image/gif" => "GIF", "image/apng" => "APNG",
            "image/tiff" => "TIFF", "image/avif" => "AVIF", _ => null
        };
    }

    private static string? ImageFormatFromContainer(string? formatName)
    {
        foreach (var token in formatName?.Split(',', StringSplitOptions.TrimEntries) ?? [])
        {
            var format = token switch
            {
                "png_pipe" => "PNG", "jpeg_pipe" => "JPEG", "webp_pipe" => "WebP",
                "bmp_pipe" => "BMP", "tiff_pipe" => "TIFF", "gif_pipe" or "gif" => "GIF",
                "apng_pipe" or "apng" => "APNG", "ppm_pipe" => "PPM", "pgm_pipe" => "PGM",
                "pbm_pipe" => "PBM", "pam_pipe" => "PAM", "qoi_pipe" => "QOI",
                "jxl_pipe" => "JPEG XL", "exr_pipe" => "OpenEXR", "avif" => "AVIF",
                _ => null
            };
            if (format is not null) return format;
        }
        return null;
    }

    private static bool IsImageFilename(string filename) =>
        Path.GetExtension(filename).ToLowerInvariant() is ".jpg" or ".jpeg" or ".png" or ".webp"
            or ".bmp" or ".gif" or ".apng" or ".tif" or ".tiff";

    private static bool HasCoverMarker(string? value) => value is not null &&
        (value.Contains("cover", StringComparison.OrdinalIgnoreCase)
         || value.Contains("front", StringComparison.OrdinalIgnoreCase)
         || value.Contains("folder", StringComparison.OrdinalIgnoreCase)
         || value.Contains("облож", StringComparison.OrdinalIgnoreCase));

    private static async Task<ProbeResult> RunProbeAsync(string executable, string[] arguments, CancellationToken cancellationToken)
    {
        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8
        };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = new Process { StartInfo = start };
        var started = false;
        using var readers = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        try
        {
            if (!process.Start()) throw new Win32Exception("FFprobe не запустился.");
            started = true;
            var stdout = ReadAndCancelOnFailure(process.StandardOutput, OutputLimit);
            var stderr = ReadAndCancelOnFailure(process.StandardError, ErrorLimit);
            await Task.WhenAll(stdout, stderr, process.WaitForExitAsync(readers.Token)).ConfigureAwait(false);
            return new ProbeResult(process.ExitCode, await stdout, await stderr);
        }

        finally
        {
            if (started && !process.HasExited)
            {
                try { process.Kill(entireProcessTree: true); }
                catch (InvalidOperationException) { }
                try { await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false); }
                catch (InvalidOperationException) { }
            }
        }

        async Task<string> ReadAndCancelOnFailure(StreamReader reader, int limit)
        {
            try { return await ReadBoundedAsync(reader, limit, readers.Token).ConfigureAwait(false); }
            catch { await readers.CancelAsync().ConfigureAwait(false); throw; }
        }
    }

    private static async Task<string> ReadBoundedAsync(StreamReader reader, int limit, CancellationToken cancellationToken)
    {
        var text = new StringBuilder();
        var buffer = new char[8192];
        int count;
        while ((count = await reader.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) != 0)
        {
            if (text.Length + count > limit) throw new InvalidDataException("FFprobe вернул слишком большой ответ.");
            text.Append(buffer, 0, count);
        }
        return text.ToString();
    }

    internal sealed record ProbeResult(int ExitCode, string Stdout, string Stderr);
}
