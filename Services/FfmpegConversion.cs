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
using TrackExtractor.Models;

namespace TrackExtractor.Services;

public sealed record ConversionFormat(string Muxer, string Extension, string Description,
    string? VideoCodec, string? AudioCodec, string? SubtitleCodec)
{
    public string Display => $"{Extension.ToUpperInvariant()} — {Description} ({Muxer})";
    public string? DefaultCodec(string kind) => kind switch
    {
        "video" => VideoCodec, "audio" => AudioCodec, "subtitle" => SubtitleCodec, _ => null
    };
}

public sealed record ConversionEncoder(string Name, string Codec, string Kind, string Description)
{
    public string Display => $"{Name} — {Description}";
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
    double? FrameRate = null, int? SampleRate = null, int? Channels = null, int? Quality = null)
{
    public string? Validate(MediaStreamInfo stream)
    {
        var kind = stream.VisualKind == VisualKind.Video ? "video" : stream.CodecType;
        if (kind is not ("video" or "audio" or "subtitle") || Encoder.Kind != kind
            || Format.DefaultCodec(kind) is null)
            return "Выбранный формат не поддерживает этот тип потока.";
        if (!Regex.IsMatch(Format.Extension, @"\A[a-zA-Z0-9]{1,10}\z")
            || !Regex.IsMatch(Format.Muxer, @"\A[a-zA-Z0-9_]{1,64}\z")
            || !Regex.IsMatch(Encoder.Name, @"\A[a-zA-Z0-9_\-]{1,64}\z"))
            return "Некорректное имя выходного формата или кодировщика.";
        if (stream.CodecType == "subtitle" && stream.SubtitleKind != SubtitleKind.Text)
            return "Графические субтитры нельзя преобразовать в текст без OCR.";
        if (kind == "subtitle" && (Encoder.Name is "dvbsub" or "dvdsub" or "xsub"
            || BitRateKbps is not null))
            return "Выбранный кодировщик не создаёт текстовые субтитры.";
        if (Crf is < 0 or > 63 || BitRateKbps is < 8 or > 1_000_000
            || Width is < 16 or > 16384 || Height is < 16 or > 16384
            || FrameRate is { } fps && (!double.IsFinite(fps) || fps <= 0 || fps > 240)
            || SampleRate is < 8000 or > 384000
            || Channels is < 1 or > 32 || Quality is < 0 or > 10)
            return "Параметры конвертации выходят за допустимые пределы.";
        if (kind != "video" && (Crf is not null || Width is not null || Height is not null || FrameRate is not null)
            || kind != "audio" && (SampleRate is not null || Channels is not null || Quality is not null))
            return "Параметры не соответствуют типу потока.";
        return null;
    }
}

public sealed class FfmpegConversionCatalog
{
    private readonly string _ffmpeg;
    private readonly List<ConversionFormat> _formats;
    private readonly List<ConversionEncoder> _encoders;
    private readonly Dictionary<ConversionFormat, IReadOnlyList<ConversionEncoder>> _confirmedHardware = new();
    private readonly Dictionary<(ConversionFormat Format, string Kind), IReadOnlyList<ConversionEncoder>> _confirmedAdditional = new();
    internal Func<string[], CancellationToken, Task<(int ExitCode, string Output)>>? CommandRunner { get; init; }

    private FfmpegConversionCatalog(string ffmpeg, List<ConversionFormat> formats, List<ConversionEncoder> encoders)
    { _ffmpeg = ffmpeg; _formats = formats; _encoders = encoders; }

    public IReadOnlyList<ConversionFormat> FormatsFor(string kind) => _formats
        .Where(f => f.DefaultCodec(kind) is { } codec && _encoders.Any(e => e.Kind == kind && e.Codec == codec
            && (kind != "subtitle" || e.Name is not ("dvbsub" or "dvdsub" or "xsub"))))
        .OrderBy(f => f.Muxer switch
        {
            "mp4" or "matroska" or "mov" or "webm" or "avi" or "mp3" or "adts" or "wav"
                or "flac" or "ogg" or "opus" or "srt" or "ass" or "webvtt" => 0,
            _ => 1
        }).ThenBy(f => f.Extension, StringComparer.OrdinalIgnoreCase).ToArray();

