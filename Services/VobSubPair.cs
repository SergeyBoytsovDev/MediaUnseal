using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using MediaUnseal.Models;

namespace MediaUnseal.Services;

internal sealed class VobSubPairException(string message) : IOException(message);

// FFmpeg can copy DVD subtitle packets into a DVD program stream, but has no IDX muxer.
// Build the index from the source packet times and the copied packet positions, then
// verify every packet (including its data hash) through FFprobe before publishing either file.
internal static class VobSubPair
{
    private sealed record Packet(decimal Time, int Size, long Position, string Hash);
    private const int MaxPackets = 2_000_000;
    private const int MaxProbeOutput = 128 * 1024 * 1024;

    internal static bool IsIndex(string path)
    {
        using var file = File.OpenRead(path);
        Span<byte> header = stackalloc byte[64];
        var count = file.Read(header);
        return Encoding.UTF8.GetString(header[..count]).TrimStart('\uFEFF')
            .StartsWith("# VobSub index file,", StringComparison.Ordinal);
    }

    internal static bool IsSub(string path)
    {
        using var file = File.OpenRead(path);
        Span<byte> header = stackalloc byte[4];
        return file.Read(header) == 4 && header.SequenceEqual(new byte[] { 0, 0, 1, 0xBA });
    }

    internal static bool IsMicroDvd(string path)
    {
        using var file = File.OpenRead(path);
        var header = new byte[512];
        var count = file.Read(header);
        var text = Encoding.UTF8.GetString(header, 0, count).TrimStart('\uFEFF');
        return Regex.IsMatch(text, @"\A\s*\{\d+\}\{\d+\}", RegexOptions.CultureInvariant);
    }

    internal static string ResolveSource(string path)
    {
        var extension = Path.GetExtension(path);
        if (extension.Equals(".idx", StringComparison.OrdinalIgnoreCase))
        {
            if (!IsIndex(path)) throw new VobSubPairException("The IDX file is not a VobSub index.");
            var sub = FindPair(path, ".sub");
            if (sub is null || !IsSub(sub))
                throw new VobSubPairException("The matching VobSub .sub file is missing or damaged.");
            ValidateIndex(path, sub);
            return path;
        }
        if (!extension.Equals(".sub", StringComparison.OrdinalIgnoreCase)) return path;
        var idx = FindPair(path, ".idx");
        if (idx is null && !IsSub(path)) return path; // Textual MicroDVD .sub.
        if (idx is null || !IsIndex(idx) || !IsSub(path))
            throw new VobSubPairException("This is a binary VobSub .sub file. A matching .idx file is required.");
        ValidateIndex(idx, path);
        return idx;
    }

