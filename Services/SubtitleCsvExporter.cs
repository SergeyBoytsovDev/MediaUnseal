using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using MediaUnseal.Models;

namespace MediaUnseal.Services;

internal static class SubtitleCsvExporter
{
    internal sealed record Cue(TimeSpan Start, TimeSpan End, string Speaker, string Text, string Role);

    internal static string IntermediateFormat(MediaStreamInfo stream) => stream.Codec?.ToLowerInvariant() switch
    {
        "ass" or "ssa" => "ass",
        "webvtt" => "webvtt",
        _ => "srt"
    };

    internal static bool CanReadDirectly(MediaInfo media, string format) => media.Streams.Count == 1
        && (format, Path.GetExtension(media.FullPath).ToLowerInvariant()) switch
        {
            ("srt", ".srt") => media.Container == "srt",
            ("ass", ".ass" or ".ssa") => media.Container == "ass",
            ("webvtt", ".vtt") => media.Container == "webvtt",
            _ => false
        };

    internal static bool HasNegativeStreamStart(MediaStreamInfo stream) =>
        stream.ExtraProperties?.TryGetValue("start_time", out var startTime) == true
            && double.TryParse(startTime, NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds)
            && seconds < 0
        || stream.ExtraProperties?.TryGetValue("start_pts", out var startPts) == true
            && long.TryParse(startPts, NumberStyles.Integer, CultureInfo.InvariantCulture, out var pts)
            && pts < 0;

    internal static string[] BuildArguments(string input, int index, string output, string format, string? sourceCodec) =>
        ["-hide_banner", "-loglevel", "error", "-nostdin", "-stats_period", "0.5",
            "-progress", "pipe:2", "-i", input, "-map", $"0:{index}", "-c:s",
            sourceCodec?.ToLowerInvariant() switch
            {
                "ass" or "ssa" or "webvtt" or "subrip" or "srt" => "copy",
                _ => "srt"
            },
            "-f", format, output];

    internal static async Task<int> ConvertAsync(string input, string format, string output, CancellationToken token,
        char delimiter = ',', string csvDestination = "universal", string adrFps = "24",
        string adrStartTimecode = "00:00:00:00")
    {
        if (delimiter is not (',' or ';' or '\t' or '|')) throw new ArgumentOutOfRangeException(nameof(delimiter));
        if (csvDestination is not ("universal" or "adr")
            || csvDestination == "adr" && !IsValidAdrTiming(adrFps, adrStartTimecode))
            throw new ArgumentException("Invalid CSV destination or ADR timing.");
        if (new FileInfo(input).Length > 256L * 1024 * 1024)
            throw new InvalidDataException("Subtitle text is too large for CSV export.");
        string source;
        try
        {
            await using var file = new FileStream(input, FileMode.Open, FileAccess.Read, FileShare.Read,
                81920, FileOptions.Asynchronous | FileOptions.SequentialScan);
            using var reader = new StreamReader(file, new UTF8Encoding(false, true), false);
            source = await reader.ReadToEndAsync(token).ConfigureAwait(false);
        }
        catch (DecoderFallbackException ex)
        { throw new InvalidDataException("Subtitle data is not valid UTF-8 text.", ex); }
        if (source.IndexOf('\0') >= 0)
            throw new InvalidDataException("Subtitle data is not valid text.");
        source = source.TrimStart('\uFEFF');
        var cues = format switch
        {
            "ass" => ParseAss(source, token),
            "webvtt" => ParseTimedText(source, true, token),
            "srt" => ParseTimedText(source, false, token),
            _ => throw new InvalidDataException("Unsupported subtitle text format.")
        };
        if (cues.Count == 0)
            throw new InvalidDataException("No readable subtitle cues were found for CSV export.");
        await using var destination = new FileStream(output, FileMode.CreateNew, FileAccess.Write,
            FileShare.None, 81920, FileOptions.Asynchronous);
        await using var writer = new StreamWriter(destination, new UTF8Encoding(true));
        if (csvDestination == "universal")
            await writer.WriteAsync((string.Join(delimiter,
                ["Index", "Start", "End", "Duration", "Speaker", "Text", "Translation", "Role", "Notes"])
                + "\r\n").AsMemory(), token).ConfigureAwait(false);
        for (var i = 0; i < cues.Count; i++)
        {
            token.ThrowIfCancellationRequested();
            var cue = cues[i];
            if (cue.Start < TimeSpan.Zero || cue.End < cue.Start)
                throw new InvalidDataException("Subtitle cue has invalid or negative timing.");
            var fields = csvDestination == "adr"
                ? new[] { AdrTimecode(cue.Start, adrFps, adrStartTimecode),
                    AdrTimecode(cue.End, adrFps, adrStartTimecode), SingleLine(cue.Speaker), SingleLine(cue.Text) }
                : new[] { (i + 1).ToString(CultureInfo.InvariantCulture), FormatTime(cue.Start),
                    FormatTime(cue.End), FormatTime(cue.End - cue.Start), cue.Speaker, cue.Text,
                    "", cue.Role, "" };
            await writer.WriteAsync((string.Join(delimiter, fields.Select(field => Escape(field, delimiter))) + "\r\n").AsMemory(), token)
                .ConfigureAwait(false);
        }
        await writer.FlushAsync(token).ConfigureAwait(false);
        return cues.Count;
    }

