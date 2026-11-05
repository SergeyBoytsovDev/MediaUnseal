using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using MediaUnseal.Models;

namespace MediaUnseal.Services;

public sealed record ConversionFormat(string Muxer, string Extension, string Description,
    string? VideoCodec, string? AudioCodec, string? SubtitleCodec, bool Original = false)
{
    public string Display => Original
        ? $"{(Extension.Equals("jpg", StringComparison.OrdinalIgnoreCase) ? "JPEG" :
            Extension.Equals("webp", StringComparison.OrdinalIgnoreCase) ? "WebP" :
            Extension.ToUpperInvariant())} — {Description}"
        : $"{Extension.ToUpperInvariant()} — {Description} ({Muxer})";
    public string? DefaultCodec(string kind) => kind switch
    {
        "video" or "image" or "animation" => VideoCodec,
        "audio" => AudioCodec, "subtitle" => SubtitleCodec, _ => null
    };
}

public sealed record ConversionEncoder(string Name, string Codec, string Kind, string Description)
{
    public string Display => Name == "copy" ? Description : $"{Name} — {Description}";
    public bool IsHardware => Name.EndsWith("_nvenc", StringComparison.Ordinal)
        || Name.EndsWith("_qsv", StringComparison.Ordinal)
        || Name.EndsWith("_amf", StringComparison.Ordinal)
        || Name.EndsWith("_vaapi", StringComparison.Ordinal)
        || Name.EndsWith("_videotoolbox", StringComparison.Ordinal)
        || Name.EndsWith("_vulkan", StringComparison.Ordinal)
        || Name.EndsWith("_d3d12va", StringComparison.Ordinal);
}

public sealed record ConversionSettings(ConversionFormat Format, ConversionEncoder Encoder,
    int? Crf = null, int? BitRateKbps = null, int? Width = null, int? Height = null,
    double? FrameRate = null, int? SampleRate = null, int? Channels = null, int? Quality = null,
    bool ForceReencode = false)
{
    private static LocalizationManager L => LocalizationManager.Active;
    public string? Validate(MediaStreamInfo stream) => ValidationKey(stream) is { } key ? L.Get(key) : null;

    internal string? ValidationKey(MediaStreamInfo stream)
    {
        var kind = stream.VisualKind switch
        {
            VisualKind.Video => "video", VisualKind.Image => "image",
            VisualKind.Animation => "animation", _ => stream.CodecType
        };
        var encoderKind = kind is "video" or "image" or "animation" ? "video" : kind;
        if (kind is not ("video" or "image" or "animation" or "audio" or "subtitle")
            || Encoder.Kind != encoderKind || !FfmpegConversionCatalog.SupportsKind(Format, kind))
            return "MainValidationUnsupportedType";
        if (!FfmpegConversionCatalog.IsEncoderSuitable(Format, Encoder))
            return "MainValidationUnsupportedOutputFormat";
        if (!Regex.IsMatch(Format.Extension, @"\A[a-zA-Z0-9]{1,10}\z")
            || !Regex.IsMatch(Format.Muxer, @"\A[a-zA-Z0-9_]{1,64}\z")
            || !Regex.IsMatch(Encoder.Name, @"\A[a-zA-Z0-9_\-]{1,64}\z"))
            return "MainValidationInvalidName";
        if (stream.CodecType == "subtitle" && stream.SubtitleKind != SubtitleKind.Text)
            return "MainValidationGraphicSubtitle";
        if (kind == "subtitle" && (Encoder.Name is "dvbsub" or "dvdsub" or "xsub"
            || BitRateKbps is not null))
            return "MainValidationSubtitleEncoder";
        if (Crf is < 0 or > 63 || BitRateKbps is < 8 or > 1_000_000
            || Width is < 16 or > 16384 || Height is < 16 or > 16384
            || FrameRate is { } fps && (!double.IsFinite(fps) || fps <= 0 || fps > 240)
            || SampleRate is < 8000 or > 384000
            || Channels is < 1 or > 32 || Quality is < 0 or > 10)
            return "MainValidationOutOfRange";
        if (kind == "image" && BitRateKbps is not null
            || kind is not ("video" or "animation")
            && (Crf is not null || Width is not null || Height is not null || FrameRate is not null)
            || kind != "audio" && (SampleRate is not null || Channels is not null || Quality is not null))
            return "MainValidationWrongTypeParameters";
        return null;
    }
}