    private static void ValidateIndex(string idx, string sub)
    {
        if (new FileInfo(idx).Length > 128L * 1024 * 1024)
            throw new VobSubPairException("The VobSub index exceeds the safety limit.");
        using var data = File.OpenRead(sub);
        var count = 0;
        Span<byte> pack = stackalloc byte[4];
        foreach (var line in File.ReadLines(idx))
        {
            if (!line.TrimStart().StartsWith("timestamp:", StringComparison.OrdinalIgnoreCase)) continue;
            if (++count > MaxPackets)
                throw new VobSubPairException("The VobSub index has too many entries.");
            var match = Regex.Match(line, @"\bfilepos:\s*([0-9a-fA-F]+)\b",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            if (!match.Success || !long.TryParse(match.Groups[1].Value, NumberStyles.HexNumber,
                    CultureInfo.InvariantCulture, out var offset)
                || offset < 0 || offset > data.Length - pack.Length)
                throw new VobSubPairException("The VobSub index contains an invalid file position.");
            data.Position = offset;
            if (data.Read(pack) != pack.Length || !pack.SequenceEqual(new byte[] { 0, 0, 1, 0xBA }))
                throw new VobSubPairException("The VobSub index points to damaged subtitle data.");
        }
        if (count == 0) throw new VobSubPairException("The VobSub index contains no subtitle positions.");
    }

    private static string? FindPair(string path, string extension)
    {
        var exact = Path.ChangeExtension(path, extension);
        if (File.Exists(exact)) return exact;
        var directory = Path.GetDirectoryName(path)!;
        var stem = Path.GetFileNameWithoutExtension(path);
        var matches = Directory.EnumerateFiles(directory).Where(candidate =>
            Path.GetFileNameWithoutExtension(candidate) == stem
            && Path.GetExtension(candidate).Equals(extension, StringComparison.OrdinalIgnoreCase))
            .Take(2).ToArray();
        if (matches.Length > 1) throw new VobSubPairException("Multiple matching VobSub pair files were found.");
        return matches.Length == 1 ? matches[0] : null;
    }

    internal static async Task ExportAsync(MediaInfo media, MediaStreamInfo stream, string subPath,
        string ffmpegPath, string ffprobePath, CancellationToken token,
        IProgress<TimeSpan>? progress, Action<string> diagnostic)
    {
        if (stream.Index is not >= 0 || stream.Codec != "dvd_subtitle")
            throw new InvalidDataException("The selected stream is not a VobSub subtitle.");
        var idxPath = Path.ChangeExtension(subPath, ".idx");
        var sourcePackets = await ReadPacketsAsync(ffprobePath, media.FullPath,
            stream.Index.Value.ToString(CultureInfo.InvariantCulture),
            token).ConfigureAwait(false);
        if (sourcePackets.Count == 0) throw new InvalidDataException("The VobSub track has no subtitle packets.");
        var privateData = await ReadPrivateDataAsync(ffprobePath, media.FullPath, stream.Index.Value,
            token).ConfigureAwait(false);
        if (media.DvdIfo is { PrivateData: { } ifoData })
        {
            var current = DvdIfo.Resolve(media);
            if (current?.PrivateData != ifoData)
                throw new InvalidDataException("The matching DVD IFO changed or is no longer valid.");
            privateData = ifoData;
        }
        if (privateData is null || !privateData.Contains("size:", StringComparison.OrdinalIgnoreCase)
            || !privateData.Contains("palette:", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The VobSub size and palette metadata are unavailable.");
        var args = new[] { "-hide_banner", "-nostdin", "-loglevel", "error", "-progress", "pipe:2",
            "-i", media.FullPath, "-map", $"0:{stream.Index.Value}", "-c:s", "copy",
            "-f", "dvd", "-y", subPath };
        var stderr = new ProcessErrorCapture();
        var exit = await TrackExporter.RunFfmpegAsync(ffmpegPath, args, progress, stderr.Add, token)
            .ConfigureAwait(false);
        if (exit != 0) throw new InvalidDataException($"FFmpeg VobSub packet copy failed (exit {exit}): {stderr.Text}");
        if (!File.Exists(subPath) || new FileInfo(subPath).Length == 0)
            throw new InvalidDataException("FFmpeg did not create the VobSub .sub file.");
        var copied = await ReadPacketsAsync(ffprobePath, subPath, "s:0", token).ConfigureAwait(false);
        if (copied.Count != sourcePackets.Count)
            throw new InvalidDataException("VobSub packet count changed during extraction.");
        var subSize = new FileInfo(subPath).Length;
        using var packStream = File.OpenRead(subPath);
        for (var i = 0; i < copied.Count; i++)
        {
            if (sourcePackets[i].Size != copied[i].Size || sourcePackets[i].Hash != copied[i].Hash)
                throw new InvalidDataException($"VobSub packet #{i + 1} changed during extraction.");
            var position = copied[i].Position & ~2047L;
            if (position < 0 || position >= subSize || !HasPackHeader(packStream, position))
                throw new InvalidDataException($"VobSub packet #{i + 1} has an invalid output position.");
        }
        var language = Regex.IsMatch(stream.Language ?? "", @"\A[a-zA-Z]{2,3}\z")
            ? stream.Language!.ToLowerInvariant() : "und";
        var index = new StringBuilder("# VobSub index file, v7 (do not modify this line!)\n\n");
        foreach (var line in privateData.Split('\n'))
        {
            var value = line.TrimEnd('\r');
            if (value.StartsWith("size:", StringComparison.OrdinalIgnoreCase)
                || value.StartsWith("palette:", StringComparison.OrdinalIgnoreCase)
                || value.StartsWith("org:", StringComparison.OrdinalIgnoreCase))
                index.AppendLine(value);
        }
        index.AppendLine("langidx: 0").Append("id: ").Append(language).AppendLine(", index: 0");
        for (var i = 0; i < sourcePackets.Count; i++)
        {
            var milliseconds = decimal.Round(sourcePackets[i].Time * 1000, 0,
                MidpointRounding.AwayFromZero);
            if (milliseconds < 0 || milliseconds > long.MaxValue)
                throw new InvalidDataException("The VobSub timestamp cannot be represented in IDX.");
            var time = TimeSpan.FromMilliseconds((double)milliseconds);
            index.Append("timestamp: ").Append((long)time.TotalHours).Append(':')
                .Append(time.Minutes.ToString("00", CultureInfo.InvariantCulture)).Append(':')
                .Append(time.Seconds.ToString("00", CultureInfo.InvariantCulture)).Append(':')
                .Append(time.Milliseconds.ToString("000", CultureInfo.InvariantCulture))
                .Append(", filepos: ").Append((copied[i].Position & ~2047L).ToString("x", CultureInfo.InvariantCulture))
                .AppendLine();
        }
        await File.WriteAllTextAsync(idxPath, index.ToString(), new UTF8Encoding(false), token)
            .ConfigureAwait(false);
        var reopened = await ReadPacketsAsync(ffprobePath, idxPath, "s:0", token).ConfigureAwait(false);
        if (reopened.Count != sourcePackets.Count)
            throw new InvalidDataException("The extracted VobSub pair could not be read completely.");
        for (var i = 0; i < reopened.Count; i++)
            if (reopened[i].Hash != sourcePackets[i].Hash ||
                Math.Abs(reopened[i].Time - sourcePackets[i].Time) > 0.001m)
                throw new InvalidDataException($"VobSub packet #{i + 1} or its timestamp changed after reopening.");
        diagnostic($"VobSub pair verified: {reopened.Count} unchanged packets; language {language}; " +
            $"SUB {subSize} bytes, IDX {new FileInfo(idxPath).Length} bytes.");
    }

    private static bool HasPackHeader(Stream file, long position)
    {
        file.Position = position;
        Span<byte> header = stackalloc byte[4];
        return file.Read(header) == 4 && header.SequenceEqual(new byte[] { 0, 0, 1, 0xBA });
    }

    private static async Task<List<Packet>> ReadPacketsAsync(string ffprobe, string path, string stream,
        CancellationToken token)
    {
        var json = await ProbeAsync(ffprobe, ["-v", "error", "-select_streams", stream,
            "-show_packets", "-show_entries", "packet=pts_time,size,pos,data_hash", "-show_data_hash", "sha256",
            "-of", "json", path], token).ConfigureAwait(false);
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("packets", out var items) || items.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("FFprobe did not return VobSub packets.");
        var packets = new List<Packet>();
        foreach (var item in items.EnumerateArray())
        {
            if (packets.Count >= MaxPackets) throw new InvalidDataException("VobSub packet count exceeds the safety limit.");
            if (!decimal.TryParse(item.GetProperty("pts_time").GetString(), NumberStyles.Float,
                    CultureInfo.InvariantCulture, out var time)
                || !int.TryParse(item.GetProperty("size").GetString(), out var size) || size <= 0
                || !long.TryParse(item.GetProperty("pos").GetString(), out var pos) || pos < 0
                || !item.GetProperty("data_hash").GetString()!.StartsWith("SHA256:", StringComparison.Ordinal))
                throw new InvalidDataException("FFprobe returned an incomplete VobSub packet.");
            packets.Add(new(time, size, pos, item.GetProperty("data_hash").GetString()!));
        }
        return packets;
    }

    private static async Task<string?> ReadPrivateDataAsync(string ffprobe, string path, int stream,
        CancellationToken token)
    {
        var json = await ProbeAsync(ffprobe, ["-v", "error", "-select_streams", stream.ToString(CultureInfo.InvariantCulture),
            "-show_streams", "-show_entries", "stream=extradata", "-show_data", "-of", "json", path], token)
            .ConfigureAwait(false);
        using var doc = JsonDocument.Parse(json);
        var streams = doc.RootElement.GetProperty("streams");
        if (streams.GetArrayLength() != 1 || !streams[0].TryGetProperty("extradata", out var data)) return null;
        var bytes = new List<byte>();
        foreach (var line in data.GetString()!.Split('\n'))
        {
            var colon = line.IndexOf(':');
            if (colon < 0) continue;
            var hex = line[(colon + 1)..].Split("  ", StringSplitOptions.None)[0].Replace(" ", "");
            if (hex.Length > 0) bytes.AddRange(Convert.FromHexString(hex));
        }
        return bytes.Count == 0 ? null : Encoding.UTF8.GetString(bytes.ToArray());
    }

    private static async Task<string> ProbeAsync(string ffprobe, string[] args, CancellationToken token)
    {
        using var process = new Process { StartInfo = new ProcessStartInfo(ffprobe)
        {
            UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true,
            CreateNoWindow = true
        } };
        foreach (var arg in args) process.StartInfo.ArgumentList.Add(arg);
        process.Start();
        try
        {
            var stderrTask = ReadBoundedAsync(process.StandardError, 12000, token);
            var stdoutTask = ReadBoundedAsync(process.StandardOutput, MaxProbeOutput, token);
            var stdout = await stdoutTask.ConfigureAwait(false);
            var stderr = await stderrTask.ConfigureAwait(false);
            await process.WaitForExitAsync(token).ConfigureAwait(false);
            if (process.ExitCode != 0) throw new InvalidDataException($"FFprobe failed to read VobSub data: {stderr}");
            return stdout;
        }
        catch
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            throw;
        }
    }

    private static async Task<string> ReadBoundedAsync(StreamReader reader, int limit, CancellationToken token)
    {
        var text = new StringBuilder();
        var buffer = new char[8192];
        while (true)
        {
            var count = await reader.ReadAsync(buffer.AsMemory(), token).ConfigureAwait(false);
            if (count == 0) return text.ToString();
            if (text.Length + count > limit)
                throw new InvalidDataException("FFprobe output exceeds the VobSub safety limit.");
            text.Append(buffer, 0, count);
        }
    }
}