    public IReadOnlyList<ConversionEncoder> EncodersFor(string kind) => _encoders
        .Where(e => e.Kind == kind && (kind != "subtitle" || e.Name is not ("dvbsub" or "dvdsub" or "xsub")))
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
        if (!_formats.Contains(format) || format.DefaultCodec(kind) is null) return;
        _confirmedAdditional[(format, kind)] = encoders
            .Where(e => e.Kind == kind && !e.IsHardware && _encoders.Contains(e)).Distinct().ToArray();
    }

    public ConversionEncoder? SuggestedEncoder(ConversionFormat format, string kind) =>
        _encoders.Where(e => e.Kind == kind && e.Codec == format.DefaultCodec(kind))
            .OrderBy(e => e.IsHardware).ThenBy(e => e.Name.StartsWith("lib", StringComparison.Ordinal) ? 0 : 1)
            .FirstOrDefault();

    public bool Contains(ConversionSettings settings) => _formats.Contains(settings.Format) && _encoders.Contains(settings.Encoder);

    public async Task<bool> CheckCopyAsync(string source, int streamIndex, ConversionFormat format,
        CancellationToken token)
    {
        if (!_formats.Contains(format) || streamIndex < 0) return false;
        var directory = Path.Combine(Path.GetTempPath(), "trackextractor-copy-" + Guid.NewGuid().ToString("N"));
        var output = Path.Combine(directory, "output." + format.Extension);
        try
        {
            Directory.CreateDirectory(directory);
            var result = await RunAsync(["-hide_banner", "-loglevel", "error", "-nostdin", "-y",
                "-i", source, "-map", $"0:{streamIndex}", "-t", "0.1", "-c", "copy",
                "-f", format.Muxer, output], token).ConfigureAwait(false);
            return result.ExitCode == 0 && File.Exists(output) && new FileInfo(output).Length > 0;
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
        catalog._formats.AddRange(formats.OfType<ConversionFormat>());
        return catalog;
    }

    public async Task<(bool Compatible, string Reason)> CheckCombinationAsync(
        ConversionFormat format, ConversionEncoder encoder, CancellationToken token,
        ConversionSettings? settings = null, Action<string>? diagnostics = null)
    {
        if (!_formats.Contains(format) || !_encoders.Contains(encoder)
            || format.DefaultCodec(encoder.Kind) is null
            || settings is not null && (settings.Format != format || settings.Encoder != encoder))
            return (false, "Этот тип потока не поддерживается выходным форматом.");
        var directory = Path.Combine(Path.GetTempPath(), "trackextractor-probe-" + Guid.NewGuid().ToString("N"));
        var output = Path.Combine(directory, "output." + format.Extension);
        try
        {
            Directory.CreateDirectory(directory);
            string[] input;
            if (encoder.Kind == "video")
                input = encoder.IsHardware
                    ? ["-f", "lavfi", "-i", "testsrc2=size=320x240:rate=30:duration=0.2", "-frames:v", "3"]
                    : ["-f", "lavfi", "-i", "color=c=black:s=64x64:r=1:d=1", "-frames:v", "1"];
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
            if (settings is not null) TrackExporter.AppendConversionOptions(args, settings);
            args.AddRange(["-f", format.Muxer, output]);
            var result = await RunAsync(args.ToArray(), token).ConfigureAwait(false);
            if (result.ExitCode != 0 && diagnostics is not null)
                diagnostics($"{encoder.Name}: код FFmpeg {result.ExitCode}; {result.Output}");
            return result.ExitCode == 0 && File.Exists(output) && new FileInfo(output).Length > 0
                ? (true, "") : (false, $"Кодировщик {encoder.Name} найден в FFmpeg, но не смог создать пробный файл " +
                    $"в формате {format.Extension.ToUpperInvariant()} (код FFmpeg: {result.ExitCode}).");
        }
        catch (TimeoutException)
        {
            diagnostics?.Invoke($"{encoder.Name}: время ожидания FFmpeg истекло.");
            return (false, $"Проверка кодировщика {encoder.Name} превысила время ожидания.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or Win32Exception
            or InvalidDataException)
        {
            diagnostics?.Invoke($"{encoder.Name}: {ex.GetType().Name}: {ex.Message}");
            return (false, $"Не удалось проверить кодировщик {encoder.Name} с форматом {format.Extension.ToUpperInvariant()}.");
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