public sealed class FfmpegConversionCatalog
{
    private static LocalizationManager L => LocalizationManager.Active;
    private readonly string _ffmpeg;
    private readonly List<ConversionFormat> _formats;
    private readonly List<ConversionEncoder> _encoders;
    private readonly Dictionary<ConversionFormat, IReadOnlyList<ConversionEncoder>> _confirmedHardware = new();
    private readonly Dictionary<(ConversionFormat Format, string Kind), IReadOnlyList<ConversionEncoder>> _confirmedAdditional = new();
    internal Func<string[], CancellationToken, Task<(int ExitCode, string Output)>>? CommandRunner { get; init; }

    private FfmpegConversionCatalog(string ffmpeg, List<ConversionFormat> formats, List<ConversionEncoder> encoders)
    { _ffmpeg = ffmpeg; _formats = formats; _encoders = encoders; }

    private static readonly HashSet<string> VideoMuxers = new(StringComparer.Ordinal)
    { "mp4", "matroska", "mov", "webm", "avi", "mpegts", "mpeg", "nut", "asf", "flv", "ogv", "3gp", "3g2", "mxf" };
    private static readonly HashSet<string> AnimationMuxers = new(StringComparer.Ordinal)
    { "gif", "apng", "webp" };
    private static readonly HashSet<string> AudioMuxers = new(StringComparer.Ordinal)
    { "mp3", "wav", "flac", "adts", "ac3", "eac3", "dts", "ogg", "opus", "ipod", "aiff", "au", "caf" };
    private static readonly HashSet<string> SubtitleMuxers = new(StringComparer.Ordinal)
    { "srt", "ass", "webvtt", "microdvd", "subviewer", "sami", "lrc" };
    private static readonly HashSet<string> StillImageCodecs = new(StringComparer.Ordinal)
    { "png", "bmp", "tiff", "sgi", "qoi", "webp", "gif", "apng", "pam", "pbm", "pgm", "ppm", "exr", "jpegxl" };
    private static readonly HashSet<string> SingleCodecAudioMuxers = new(StringComparer.Ordinal)
    { "mp3", "flac", "adts", "ac3", "eac3", "dts", "opus" };

    internal static bool IsEncoderSuitable(ConversionFormat format, ConversionEncoder encoder) =>
        Category(format) switch
        {
            "image" or "animation" => encoder.Kind == "video" && encoder.Codec == format.VideoCodec,
            "video" => encoder.Kind == "video" && !StillImageCodecs.Contains(encoder.Codec)
                && (format.Muxer != "webm" || encoder.Codec is "vp8" or "vp9" or "av1"),
            "audio" => encoder.Kind == "audio" && (!SingleCodecAudioMuxers.Contains(format.Muxer)
                || encoder.Codec == format.AudioCodec),
            "subtitle" => encoder.Kind == "subtitle" && encoder.Codec == format.SubtitleCodec,
            _ => false
        };

    internal static string Category(ConversionFormat format) => format.Muxer == "image2" ? "image"
        : AnimationMuxers.Contains(format.Muxer) ? "animation"
        : VideoMuxers.Contains(format.Muxer) ? "video"
        : AudioMuxers.Contains(format.Muxer) ? "audio"
        : SubtitleMuxers.Contains(format.Muxer) ? "subtitle" : "other";

    internal static bool SupportsKind(ConversionFormat format, string kind) =>
        kind switch
        {
            "video" or "animation" => Category(format) is "video" or "animation" && format.VideoCodec is not null,
            "image" => Category(format) == "image" && format.VideoCodec is not null,
            "audio" => Category(format) == "audio" && format.AudioCodec is not null,
            "subtitle" => Category(format) == "subtitle" && format.SubtitleCodec is not null,
            _ => false
        };

