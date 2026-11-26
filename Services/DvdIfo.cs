using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using MediaUnseal.Models;

namespace MediaUnseal.Services;

// Only the DVD title-set metadata needed for a lossless VobSub pair is read here.
// PGCs can have different palettes; without DVD navigation there is no safe way
// to assign different PGC palettes to packets in an independently opened VOB.
internal static class DvdIfo
{
    private const int Sector = 2048;
    private const int PgcSize = 236;
    private const int MaxPgc = 2048;
    private static readonly Regex VobName = new(@"\AVTS_(\d{2})_([0-9])\.VOB\z",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    internal static DvdIfoDependency? Resolve(MediaInfo media)
    {
        var match = VobName.Match(Path.GetFileName(media.FullPath));
        if (!match.Success || !media.Streams.Any(s => s.Codec == "dvd_subtitle")) return null;
        var set = int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
        var part = int.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture);
        int[] related = [];
        string? ifoPath = null;
        DvdIfoDependency Failure(string key, string? detail = null) =>
            new(ifoPath, null, key, set, part, related, 0, detail);
        try
        {
        var directory = Path.GetDirectoryName(media.FullPath)!;
        var prefix = $"VTS_{set:00}_";
        related = Directory.EnumerateFiles(directory)
            .Select(path => VobName.Match(Path.GetFileName(path)))
            .Where(m => m.Success && int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture) == set)
            .Select(m => int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture))
            .Distinct().Order().ToArray();
        var candidates = Directory.EnumerateFiles(directory)
            .Where(path => Path.GetFileName(path).Equals(prefix + "0.IFO", StringComparison.OrdinalIgnoreCase))
            .Take(2).ToArray();
        if (candidates.Length == 0) return Failure("MainDvdIfoMissing");
        if (candidates.Length != 1) return Failure("MainDvdIfoInvalid");
        ifoPath = candidates[0];
            using var file = new FileStream(candidates[0], FileMode.Open, FileAccess.Read, FileShare.Read,
                Sector, FileOptions.RandomAccess);
            if (file.Length < Sector || file.Length > 128L * 1024 * 1024)
                return Failure("MainDvdIfoInvalid");
            Span<byte> header = stackalloc byte[Sector];
            file.ReadExactly(header);
            if (!header[..12].SequenceEqual("DVDVIDEO-VTS"u8))
                return Failure("MainDvdIfoInvalid");
            var video = part == 0 ? header.Slice(0x100, 2) : header.Slice(0x200, 2);
            var standard = (video[0] >> 4) & 3;
            if (standard is not (0 or 1)) return Failure("MainDvdIfoInvalid");
            var dimensions = ((video[1] >> 2) & 3) switch
            {
                0 => (Width: 720, Height: standard == 0 ? 480 : 576),
                1 => (Width: 704, Height: standard == 0 ? 480 : 576),
                2 => (Width: 352, Height: standard == 0 ? 480 : 576),
                _ => (Width: 352, Height: standard == 0 ? 240 : 288)
            };
            var videoStreams = media.Streams.Where(s => s.CodecType == "video" && s.Width > 0 && s.Height > 0);
            if (videoStreams.Any(s => s.Width != dimensions.Width || s.Height != dimensions.Height))
                return Failure("MainDvdIfoMismatch", $"IFO video {dimensions.Width}x{dimensions.Height}; " +
                    $"VOB video {string.Join(",", videoStreams.Select(s => $"{s.Width}x{s.Height}"))}.");
            var count = part == 0 ? header[0x155] : header[0x255];
            if (count is < 1 or > 32 || part != 0
                && media.Streams.Count(s => s.Codec == "dvd_subtitle") > count)
                return Failure("MainDvdIfoMismatch", $"IFO subpicture attribute count {count}; " +
                    $"VOB subtitle streams {media.Streams.Count(s => s.Codec == "dvd_subtitle")}.");
            foreach (var subtitle in media.Streams.Where(s => s.Codec == "dvd_subtitle"))
            {
                if (part != 0 && subtitle.ExtraProperties?.GetValueOrDefault("id") is { } id
                    && TrySubtitleId(id, out var number) && number >= count)
                    return Failure("MainDvdIfoMismatch", $"VOB subtitle ID {id}; IFO subpicture attribute count {count}.");
            }
            var palettes = part == 0
                ? ReadMenuPalettes(file, header)
                : ReadTitlePalettes(file, header);
            if (palettes.Count == 0) return Failure("MainDvdIfoInvalid");
            if (palettes.Skip(1).Any(palette => !palette.SequenceEqual(palettes[0])))
                return Failure("MainDvdIfoAmbiguousPalette", $"Active subtitle PGC palettes: {palettes.Count}; " +
                    $"distinct palettes: {palettes.Select(Convert.ToHexString).Distinct().Count()}.");
            var rgb = palettes[0].Chunk(4).Select(ToRgb);
            var privateData = $"size: {dimensions.Width}x{dimensions.Height}\n" +
                "palette: " + string.Join(", ", rgb.Select(value => value.ToString("x6", CultureInfo.InvariantCulture))) +
                "\norg: 0, 0\n";
            return new(candidates[0], privateData, null, set, part, related, count);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or OverflowException)
        {
            return Failure("MainDvdIfoInvalid");
        }
    }

    internal static string? Language(MediaInfo media, MediaStreamInfo subtitle)
    {
        if (media.DvdIfo is not { UnavailableKey: null, Path: { } path, Part: not 0 } || subtitle.Codec != "dvd_subtitle"
            || subtitle.ExtraProperties?.GetValueOrDefault("id") is not { } id
            || !TrySubtitleId(id, out var number) || number >= media.DvdIfo.SubtitleStreamCount)
            return null;
        try
        {
            using var file = File.OpenRead(path);
            file.Position = (media.DvdIfo.Part == 0 ? 0x156 : 0x256) + number * 6 + 2;
            Span<byte> code = stackalloc byte[2];
            file.ReadExactly(code);
            return code[0] is >= (byte)'a' and <= (byte)'z'
                && code[1] is >= (byte)'a' and <= (byte)'z'
                ? Encoding.ASCII.GetString(code) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }

    private static bool TrySubtitleId(string id, out int number)
    {
        number = 0;
        if (!id.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
            || !int.TryParse(id.AsSpan(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var value))
            return false;
        if (value is < 0x20 or > 0x3f) return false;
        number = value - 0x20;
        return true;
    }

    private static List<byte[]> ReadTitlePalettes(Stream file, ReadOnlySpan<byte> header)
    {
        var sector = BinaryPrimitives.ReadUInt32BigEndian(header.Slice(0xcc, 4));
        return ReadPgcTable(file, checked((long)sector * Sector));
    }

    private static List<byte[]> ReadMenuPalettes(Stream file, ReadOnlySpan<byte> header)
    {
        var sector = BinaryPrimitives.ReadUInt32BigEndian(header.Slice(0xd0, 4));
        var table = checked((long)sector * Sector);
        if (table < Sector || table > file.Length - 8) return [];
        Span<byte> prefix = stackalloc byte[8];
        file.Position = table;
        file.ReadExactly(prefix);
        var count = BinaryPrimitives.ReadUInt16BigEndian(prefix);
        if (count is < 1 or > 100 || table + 8L + count * 8L > file.Length) return [];
        var palettes = new List<byte[]>();
        Span<byte> entry = stackalloc byte[8];
        for (var i = 0; i < count; i++)
        {
            file.Position = table + 8 + i * 8;
            file.ReadExactly(entry);
            var offset = BinaryPrimitives.ReadUInt32BigEndian(entry[4..]);
            var unit = ReadPgcTable(file, checked(table + offset));
            if (unit.Count == 0) return [];
            palettes.AddRange(unit);
        }
        return palettes;
    }

    private static List<byte[]> ReadPgcTable(Stream file, long table)
    {
        if (table < Sector || table > file.Length - 8) return [];
        Span<byte> prefix = stackalloc byte[8];
        file.Position = table;
        file.ReadExactly(prefix);
        var count = BinaryPrimitives.ReadUInt16BigEndian(prefix);
        var end = BinaryPrimitives.ReadUInt32BigEndian(prefix[4..]);
        if (count is < 1 or > MaxPgc || end < 8L + count * 8L
            || table + end >= file.Length) return [];
        var palettes = new List<byte[]>(count);
        Span<byte> entry = stackalloc byte[8];
        Span<byte> pgc = stackalloc byte[PgcSize];
        for (var i = 0; i < count; i++)
        {
            file.Position = table + 8 + i * 8;
            file.ReadExactly(entry);
            var offset = BinaryPrimitives.ReadUInt32BigEndian(entry[4..]);
            if (offset < 8L + count * 8L || offset + PgcSize > end + 1L) return [];
            file.Position = table + offset;
            file.ReadExactly(pgc);
            var hasSubtitle = false;
            for (var stream = 0; stream < 32; stream++)
                if ((BinaryPrimitives.ReadUInt32BigEndian(pgc.Slice(28 + stream * 4, 4)) & 0x80000000) != 0)
                { hasSubtitle = true; break; }
            if (pgc[3] == 0 || !hasSubtitle) continue;
            var palette = new byte[64];
            file.Position = table + offset + 0xa4;
            file.ReadExactly(palette);
            palettes.Add(palette);
        }
        return palettes;
    }

    private static int ToRgb(byte[] yCrCb)
    {
        var y = (yCrCb[1] - 16) * 255.0 / 219.0;
        var cr = yCrCb[2] - 128;
        var cb = yCrCb[3] - 128;
        static int Clip(double value) => Math.Clamp((int)Math.Round(value, MidpointRounding.AwayFromZero), 0, 255);
        return Clip(y + 1.402 * 255 / 224 * cr) << 16
            | Clip(y - 0.34414 * 255 / 224 * cb - 0.71414 * 255 / 224 * cr) << 8
            | Clip(y + 1.772 * 255 / 224 * cb);
    }
}
