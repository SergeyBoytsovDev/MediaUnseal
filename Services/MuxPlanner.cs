using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Diagnostics;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using MediaUnseal.Models;

namespace MediaUnseal.Services;

internal sealed record MuxStreamPlan(MediaStreamInfo Stream, string Encoder, string? OutputCodec = null)
{
    public bool Copy => Encoder == "copy";
}

internal sealed record MuxPlan(ConversionFormat Format, IReadOnlyList<MuxStreamPlan> Streams,
    string? ErrorKey = null, string? AudioCodec = null, string? SubtitleCodec = null,
    bool GeneratePts = false)
{
    public bool IsValid => ErrorKey is null && Streams.Count > 0;
}

internal sealed record MuxOutputValidation(bool Success, string Reason);

internal static class MuxPlanner
{
    private static readonly string[] PreferredMuxers =
        ["matroska", "mp4", "mov", "avi", "webm", "mpegts", "mpeg", "nut",
         "asf", "flv", "ogv", "3gp", "3g2", "mxf"];

    private static readonly Dictionary<string, string> SupportedTypes =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["matroska"] = "video audio subtitle", ["mp4"] = "video audio subtitle",
            ["mov"] = "video audio subtitle", ["avi"] = "video audio",
            ["webm"] = "video audio subtitle", ["mpegts"] = "video audio subtitle",
            ["mpeg"] = "video audio", ["nut"] = "video audio subtitle",
            ["asf"] = "video audio", ["flv"] = "video audio",
            ["ogv"] = "video audio", ["3gp"] = "video audio subtitle",
            ["3g2"] = "video audio subtitle", ["mxf"] = "video audio"
        };

    // Container/codec pairs confirmed with FFmpeg and FFprobe. Encoder discovery below
    // still determines whether this installation can produce a particular codec.
    private static readonly Dictionary<string, string> AudioCodecs = new(StringComparer.Ordinal)
    {
        ["matroska"] = "mp3 pcm_s16le flac aac vorbis opus ac3 pcm_s16be alac dts eac3",
        ["mp4"] = "mp3 pcm_s16le flac aac opus ac3 pcm_s16be alac eac3",
        ["mov"] = "mp3 pcm_s16le aac ac3 pcm_s16be alac eac3",
        ["avi"] = "mp3 pcm_s16le flac aac vorbis ac3 eac3",
        ["webm"] = "vorbis opus",
        ["mpegts"] = "mp3 pcm_s16le flac aac vorbis opus ac3 pcm_s16be alac eac3",
        ["mpeg"] = "mp3 ac3 pcm_s16be",
        ["nut"] = "mp3 pcm_s16le flac aac vorbis opus ac3 pcm_s16be eac3",
        ["asf"] = "mp3 pcm_s16le flac aac vorbis ac3 eac3",
        ["flv"] = "mp3 flac aac opus ac3 eac3",
        ["ogv"] = "flac vorbis opus",
        ["3gp"] = "aac", ["3g2"] = "aac", ["mxf"] = "pcm_s16le"
    };

    internal static bool SupportsAudioCodec(string muxer, string codec) =>
        AudioCodecs.TryGetValue(muxer, out var codecs)
        && codecs.Split(' ').Contains(codec, StringComparer.Ordinal);

    private static bool SupportsStreamCodec(string muxer, string kind, string codec) => kind switch
    {
        "audio" => SupportsAudioCodec(muxer, codec),
        "video" => muxer switch
        {
            "matroska" or "nut" => true,
            "mp4" or "mov" => codec is "h264" or "hevc" or "mpeg4" or "av1",
            "avi" => codec is "mpeg4" or "mjpeg" or "h264",
            "webm" => codec is "vp8" or "vp9" or "av1",
            "mpegts" => codec is "h264" or "hevc" or "mpeg2video" or "mpeg1video",
            "mpeg" or "mxf" => codec is "mpeg1video" or "mpeg2video",
            "asf" => codec is "msmpeg4v3" or "wmv1" or "wmv2" or "wmv3" or "mpeg4",
            "flv" => codec is "h264" or "flv1",
            "ogv" => codec == "theora",
            "3gp" or "3g2" => codec is "h263" or "mpeg4" or "h264",
            _ => false
        },
        "subtitle" => muxer switch
        {
            "matroska" or "nut" => true,
            "mp4" or "mov" or "3gp" or "3g2" => codec == "mov_text",
            "webm" => codec == "webvtt",
            _ => false
        },
        _ => false
    };

    internal static IReadOnlyList<ConversionFormat> Formats(FfmpegConversionCatalog catalog)
    {
        var normalOrder = catalog.FormatsFor("video")
            .Where(f => SupportedTypes.ContainsKey(f.Muxer)).ToArray();
        return normalOrder.Concat(catalog.MuxFormats()
            .Where(f => SupportedTypes.ContainsKey(f.Muxer) && !normalOrder.Contains(f))
            .OrderBy(f => Array.IndexOf(PreferredMuxers, f.Muxer))).ToArray();
    }

    internal static bool CanCombine(MediaInfo media, IReadOnlyList<MediaStreamInfo> selected,
        FfmpegConversionCatalog catalog) => selected.Count >= 2
        && Formats(catalog).Any(format => Create(media, selected, format, catalog).IsValid);

    internal static ConversionFormat? SourceFormat(MediaInfo media, FfmpegConversionCatalog catalog)
    {
        var formats = Formats(catalog);
        var extension = Path.GetExtension(media.FullPath).TrimStart('.');
        return formats.FirstOrDefault(f => f.Extension.Equals(extension, StringComparison.OrdinalIgnoreCase))
            ?? formats.FirstOrDefault(f => media.Container?.Split(',')
                .Contains(f.Muxer, StringComparer.OrdinalIgnoreCase) == true);
    }

    // FFmpeg may accept a short sample yet fail to write the trailer for these pairs.
    internal static bool HasUnreliableStream(MuxPlan plan) => plan.Streams.Any(stream =>
        stream.Stream.CodecType == "audio" && (stream.OutputCodec switch
        {
            "vorbis" => plan.Format.Muxer is "mp4" or "mov",
            "opus" or "flac" => plan.Format.Muxer == "mov",
            _ => false
        }));

    internal static async Task<bool> VerifyOutputAsync(string ffprobePath, string output,
        MuxPlan plan, CancellationToken token)
        => (await InspectOutputAsync(ffprobePath, output, plan, token).ConfigureAwait(false)).Success;

    internal static async Task<MuxOutputValidation> InspectOutputAsync(string ffprobePath,
        string output, MuxPlan plan, CancellationToken token)
    {
        var start = new ProcessStartInfo(ffprobePath)
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true
        };
        foreach (var arg in new[] { "-v", "error", "-show_entries",
            "stream=codec_type,codec_name", "-of", "json", output })
            start.ArgumentList.Add(arg);
        using var process = Process.Start(start);
        if (process is null) return new(false, "FFprobe could not be started.");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(8));
        try
        {
            var stdout = ReadProbeOutputAsync(process.StandardOutput, 1024 * 1024, false, deadline.Token);
            var stderr = ReadProbeOutputAsync(process.StandardError, 12000, true, deadline.Token);
            await Task.WhenAll(stdout, stderr, process.WaitForExitAsync(deadline.Token))
                .ConfigureAwait(false);
            if (process.ExitCode != 0)
                return new(false, $"FFprobe exited with code {process.ExitCode}: {await stderr.ConfigureAwait(false)}");
            using var document = JsonDocument.Parse(await stdout.ConfigureAwait(false));
            if (!document.RootElement.TryGetProperty("streams", out var streams)
                || streams.ValueKind != JsonValueKind.Array)
                return new(false, "FFprobe did not return a stream array.");
            if (streams.GetArrayLength() != plan.Streams.Count)
                return new(false, $"FFprobe found {streams.GetArrayLength()} streams; expected {plan.Streams.Count}.");
            for (var i = 0; i < plan.Streams.Count; i++)
            {
                var outputStream = streams[i];
                if (!outputStream.TryGetProperty("codec_type", out var type)
                    || type.GetString() != plan.Streams[i].Stream.CodecType
                    || !outputStream.TryGetProperty("codec_name", out var codec)
                    || codec.GetString() != plan.Streams[i].OutputCodec)
                    return new(false, $"FFprobe stream {i} does not match the planned type/codec " +
                        $"{plan.Streams[i].Stream.CodecType}/{plan.Streams[i].OutputCodec}.");
            }
            return new(true, "Output streams and codecs match the plan.");
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        { return new(false, "FFprobe validation timed out after 8 seconds."); }
        catch (JsonException ex) { return new(false, $"FFprobe returned invalid JSON: {ex.Message}"); }
        catch (InvalidOperationException ex) { return new(false, $"FFprobe returned invalid stream data: {ex.Message}"); }
        finally
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
            catch (InvalidOperationException) { }
        }
    }

    private static async Task<string> ReadProbeOutputAsync(StreamReader reader, int limit, bool keepTail,
        CancellationToken token)
    {
        var text = new System.Text.StringBuilder();
        var buffer = new char[4096];
        int count;
        while ((count = await reader.ReadAsync(buffer, token)) != 0)
        {
            text.Append(buffer, 0, count);
            if (text.Length <= limit) continue;
            if (!keepTail) throw new InvalidDataException("FFprobe validation response exceeds the size limit.");
            text.Remove(0, text.Length - limit);
        }
        return text.ToString();
    }

    internal static MuxPlan Create(MediaInfo media, IReadOnlyList<MediaStreamInfo> selected,
        ConversionFormat format, FfmpegConversionCatalog catalog,
        string? audioCodec = null, string? subtitleCodec = null)
    {
        if (!Formats(catalog).Contains(format)) return new(format, [], "MainMuxUnavailable");
        if (selected.Count == 0) return new(format, [], "MainMuxSelectStreams");
        if (format.Muxer == "mxf" && (selected[0].CodecType != "video"
            || selected.Count(s => s.CodecType == "video") != 1))
            return new(format, [], "MainMuxUnavailable");
        var streams = new List<MuxStreamPlan>(selected.Count);
        foreach (var stream in selected)
        {
            if (!media.Streams.Contains(stream) || stream.Index is not >= 0 ||
                stream.CodecType is not ("video" or "audio" or "subtitle") ||
                stream.Codec is null or "csv_universal" or "csv_adr" ||
                stream.VisualKind is VisualKind.Image or VisualKind.Animation)
                return new(format, [], "MainMuxUnsupportedStream");
            var kind = stream.CodecType;
            if (!SupportedTypes[format.Muxer].Split(' ').Contains(kind))
                return new(format, [], "MainMuxUnavailable");
            var requested = kind == "audio" ? audioCodec : kind == "subtitle" ? subtitleCodec : null;
            if (kind == "audio" && requested is not null
                && !SupportsAudioCodec(format.Muxer, requested))
                return new(format, [], "MainMuxRejected");
            if (kind == "audio" && requested is not null &&
                (requested == "vorbis" && format.Muxer is "mp4" or "mov"
                 || requested is "opus" or "flac" && format.Muxer == "mov"))
                return new(format, [], "MainMuxRejected");
            if (kind == "subtitle" && requested is "csv" or "csv_universal" or "csv_adr")
                return new(format, [], "MainMuxUnsupportedStream");
            if (requested is null || stream.Codec == requested)
            {
                if (SupportsStreamCodec(format.Muxer, kind, stream.Codec))
                    streams.Add(new(stream, "copy", stream.Codec));
                else if (requested is not null)
                    return new(format, [], "MainMuxRejected");
                else
                {
                    if (kind == "subtitle" && stream.SubtitleKind != SubtitleKind.Text)
                        return new(format, [], "MainMuxGraphicSubtitle");
                    var fallback = MuxEncoders(format, kind, catalog)
                        .FirstOrDefault(e => e.Name != "dca"
                            && SupportsStreamCodec(format.Muxer, kind, e.Codec));
                    if (fallback is null) return new(format, [], "MainMuxNoEncoder");
                    streams.Add(new(stream, fallback.Name, fallback.Codec));
                }
            }
            else
            {
                if (kind == "subtitle" && stream.SubtitleKind != SubtitleKind.Text)
                    return new(format, [], "MainMuxGraphicSubtitle");
                var encoder = catalog.EncodersFor(kind).Where(e => e.Codec == requested
                    && e.Name != "dca")
                    .OrderBy(e => e.IsHardware).ThenBy(e => e.Name.StartsWith("lib", StringComparison.Ordinal) ? 0 : 1)
                    .ThenBy(e => e.Name, StringComparer.Ordinal).FirstOrDefault();
                if (encoder is null) return new(format, [], "MainMuxNoEncoder");
                streams.Add(new(stream, encoder.Name, encoder.Codec));
            }
        }
        var generatePts = format.Muxer == "flv" && media.Container?.Split(',')
            .Contains("avi", StringComparer.OrdinalIgnoreCase) == true;
        return new(format, streams, AudioCodec: audioCodec, SubtitleCodec: subtitleCodec,
            GeneratePts: generatePts);
    }

    internal static async Task<MuxPlan> ResolveAsync(MediaInfo media, MuxPlan plan,
        FfmpegConversionCatalog catalog, bool preserveMetadata, CancellationToken token)
    {
        if (!plan.IsValid || await catalog.CheckMuxAsync(media, plan, preserveMetadata, token)) return plan;
        var changed = false;
        for (var i = 0; i < plan.Streams.Count; i++)
        {
            token.ThrowIfCancellationRequested();
            var single = ProbeStream(plan, i);
            if (await catalog.CheckMuxAsync(media, single, preserveMetadata, token)) continue;
            MuxPlan? fallback = null;
            foreach (var candidate in ReencodeCandidates(plan, i, catalog))
            {
                token.ThrowIfCancellationRequested();
                var probe = ProbeStream(candidate, i);
                if (!await catalog.CheckMuxAsync(media, probe, preserveMetadata, token)) continue;
                fallback = candidate;
                break;
            }
            if (fallback is null)
                return plan with { ErrorKey = plan.Streams[i].Stream.SubtitleKind == SubtitleKind.Graphic
                    ? "MainMuxGraphicSubtitle" : "MainMuxNoEncoder" };
            plan = fallback;
            changed = true;
        }
        return changed && await catalog.CheckMuxAsync(media, plan, preserveMetadata, token)
            ? plan : plan with { ErrorKey = "MainMuxRejected" };
    }

    private static MuxPlan ProbeStream(MuxPlan plan, int index) => plan.Format.Muxer == "mxf"
        && plan.Streams[index].Stream.CodecType == "audio"
        && plan.Streams[0].Stream.CodecType == "video"
            ? plan with { Streams = [plan.Streams[0], plan.Streams[index]] }
            : plan with { Streams = [plan.Streams[index]] };

    private static IEnumerable<MuxPlan> ReencodeCandidates(MuxPlan plan, int streamIndex,
        FfmpegConversionCatalog catalog)
    {
        if (!plan.IsValid || streamIndex < 0 || streamIndex >= plan.Streams.Count
            || !plan.Streams[streamIndex].Copy) yield break;
        var stream = plan.Streams[streamIndex].Stream;
        if (stream.CodecType == "subtitle" && stream.SubtitleKind != SubtitleKind.Text) yield break;
        var requested = stream.CodecType == "audio" ? plan.AudioCodec
            : stream.CodecType == "subtitle" ? plan.SubtitleCodec : null;
        foreach (var encoder in requested is null
            ? MuxEncoders(plan.Format, stream.CodecType!, catalog)
            : catalog.EncodersFor(stream.CodecType!).Where(e => e.Codec == requested)
                .OrderBy(e => e.IsHardware).ThenBy(e => e.Name, StringComparer.Ordinal))
        {
            var streams = plan.Streams.ToArray();
            streams[streamIndex] = new(stream, encoder.Name, encoder.Codec);
            yield return plan with { Streams = streams };
        }
    }

    private static IEnumerable<ConversionEncoder> MuxEncoders(ConversionFormat format, string kind,
        FfmpegConversionCatalog catalog)
    {
        var subtitleCodec = kind == "subtitle" ? format.Muxer switch
        {
            "matroska" or "nut" => "ass",
            "mp4" or "mov" or "3gp" or "3g2" => "mov_text",
            "webm" => "webvtt",
            _ => null
        } : null;
        var alternatives = kind switch
        {
            "video" when format.Muxer is "mp4" or "mov" or "3gp" or "3g2" => "h264 hevc mpeg4 av1",
            "video" when format.Muxer == "webm" => "vp9 vp8 av1",
            "video" when format.Muxer == "avi" => "mpeg4 mjpeg h264",
            "video" when format.Muxer == "matroska" => "h264 hevc mpeg4 vp9 av1",
            "video" when format.Muxer == "flv" => "h264 flv1",
            "audio" when format.Muxer is "mp4" or "mov" or "3gp" or "3g2" => "aac alac mp3 ac3",
            "audio" when format.Muxer == "webm" => "opus vorbis",
            "audio" when format.Muxer == "avi" => "mp3 ac3 pcm_s16le",
            "audio" when format.Muxer == "matroska" => "aac opus flac vorbis",
            "audio" when format.Muxer == "mpegts" => "aac mp3 ac3 pcm_s16le",
            "audio" when format.Muxer == "mpeg" => "mp3 ac3",
            "audio" when format.Muxer == "nut" => "aac opus flac mp3",
            "audio" when format.Muxer == "asf" => "aac mp3 ac3",
            "audio" when format.Muxer == "flv" => "aac mp3",
            "audio" when format.Muxer == "ogv" => "vorbis opus flac",
            "audio" when format.Muxer == "mxf" => "pcm_s16le",
            "subtitle" when subtitleCodec is not null => subtitleCodec,
            _ => ""
        };
        var visibleCodecs = kind == "audio"
            ? catalog.FormatsFor(kind).Select(f => f.DefaultCodec(kind))
                .ToHashSet(StringComparer.Ordinal) : null;
        var codecs = new[] { kind == "subtitle" ? subtitleCodec
            : kind == "video" && format.Muxer == "flv" ? "h264" : format.DefaultCodec(kind) }
            .Concat(alternatives.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            .Where(codec => codec is not null && (visibleCodecs is null || visibleCodecs.Contains(codec)))
            .Distinct(StringComparer.Ordinal).ToArray();
        foreach (var codec in codecs)
            foreach (var encoder in catalog.EncodersFor(kind).Where(e => e.Codec == codec)
                .OrderBy(e => e.IsHardware).ThenBy(e => e.Name.StartsWith("lib", StringComparison.Ordinal) ? 0 : 1)
                .ThenBy(e => e.Name, StringComparer.Ordinal))
                yield return encoder;
    }

    internal static string[] BuildArguments(string source, string output, MuxPlan plan, bool preserveMetadata)
    {
        if (!plan.IsValid) throw new ArgumentException("The mux plan is invalid.", nameof(plan));
        var args = new List<string> { "-hide_banner", "-loglevel", "error", "-nostdin",
            "-stats_period", "0.5", "-progress", "pipe:2" };
        if (plan.GeneratePts) args.AddRange(["-fflags", "+genpts"]);
        args.AddRange(["-i", source]);
        for (var i = 0; i < plan.Streams.Count; i++)
        {
            args.AddRange(["-map", $"0:{plan.Streams[i].Stream.Index}", $"-c:{i}", plan.Streams[i].Encoder]);
            if (OpusChannelLayout.FilterFor(plan.Streams[i].Encoder, plan.Streams[i].Stream) is { } filter)
                args.AddRange([$"-filter:{i}", filter]);
        }
        args.AddRange(["-map_metadata", preserveMetadata ? "0" : "-1",
            "-map_chapters", preserveMetadata ? "0" : "-1"]);
        if (!preserveMetadata) args.AddRange(["-map_metadata:s", "-1"]);
        args.AddRange(["-f", plan.Format.Muxer, output]);
        return args.ToArray();
    }
}