    private static int FormatPriority(string kind, ConversionFormat format)
    {
        if (kind == "animation" && Category(format) == "video") kind = "video";
        else if (kind == "video" && Category(format) == "animation") kind = "animation";
        string[] preferred = kind switch
        {
            "video" => ["matroska", "mp4", "mov", "webm", "avi", "mpegts", "mpeg",
                "mxf", "flv", "asf", "ogv", "3gp", "3g2", "nut"],
            "animation" => ["gif", "apng", "webp"],
            "image" => ["jpg", "png", "webp", "bmp", "tiff", "qoi", "gif"],
            "audio" => ["mp3", "wav", "flac", "adts", "ogg", "opus", "ipod"],
            "subtitle" => ["srt", "ass", "webvtt"],
            _ => []
        };
        var value = kind == "image" ? format.Extension : format.Muxer;
        var index = Array.IndexOf(preferred, value);
        return index >= 0 ? index : preferred.Length;
    }

    public IReadOnlyList<ConversionFormat> FormatsFor(string kind) => _formats
        .Where(f => SupportsKind(f, kind) && _encoders.Any(e => e.Kind ==
            (kind is "video" or "image" or "animation" ? "video" : kind)
            && e.Codec == f.DefaultCodec(kind)
            && (kind != "subtitle" || e.Name is not ("dvbsub" or "dvdsub" or "xsub"))))
        .OrderBy(f => kind == "animation" ? Category(f) == "animation" ? 0 : 1
            : kind == "video" ? Category(f) == "video" ? 0 : 1 : 0)
        .ThenBy(f => FormatPriority(kind, f))
        .ThenBy(f => f.Extension, StringComparer.OrdinalIgnoreCase).ToArray();

    internal ConversionFormat? FindFormat(string muxer, string extension, string kind) =>
        _formats.FirstOrDefault(format => format.Muxer == muxer && format.Extension == extension
            && SupportsKind(format, kind));

    public IReadOnlyList<ConversionEncoder> EncodersFor(string kind) => _encoders
        .Where(e => e.Kind == (kind is "image" or "animation" ? "video" : kind)
            && (kind != "subtitle" || e.Name is not ("dvbsub" or "dvdsub" or "xsub")))
        .Where(e => kind != "subtitle" || e.Name switch
        {
            "subrip" => !_encoders.Any(candidate => candidate.Name == "srt" && candidate.Kind == "subtitle"),
            "ssa" => !_encoders.Any(candidate => candidate.Name == "ass" && candidate.Kind == "subtitle"),
            _ => true
        })
        .OrderBy(e => e.Name, StringComparer.OrdinalIgnoreCase).ToArray();

    internal IReadOnlyList<ConversionEncoder> ConfirmedHardwareFor(ConversionFormat format) =>
        _confirmedHardware.TryGetValue(format, out var encoders) ? encoders : [];

    internal void RememberConfirmedHardware(ConversionFormat format, IEnumerable<ConversionEncoder> encoders)
    {
        if (!_formats.Contains(format)) return;
        _confirmedHardware[format] = encoders.Where(e => e.IsHardware && _encoders.Contains(e))
            .Distinct().ToArray();
    }

    internal IReadOnlyList<ConversionEncoder> ConfirmedAdditionalFor(ConversionFormat format, string kind) =>
        _confirmedAdditional.TryGetValue((format, kind), out var encoders) ? encoders : [];

    internal void RememberConfirmedAdditional(ConversionFormat format, string kind,
        IEnumerable<ConversionEncoder> encoders)
    {
        if (!_formats.Contains(format) || !SupportsKind(format, kind)) return;
        _confirmedAdditional[(format, kind)] = encoders
            .Where(e => e.Kind == (kind is "image" or "animation" ? "video" : kind)
                && !e.IsHardware && _encoders.Contains(e) && IsEncoderSuitable(format, e)).Distinct().ToArray();
    }

    public ConversionEncoder? SuggestedEncoder(ConversionFormat format, string kind) =>
        EncodersFor(kind).Where(e => e.Codec == format.DefaultCodec(kind))
            .OrderBy(e => e.IsHardware)
            .ThenBy(e => (format.Muxer is "avi" or "3gp" or "3g2") && e.Name == "mpeg4" ? 0 : 1)
            .ThenBy(e => e.Name.StartsWith("lib", StringComparison.Ordinal) ? 0 : 1)
            .ThenBy(e => e.Name, StringComparer.Ordinal)
            .FirstOrDefault();

    public bool Contains(ConversionSettings settings) => _formats.Contains(settings.Format) && _encoders.Contains(settings.Encoder);

