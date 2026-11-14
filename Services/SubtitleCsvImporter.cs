using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace MediaUnseal.Services;

internal static class SubtitleCsvImporter
{
    internal sealed record Cue(string In, string Out, string Speaker, string Text, string[]? OriginalFields = null);
    internal sealed record Data(bool Adr, char Delimiter, IReadOnlyList<Cue> Cues);
    private static readonly char[] Delimiters = [',', ';', '\t', '|'];
    private static readonly string[] Header =
        ["Index", "Start", "End", "Duration", "Speaker", "Text", "Translation", "Role", "Notes"];
    private static readonly Regex Milliseconds = new(@"\A(\d{2,}):(\d{2}):(\d{2})\.(\d{3})\z");
    private static readonly Regex Frames = new(@"\A\d{2,}:\d{2}:\d{2}[:;]\d{2}\z");

    internal static async Task<Data?> ReadAsync(string path, CancellationToken token)
    {
        var length = new FileInfo(path).Length;
        if (length is <= 0 or > 256L * 1024 * 1024) return null;
        await using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
            81920, FileOptions.Asynchronous | FileOptions.SequentialScan);
        using var reader = new StreamReader(file, new UTF8Encoding(false, true), false);
        string source;
        try { source = await reader.ReadToEndAsync(token).ConfigureAwait(false); }
        catch (DecoderFallbackException) { return null; }
        if (source.IndexOf('\0') >= 0) return null;
        source = source.TrimStart('\uFEFF');
        Data? match = null;
        foreach (var delimiter in Delimiters)
        {
            token.ThrowIfCancellationRequested();
            if (!TryRows(source, delimiter, token, out var rows) || rows.Count < 1) continue;
            Data? candidate = null;
            if (rows[0].SequenceEqual(Header) && rows.Count > 1
                && rows.Skip(1).All((row) => row.Length == 9))
            {
                var cues = new List<Cue>();
                var valid = true;
                for (var i = 1; i < rows.Count; i++)
                {
                    token.ThrowIfCancellationRequested();
                    var row = rows[i];
                    if (!int.TryParse(row[0], NumberStyles.None, CultureInfo.InvariantCulture, out var index)
                        || index != i || !TryMilliseconds(row[1], out var start)
                        || !TryMilliseconds(row[2], out var end) || end < start)
                    { valid = false; break; }
                    cues.Add(new(row[1], row[2], row[4], row[5], row));
                }
                if (valid) candidate = new(false, delimiter, cues);
            }
            else if (rows.All(row => row.Length == 4 && TryFrameShape(row[0], out var a)
                && TryFrameShape(row[1], out var b) && row[0][^3] == row[1][^3] && b.CompareTo(a) >= 0))
                candidate = new(true, delimiter, rows.Select(row => new Cue(row[0], row[1], row[2], row[3])).ToArray());
            if (candidate is null) continue;
            if (match is not null) return null;
            match = candidate;
        }
        return match;
    }

    private static bool TryRows(string source, char delimiter, CancellationToken token, out List<string[]> rows)
    {
        rows = [];
        var row = new List<string>();
        var field = new StringBuilder();
        var quoted = false;
        var closed = false;
        var lineStart = true;
        for (var i = 0; i < source.Length; i++)
        {
            if ((i & 4095) == 0) token.ThrowIfCancellationRequested();
            var c = source[i];
            if (quoted)
            {
                if (c == '"')
                {
                    if (i + 1 < source.Length && source[i + 1] == '"') { field.Append('"'); i++; }
                    else { quoted = false; closed = true; }
                }
                else field.Append(c);
                continue;
            }
            if (c == '"')
            {
                if (field.Length != 0 || closed) return false;
                quoted = true;
            }
            else if (c == delimiter)
            { row.Add(field.ToString()); field.Clear(); closed = false; lineStart = false; }
            else if (c is '\r' or '\n')
            {
                if (c == '\r' && i + 1 < source.Length && source[i + 1] == '\n') i++;
                if (lineStart && row.Count == 0 && field.Length == 0) return false;
                row.Add(field.ToString()); field.Clear(); rows.Add(row.ToArray()); row.Clear();
                closed = false; lineStart = true;
            }
            else
            {
                if (closed || c == '"') return false;
                field.Append(c); lineStart = false;
            }
        }
        if (quoted) return false;
        if (!lineStart || row.Count != 0 || field.Length != 0 || closed)
        { row.Add(field.ToString()); rows.Add(row.ToArray()); }
        return true;
    }

    private static bool TryMilliseconds(string value, out TimeSpan time)
    {
        time = default;
        var m = Milliseconds.Match(value);
        if (!m.Success || !long.TryParse(m.Groups[1].Value, NumberStyles.None,
                CultureInfo.InvariantCulture, out var h)) return false;
        var minute = int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture);
        var second = int.Parse(m.Groups[3].Value, CultureInfo.InvariantCulture);
        var millis = int.Parse(m.Groups[4].Value, CultureInfo.InvariantCulture);
        if (minute > 59 || second > 59) return false;
        try { time = TimeSpan.FromTicks(checked(h * TimeSpan.TicksPerHour
            + minute * TimeSpan.TicksPerMinute + second * TimeSpan.TicksPerSecond
            + millis * TimeSpan.TicksPerMillisecond)); return true; }
        catch (OverflowException) { return false; }
    }

    private static bool TryFrame(string value, string fps, out long frame)
    {
        frame = 0;
        var rate = SubtitleCsvExporter.FrameRate(fps);
        if (!SubtitleCsvExporter.IsValidAdrTiming(fps, value)
            || value[^3] != (rate.Drop > 0 ? ';' : ':')) return false;
        var first = value.IndexOf(':');
        if (!long.TryParse(value.AsSpan(0, first), NumberStyles.None,
            CultureInfo.InvariantCulture, out var hour)) return false;
        var minute = int.Parse(value.AsSpan(first + 1, 2), CultureInfo.InvariantCulture);
        var second = int.Parse(value.AsSpan(first + 4, 2), CultureInfo.InvariantCulture);
        var number = int.Parse(value.AsSpan(value.Length - 2), CultureInfo.InvariantCulture);
        try
        {
            var totalMinutes = checked(hour * 60 + minute);
            frame = checked((hour * 3600 + minute * 60 + second) * rate.Nominal + number
                - rate.Drop * (totalMinutes - totalMinutes / 10));
            return true;
        }
        catch (OverflowException) { return false; }
    }

    private static bool TryFrameShape(string value, out (long Hour, int Minute, int Second, int Frame) parts)
    {
        parts = default;
        if (!Frames.IsMatch(value) || !long.TryParse(value.AsSpan(0, value.IndexOf(':')),
            NumberStyles.None, CultureInfo.InvariantCulture, out var hour)) return false;
        var first = value.IndexOf(':');
        var minute = int.Parse(value.AsSpan(first + 1, 2), CultureInfo.InvariantCulture);
        var second = int.Parse(value.AsSpan(first + 4, 2), CultureInfo.InvariantCulture);
        var frame = int.Parse(value.AsSpan(value.Length - 2), CultureInfo.InvariantCulture);
        parts = (hour, minute, second, frame);
        return minute < 60 && second < 60 && frame < 60;
    }

    internal static async Task<int> ConvertAsync(string input, string output, string format,
        string? fps, string startTimecode, CancellationToken token, string? expectedCodec = null,
        char outputDelimiter = ',', string outputDestination = "universal",
        string outputFps = "24", string outputStartTimecode = "00:00:00:00")
    {
        var data = await ReadAsync(input, token).ConfigureAwait(false)
            ?? throw new InvalidDataException("The source is not a supported subtitle CSV file.");
        if (expectedCodec is not null && data.Adr != (expectedCodec == "csv_adr"))
            throw new InvalidDataException("Subtitle CSV structure changed after analysis.");
        if (format is not ("srt" or "webvtt" or "ass" or "ssa" or "csv")
            || format == "csv" && (outputDelimiter is not (',' or ';' or '\t' or '|')
                || outputDestination is not ("universal" or "adr")
                || outputDestination == "adr" && !SubtitleCsvExporter.IsValidAdrTiming(outputFps, outputStartTimecode)))
            throw new InvalidDataException("Unsupported CSV subtitle output format.");
        var rate = fps is null ? default : SubtitleCsvExporter.FrameRate(fps);
        long offset = 0;
        var normalizedStart = startTimecode.Length >= 3 && rate.Drop > 0
            ? startTimecode[..^3] + ";" + startTimecode[^2..] : startTimecode;
        if (data.Adr && (rate.Nominal == 0 || !TryFrame(normalizedStart, fps!, out offset)))
            throw new InvalidDataException("Choose a valid ADR frame rate and timeline start timecode.");
        await using var file = new FileStream(output, FileMode.CreateNew, FileAccess.Write,
            FileShare.None, 81920, FileOptions.Asynchronous);
        await using var writer = new StreamWriter(file, new UTF8Encoding(true));
        if (format == "csv" && outputDestination == "universal")
            await writer.WriteAsync((string.Join(outputDelimiter, Header) + "\r\n").AsMemory(), token).ConfigureAwait(false);
        if (format == "webvtt") await writer.WriteAsync("WEBVTT\r\n\r\n".AsMemory(), token).ConfigureAwait(false);
        if (format is "ass" or "ssa")
        {
            var styles = format == "ass"
                ? "[V4+ Styles]\r\nFormat: Name, Fontname, Fontsize, PrimaryColour, SecondaryColour, OutlineColour, BackColour, Bold, Italic, Underline, StrikeOut, ScaleX, ScaleY, Spacing, Angle, BorderStyle, Outline, Shadow, Alignment, MarginL, MarginR, MarginV, Encoding\r\nStyle: Default,Arial,20,&H00FFFFFF,&H000000FF,&H00000000,&H00000000,0,0,0,0,100,100,0,0,1,2,0,2,10,10,10,1\r\n"
                : "[V4 Styles]\r\nFormat: Name, Fontname, Fontsize, PrimaryColour, SecondaryColour, TertiaryColour, BackColour, Bold, Italic, BorderStyle, Outline, Shadow, Alignment, MarginL, MarginR, MarginV, AlphaLevel, Encoding\r\nStyle: Default,Arial,20,16777215,0,0,0,0,0,1,2,0,2,10,10,10,0,1\r\n";
            await writer.WriteAsync(("[Script Info]\r\nScriptType: " + (format == "ass" ? "v4.00+" : "v4.00") + "\r\n" + styles
                + "[Events]\r\nFormat: " + (format == "ass" ? "Layer" : "Marked")
                + ", Start, End, Style, Name, MarginL, MarginR, MarginV, Effect, Text\r\n")
                .AsMemory(), token).ConfigureAwait(false);
        }
        for (var i = 0; i < data.Cues.Count; i++)
        {
            token.ThrowIfCancellationRequested();
            var cue = data.Cues[i];
            TimeSpan begin, end;
            if (data.Adr)
            {
                if (!TryFrame(cue.In, fps!, out var a) || !TryFrame(cue.Out, fps!, out var b)
                    || a < offset || b < a) throw new InvalidDataException("Invalid ADR cue timecode.");
                var numerator = (decimal)TimeSpan.TicksPerSecond * rate.Denominator;
                try
                {
                    begin = TimeSpan.FromTicks(checked((long)Math.Round((a - offset) * numerator / rate.Numerator
                        / TimeSpan.TicksPerMillisecond, MidpointRounding.AwayFromZero) * TimeSpan.TicksPerMillisecond));
                    end = TimeSpan.FromTicks(checked((long)Math.Round((b - offset) * numerator / rate.Numerator
                        / TimeSpan.TicksPerMillisecond, MidpointRounding.AwayFromZero) * TimeSpan.TicksPerMillisecond));
                }
                catch (OverflowException ex) { throw new InvalidDataException("ADR timecode is out of range.", ex); }
            }
            else if (!TryMilliseconds(cue.In, out begin) || !TryMilliseconds(cue.Out, out end) || end < begin)
                throw new InvalidDataException("Invalid CSV subtitle timing.");
            var clock = format == "srt" ? ',' : '.';
            string Time(TimeSpan time) => $"{time.Ticks / TimeSpan.TicksPerHour:00}:{time.Minutes:00}:{time.Seconds:00}{clock}{time.Milliseconds:000}";
            var text = cue.Text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
            string entry;
            if (format == "csv")
            {
                string[] fields = outputDestination == "adr"
                    ? [SubtitleCsvExporter.AdrTimecode(begin, outputFps, outputStartTimecode),
                        SubtitleCsvExporter.AdrTimecode(end, outputFps, outputStartTimecode),
                        SubtitleCsvExporter.SingleLine(cue.Speaker), SubtitleCsvExporter.SingleLine(cue.Text)]
                    : cue.OriginalFields is { } original ? original
                    : [(i + 1).ToString(CultureInfo.InvariantCulture), SubtitleCsvExporter.FormatTime(begin),
                        SubtitleCsvExporter.FormatTime(end), SubtitleCsvExporter.FormatTime(end - begin),
                        cue.Speaker, cue.Text, "", "", ""];
                entry = string.Join(outputDelimiter,
                    fields.Select(field => SubtitleCsvExporter.Escape(field, outputDelimiter))) + "\r\n";
            }
            else if (format == "srt") entry = $"{i + 1}\r\n{Time(begin)} --> {Time(end)}\r\n{text.Replace("\n", "\r\n", StringComparison.Ordinal)}\r\n\r\n";
            else if (format == "webvtt") entry = $"{Time(begin)} --> {Time(end)}\r\n{text.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\n", "\r\n", StringComparison.Ordinal)}\r\n\r\n";
            else
            {
                if (cue.Speaker.IndexOfAny([',', '\r', '\n']) >= 0)
                    throw new InvalidDataException("The speaker name cannot be represented in ASS/SSA.");
                string AssTime(TimeSpan time)
                {
                    var centiseconds = (time.Ticks + 50000) / 100000;
                    return $"{centiseconds / 360000:0}:{centiseconds / 6000 % 60:00}:{centiseconds / 100 % 60:00}.{centiseconds % 100:00}";
                }
                entry = $"Dialogue: {(format == "ass" ? "0" : "Marked=0")},{AssTime(begin)},{AssTime(end)},Default,{cue.Speaker},0,0,0,,{text.Replace("\n", "\\N", StringComparison.Ordinal)}\r\n";
            }
            await writer.WriteAsync(entry.AsMemory(), token).ConfigureAwait(false);
        }
        await writer.FlushAsync(token).ConfigureAwait(false);
        return data.Cues.Count;
    }
}