    internal static (int Numerator, int Denominator, int Nominal, int Drop) FrameRate(string fps) => fps switch
    {
        "23.976" => (24000, 1001, 24, 0), "24" => (24, 1, 24, 0),
        "25" => (25, 1, 25, 0), "29.97 DF" => (30000, 1001, 30, 2),
        "29.97 NDF" => (30000, 1001, 30, 0),
        "30" => (30, 1, 30, 0), "50" => (50, 1, 50, 0),
        "59.94 DF" => (60000, 1001, 60, 4),
        "59.94 NDF" => (60000, 1001, 60, 0), "60" => (60, 1, 60, 0),
        _ => (0, 1, 0, 0)
    };

    internal static bool IsValidAdrTiming(string fps, string start)
    {
        var rate = FrameRate(fps);
        var match = Regex.Match(start ?? "", @"\A(\d{2,}):(\d{2}):(\d{2})[:;](\d{2})\z");
        if (rate.Nominal == 0 || !match.Success
            || !long.TryParse(match.Groups[1].Value, out _)) return false;
        var minute = int.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture);
        var second = int.Parse(match.Groups[3].Value, CultureInfo.InvariantCulture);
        var frame = int.Parse(match.Groups[4].Value, CultureInfo.InvariantCulture);
        return minute < 60 && second < 60 && frame < rate.Nominal
            && !(rate.Drop > 0 && minute % 10 != 0 && second == 0 && frame < rate.Drop);
    }

    internal static string AdrTimecode(TimeSpan time, string fps, string start)
    {
        var rate = FrameRate(fps);
        var h = long.Parse(start[..start.IndexOf(':')], CultureInfo.InvariantCulture);
        var m = int.Parse(start.AsSpan(start.IndexOf(':') + 1, 2), CultureInfo.InvariantCulture);
        var s = int.Parse(start.AsSpan(start.IndexOf(':') + 4, 2), CultureInfo.InvariantCulture);
        var f = int.Parse(start.AsSpan(start.Length - 2), CultureInfo.InvariantCulture);
        var elapsed = (long)Math.Round((decimal)time.Ticks * rate.Numerator
            / (TimeSpan.TicksPerSecond * rate.Denominator), MidpointRounding.AwayFromZero);
        var minutes = h * 60 + m;
        var offset = checked(((h * 3600 + m * 60 + s) * rate.Nominal + f)
            - rate.Drop * (minutes - minutes / 10));
        var frames = checked(elapsed + offset);
        if (rate.Drop > 0)
        {
            var tenMinutes = rate.Nominal * 600 - rate.Drop * 9;
            var remainder = frames % tenMinutes;
            frames += rate.Drop * 9 * (frames / tenMinutes)
                + rate.Drop * Math.Max(0, (remainder - rate.Drop) /
                    (rate.Nominal * 60 - rate.Drop));
        }
        var frameNumber = frames % rate.Nominal;
        var totalSeconds = frames / rate.Nominal;
        return FormattableString.Invariant(
            $"{totalSeconds / 3600:00}:{totalSeconds / 60 % 60:00}:{totalSeconds % 60:00}{(rate.Drop > 0 ? ';' : ':')}{frameNumber:00}");
    }

    private static List<Cue> ParseTimedText(string source, bool webvtt, CancellationToken token)
    {
        var cues = new List<Cue>();
        var lines = source.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Split('\n');
        for (var i = 0; i < lines.Length;)
        {
            token.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(lines[i])) { i++; continue; }
            if (webvtt && (lines[i].StartsWith("WEBVTT", StringComparison.Ordinal)
                || lines[i] is "STYLE" or "REGION" || lines[i].StartsWith("NOTE", StringComparison.Ordinal)))
            {
                while (i < lines.Length && !string.IsNullOrWhiteSpace(lines[i])) i++;
                continue;
            }
            if (i + 1 < lines.Length && !lines[i].Contains("-->", StringComparison.Ordinal)
                && lines[i + 1].Contains("-->", StringComparison.Ordinal)) i++;
            if (!lines[i].Contains("-->", StringComparison.Ordinal))
                throw new InvalidDataException("Invalid subtitle cue timing.");
            var timing = Regex.Match(lines[i++], @"\A\s*(\S+)\s+-->\s+(\S+)");
            if (!timing.Success || !TryTime(timing.Groups[1].Value, out var start)
                || !TryTime(timing.Groups[2].Value, out var end))
                throw new InvalidDataException("Invalid subtitle cue timing.");
            var text = new List<string>();
            while (i < lines.Length && !string.IsNullOrWhiteSpace(lines[i])) text.Add(lines[i++]);
            var raw = string.Join("\n", text);
            var voice = webvtt ? Regex.Match(raw, @"<v(?:\s+([^>]+))?>", RegexOptions.IgnoreCase) : Match.Empty;
            var speaker = voice.Success ? NormalizeSpeaker(WebUtility.HtmlDecode(voice.Groups[1].Value)) : "";
            var clean = CleanTags(raw);
            if (end < start || start < TimeSpan.Zero)
                throw new InvalidDataException("Subtitle cue has invalid or negative timing.");
            cues.Add(new(start, end, speaker, clean, ""));
        }
        return cues;
    }

    private static List<Cue> ParseAss(string source, CancellationToken token)
    {
        var cues = new List<Cue>();
        var inEvents = false;
        string[] fields = ["Layer", "Start", "End", "Style", "Name", "MarginL", "MarginR", "MarginV", "Effect", "Text"];
        foreach (var line in source.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Split('\n'))
        {
            token.ThrowIfCancellationRequested();
            var trimmed = line.Trim();
            if (trimmed.StartsWith('[')) { inEvents = trimmed.Equals("[Events]", StringComparison.OrdinalIgnoreCase); continue; }
            if (!inEvents) continue;
            if (trimmed.StartsWith("Format:", StringComparison.OrdinalIgnoreCase))
            { fields = trimmed[7..].Split(',').Select(s => s.Trim()).ToArray(); continue; }
            if (!trimmed.StartsWith("Dialogue:", StringComparison.OrdinalIgnoreCase)) continue;
            var values = trimmed[9..].Split(',', fields.Length);
            if (fields.Length < 3 || values.Length != fields.Length)
                throw new InvalidDataException("Invalid ASS dialogue fields.");
            string Value(string name)
            {
                var index = Array.FindIndex(fields, field => field.Equals(name, StringComparison.OrdinalIgnoreCase));
                return index < 0 ? "" : name == "Text" ? values[index] : values[index].Trim();
            }
            if (!TryTime(Value("Start"), out var start) || !TryTime(Value("End"), out var end)
                || end < start || start < TimeSpan.Zero)
                throw new InvalidDataException("Invalid ASS dialogue timing.");
            var text = Regex.Replace(Value("Text"), @"\{\\[^}]*\}", "")
                .Replace("\\N", "\n", StringComparison.Ordinal).Replace("\\n", "\n", StringComparison.Ordinal)
                .Replace("\\h", " ", StringComparison.Ordinal);
            cues.Add(new(start, end, NormalizeSpeaker(Value("Name")), CleanTags(text), ""));
        }
        return cues;
    }

    private static string CleanTags(string text) => WebUtility.HtmlDecode(
        Regex.Replace(Regex.Replace(text,
            @"</?(?:i|b|u|s|font|c|v|lang|ruby|rt|span)(?:\.[^>\s]+)?(?:\s+[^>]*)?>",
            "", RegexOptions.IgnoreCase), @"<\d{2}:\d{2}(?::\d{2})?\.\d{3}>", ""));

    private static string NormalizeSpeaker(string value)
    {
        value = value.Trim();
        return value.Equals("None", StringComparison.OrdinalIgnoreCase)
            || value.Equals("N/A", StringComparison.OrdinalIgnoreCase)
            || value.Equals("Unknown", StringComparison.OrdinalIgnoreCase) ? "" : value;
    }

    private static bool TryTime(string value, out TimeSpan time)
    {
        time = default;
        if (value.StartsWith('-')) return false;
        var parts = value.Replace(',', '.').Split(':');
        if (parts.Length is not (2 or 3)) return false;
        var seconds = parts[^1].Split('.');
        if (seconds.Length != 2 || seconds[1].Length is < 1 or > 3
            || !long.TryParse(parts.Length == 3 ? parts[0] : "0", NumberStyles.None,
                CultureInfo.InvariantCulture, out var hours)
            || !int.TryParse(parts[^2], NumberStyles.None, CultureInfo.InvariantCulture, out var minutes)
            || !int.TryParse(seconds[0], NumberStyles.None, CultureInfo.InvariantCulture, out var wholeSeconds)
            || !int.TryParse(seconds[1].PadRight(3, '0'), NumberStyles.None,
                CultureInfo.InvariantCulture, out var millis)
            || minutes > 59 || wholeSeconds > 59) return false;
        try { time = new TimeSpan(checked(hours * TimeSpan.TicksPerHour
            + minutes * TimeSpan.TicksPerMinute + wholeSeconds * TimeSpan.TicksPerSecond
            + millis * TimeSpan.TicksPerMillisecond)); return true; }
        catch (OverflowException) { return false; }
    }

    internal static string FormatTime(TimeSpan time)
    {
        var hours = time.Ticks / TimeSpan.TicksPerHour;
        return string.Create(CultureInfo.InvariantCulture,
            $"{hours:00}:{time.Minutes:00}:{time.Seconds:00}.{time.Milliseconds:000}");
    }

    internal static string SingleLine(string value) => value.Replace("\r\n", " ", StringComparison.Ordinal)
        .Replace('\r', ' ').Replace('\n', ' ');

    internal static string Escape(string value, char delimiter)
    {
        value = value.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n')
            .Replace("\n", "\r\n", StringComparison.Ordinal);
        return value.IndexOfAny([delimiter, '"', '\r', '\n']) < 0 ? value : "\"" + value.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
    }
}