    public async Task<bool> CheckCopyAsync(string source, int streamIndex, ConversionFormat format,
        CancellationToken token, MediaStreamInfo? stream = null)
    {
        if (!_formats.Contains(format) || streamIndex < 0) return false;
        // Video copy is decided from existing FFprobe metadata; the actual muxer checks
        // compatibility during export. Do not create and decode a disposable video file.
        if (stream?.VisualKind == VisualKind.Video) return true;
        var directory = Path.Combine(Path.GetTempPath(), "mediaunseal-copy-" + Guid.NewGuid().ToString("N"));
        var output = Path.Combine(directory, "output." + format.Extension);
        try
        {
            Directory.CreateDirectory(directory);
            var copyOptions = stream is null ? Array.Empty<string>() : TrackExporter.CopyVideoOptions(
                new(stream, format.Extension, format.Muxer, false));
            var result = await RunAsync(["-hide_banner", "-loglevel", "error", "-nostdin", "-y",
                "-i", source, "-map", $"0:{streamIndex}", "-t", "0.1", "-c", "copy",
                ..copyOptions, "-f", format.Muxer, output], token).ConfigureAwait(false);
            if (result.ExitCode != 0 || !File.Exists(output) || new FileInfo(output).Length == 0) return false;
            if (Category(format) is not ("video" or "animation")) return true;
            var decoded = await RunAsync(["-hide_banner", "-loglevel", "error", "-nostdin", "-i", output,
                "-map", "0:v:0", "-frames:v", "1", "-f", "null", "-"], token).ConfigureAwait(false);
            return decoded.ExitCode == 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or Win32Exception
            or TimeoutException or InvalidDataException) { return false; }
        finally
        {
            try { File.Delete(output); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            try { Directory.Delete(directory, false); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }

    public async Task<bool> SupportsCrfAsync(ConversionEncoder encoder, CancellationToken token)
    {
        if (!_encoders.Contains(encoder) || encoder.Kind != "video") return false;
        var result = await RunAsync(["-hide_banner", "-h", "encoder=" + encoder.Name], token).ConfigureAwait(false);
        return result.ExitCode == 0 && Regex.IsMatch(result.Output, @"(?m)^\s*-crf\s");
    }

    public static Task<FfmpegConversionCatalog> DiscoverAsync(string ffmpeg, CancellationToken token) =>
        DiscoverAsync(ffmpeg, token, null);

    internal static async Task<FfmpegConversionCatalog> DiscoverAsync(string ffmpeg, CancellationToken token,
        Func<string[], CancellationToken, Task<(int ExitCode, string Output)>>? commandRunner)
    {
        var catalog = new FfmpegConversionCatalog(ffmpeg, [], []) { CommandRunner = commandRunner };
        var encoders = await catalog.RunAsync(["-hide_banner", "-encoders"], token).ConfigureAwait(false);
        var muxers = await catalog.RunAsync(["-hide_banner", "-muxers"], token).ConfigureAwait(false);
        if (encoders.ExitCode != 0 || muxers.ExitCode != 0)
            throw new InvalidDataException("Не удалось получить возможности установленного FFmpeg.");
        catalog._encoders.AddRange(ParseEncoders(encoders.Output));
        var names = ParseMuxerNames(muxers.Output).ToArray();
        using var gate = new SemaphoreSlim(4);
        var tasks = names.Select(async name =>
        {
            await gate.WaitAsync(token).ConfigureAwait(false);
            try
            {
                var result = await catalog.RunAsync(["-hide_banner", "-h", "muxer=" + name], token).ConfigureAwait(false);
                return result.ExitCode == 0 ? ParseFormat(name, result.Output) : null;
            }
            catch (TimeoutException) { return null; }
            finally { gate.Release(); }
        }).ToArray();
        var formats = await Task.WhenAll(tasks).ConfigureAwait(false);
        catalog._formats.AddRange(formats.OfType<ConversionFormat>().Where(f => f.Muxer != "image2")
            .Select(f => (f.Muxer is "3gp" or "3g2")
                && catalog._encoders.Any(e => e.Kind == "video" && e.Name == "mpeg4")
                    ? f with { VideoCodec = "mpeg4" }
                    : f.Muxer == "caf" && catalog._encoders.Any(e => e.Name == "alac" && e.Kind == "audio")
                        ? f with { AudioCodec = "alac" } : f));
        if (names.Contains("image2"))
        {
            foreach (var (extension, codec) in new[]
            {
                ("jpg", "mjpeg"), ("png", "png"), ("bmp", "bmp"), ("tiff", "tiff"),
                ("qoi", "qoi"), ("webp", "webp"), ("gif", "gif")
            })
                if (catalog._encoders.Any(e => e.Kind == "video" && e.Codec == codec))
                    catalog._formats.Add(new("image2", extension, codec.ToUpperInvariant(), codec, null, null));
        }
        return catalog;
    }

    public async Task<(bool Compatible, string Reason, bool InfrastructureFailure)> CheckCombinationAsync(
        ConversionFormat format, ConversionEncoder encoder, CancellationToken token,
        ConversionSettings? settings = null, Action<string>? diagnostics = null)
    {
        if (!_formats.Contains(format) || !_encoders.Contains(encoder)
            || format.DefaultCodec(encoder.Kind) is null
            || !IsEncoderSuitable(format, encoder)
            || settings is not null && (settings.Format != format || settings.Encoder != encoder))
            return (false, L.Get("MainValidationUnsupportedOutputFormat"), false);
        var directory = Path.Combine(Path.GetTempPath(), "mediaunseal-probe-" + Guid.NewGuid().ToString("N"));
        var output = Path.Combine(directory, "output." + format.Extension);
        try
        {
            Directory.CreateDirectory(directory);
            string[] input;
            if (encoder.Kind == "video")
                input = format.Muxer == "image2"
                    ? ["-f", "lavfi", "-i", "color=c=black:s=64x64:r=25:d=0.2", "-frames:v", "1"]
                    : ["-f", "lavfi", "-i", format.Muxer is "3gp" or "3g2"
                        ? "testsrc2=size=176x144:rate=25:duration=0.2"
                        : "testsrc2=size=320x240:rate=25:duration=0.2", "-frames:v", "3"];
            else if (encoder.Kind == "audio")
                input = ["-f", "lavfi", "-i", "sine=frequency=440:sample_rate=48000:duration=0.2", "-t", "0.2"];
            else
            {
                var subtitle = Path.Combine(directory, "probe.srt");
                await File.WriteAllTextAsync(subtitle, "1\n00:00:00,000 --> 00:00:01,000\nTest\n", token).ConfigureAwait(false);
                input = ["-i", subtitle];
            }
            var args = new List<string> { "-hide_banner", "-loglevel", "error", "-nostdin", "-y" };
            args.AddRange(input);
            args.AddRange(["-map", "0:0", "-c", encoder.Name]);
            TrackExporter.AppendConversionOptions(args, settings ?? new ConversionSettings(format, encoder));
            if (encoder.Kind == "video") TrackExporter.AppendVisualOutputOptions(args,
                settings ?? new ConversionSettings(format, encoder),
                encoder.Name is "libvpx" or "libvpx-vp9" or "mpeg1video" or "mpeg2video");
            if (format.Muxer == "image2") args.AddRange(["-frames:v", "1", "-update", "1"]);
            args.AddRange(["-f", format.Muxer, output]);
            var result = await RunAsync(args.ToArray(), token).ConfigureAwait(false);
            if (result.ExitCode != 0 && diagnostics is not null)
                diagnostics($"{encoder.Name}: FFmpeg exit code {result.ExitCode}; {result.Output}");
            if (result.ExitCode != 0 || !File.Exists(output) || new FileInfo(output).Length == 0)
                return (false, L.Format("MainValidationProbeFileFailed",
                    encoder.Name, format.Extension.ToUpperInvariant(), result.ExitCode), false);
            if (CommandRunner is null && encoder.Kind is "video" or "audio")
            {
                var decoded = await RunAsync(encoder.Kind == "video"
                    ? ["-hide_banner", "-loglevel", "error", "-nostdin", "-i", output,
                        "-map", "0:v:0", "-frames:v", "1", "-f", "null", "-"]
                    : ["-hide_banner", "-loglevel", "error", "-nostdin", "-i", output,
                        "-map", "0:a:0", "-t", "0.05", "-f", "null", "-"], token).ConfigureAwait(false);
                if (decoded.ExitCode != 0)
                {
                    diagnostics?.Invoke($"{encoder.Name}: output stream could not be decoded; " +
                        $"FFmpeg exit code {decoded.ExitCode}; {decoded.Output}");
                    return (false, L.Format("MainValidationProbeFileFailed",
                        encoder.Name, format.Extension.ToUpperInvariant(), decoded.ExitCode), false);
                }
            }
            return (true, "", false);
        }
        catch (TimeoutException)
        {
            diagnostics?.Invoke($"{encoder.Name}: FFmpeg timed out.");
            return (false, L.Format("MainValidationEncoderTimeout", encoder.Name), true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or Win32Exception
            or InvalidDataException)
        {
            diagnostics?.Invoke($"{encoder.Name}: {ex.GetType().Name}: {ex.Message}");
            return (false, L.Format("MainValidationEncoderCheckFailed", encoder.Name, format.Extension.ToUpperInvariant()), true);
        }
        finally
        {
            foreach (var file in new[] { Path.Combine(directory, "probe.srt"), output })
                try { File.Delete(file); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            try { Directory.Delete(directory, recursive: false); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }

    internal static IEnumerable<ConversionEncoder> ParseEncoders(string output)
    {
        foreach (var line in output.Split('\n'))
        {
            var match = Regex.Match(line, @"^\s*([VAS])[.A-Z]{5}\s+([A-Za-z0-9_\-]+)\s+(.+)");
            if (!match.Success) continue;
            var kind = match.Groups[1].Value switch { "V" => "video", "A" => "audio", _ => "subtitle" };
            var name = match.Groups[2].Value;
            var description = match.Groups[3].Value.Trim();
            var codec = Regex.Match(description, @"\(codec ([A-Za-z0-9_\-]+)\)");
            yield return new(name, codec.Success ? codec.Groups[1].Value : name, kind, description);
        }
    }

    internal static IEnumerable<string> ParseMuxerNames(string output)
    {
        foreach (var line in output.Split('\n'))
        {
            var match = Regex.Match(line, @"^\s*E[d ]\s+([a-z0-9_]+)\s+");
            if (match.Success && match.Groups[1].Value is not ("null" or "tee" or "fifo" or "segment"))
                yield return match.Groups[1].Value;
        }
    }

    internal static ConversionFormat? ParseFormat(string name, string output)
    {
        var extension = Regex.Match(output, @"(?m)^\s*Common extensions:\s*([a-zA-Z0-9]+)");
        if (!extension.Success) return null;
        var description = Regex.Match(output, @"(?m)^Muxer \S+ \[(.+?)\]:");
        string? Default(string kind)
        {
            var match = Regex.Match(output, @"(?m)^\s*Default " + kind + @" codec:\s*([a-zA-Z0-9_]+)");
            return match.Success && match.Groups[1].Value != "none" ? match.Groups[1].Value : null;
        }
        var format = new ConversionFormat(name, extension.Groups[1].Value.ToLowerInvariant(),
            description.Success ? description.Groups[1].Value : name,
            Default("video"), Default("audio"), Default("subtitle"));
        return format.VideoCodec is null && format.AudioCodec is null && format.SubtitleCodec is null ? null : format;
    }

    private async Task<(int ExitCode, string Output)> RunAsync(string[] args, CancellationToken token)
    {
        if (CommandRunner is not null) return await CommandRunner(args, token).ConfigureAwait(false);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(8));
        var start = new ProcessStartInfo(_ffmpeg)
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8
        };
        foreach (var arg in args) start.ArgumentList.Add(arg);
        using var process = new Process { StartInfo = start };
        if (!process.Start()) throw new Win32Exception("FFmpeg не запустился.");
        try
        {
            var stdout = process.StandardOutput.ReadToEndAsync(deadline.Token);
            var stderr = process.StandardError.ReadToEndAsync(deadline.Token);
            await process.WaitForExitAsync(deadline.Token).ConfigureAwait(false);
            var output = await stdout.ConfigureAwait(false) + await stderr.ConfigureAwait(false);
            if (output.Length > 2_000_000) throw new InvalidDataException("Ответ FFmpeg слишком велик.");
            return (process.ExitCode, output);
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        { throw new TimeoutException("FFmpeg слишком долго отвечает на запрос возможностей."); }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
            }
        }
    }
}
