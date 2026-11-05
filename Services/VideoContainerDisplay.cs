using System;
using System.IO;
using System.Linq;
using MediaUnseal.Models;

namespace MediaUnseal.Services;

internal static class VideoContainerDisplay
{
    internal static string Format(MediaInfo media)
    {
        var raw = media.Container;
        if (string.IsNullOrWhiteSpace(raw)) return "—";
        var names = raw.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        var extension = Path.GetExtension(media.FullPath).ToLowerInvariant();

        if (names.Contains("matroska") || names.Contains("webm"))
        {
            if (extension == ".mka") return raw;
            var documentType = ReadEbmlDocType(media.FullPath);
            if (documentType is "matroska" or "webm") return documentType == "webm" ? "WEBM" : "MKV";
            if (documentType is not null) return "—";
            if (names.Length == 1) return names[0] == "webm" ? "WEBM" : "MKV";
            return extension switch { ".mkv" => "MKV", ".webm" => "WEBM", _ => "—" };
        }

        if (names.Contains("mov") && names.Contains("mp4"))
        {
            // FFprobe reports one demuxer family for these distinct ISO BMFF containers.
            if (extension is ".m4a" or ".mj2" or ".heic" or ".heif") return raw;
            media.Tags.TryGetValue("major_brand", out var brand);
            brand = brand?.Trim().ToLowerInvariant();
            if (brand is not null)
            {
                if (brand.StartsWith("3g2", StringComparison.Ordinal)) return "3G2";
                if (brand.StartsWith("3gp", StringComparison.Ordinal)) return "3GP";
                if (brand == "qt") return "MOV";
                if (brand is "isom" or "iso2" or "iso4" or "iso5" or "iso6" or "mp41" or "mp42" or "avc1" or "dash")
                    return "MP4";
            }
            return extension switch
            {
                ".mov" => "MOV", ".mp4" => "MP4", ".m4v" => "MP4",
                ".3gp" => "3GP", ".3g2" => "3G2", _ => "—"
            };
        }

        return names[0] switch
        {
            "mov" => "MOV", "mp4" => "MP4", "3gp" => "3GP", "3g2" => "3G2",
            "avi" => "AVI", "mpegts" => "TS", "mpeg" => "MPG", "mxf" => "MXF",
            "flv" => "FLV", "asf" => "ASF", "nut" => "NUT",
            "ogg" when extension == ".ogv" || media.VideoCount > 0 => "OGV",
            _ => raw
        };
    }

    private static string? ReadEbmlDocType(string path)
    {
        try
        {
            using var input = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            if (input.Length < 5 || input.ReadByte() != 0x1A || input.ReadByte() != 0x45
                || input.ReadByte() != 0xDF || input.ReadByte() != 0xA3) return "";
            var headerSize = ReadVint(input);
            if (headerSize is null or > 65536 || headerSize > input.Length - input.Position) return "";
            var headerEnd = input.Position + headerSize.Value;
            while (input.Position < headerEnd)
            {
                var id = ReadVint(input, preserveMarker: true);
                var size = ReadVint(input);
                if (id is null || size is null || size > headerEnd - input.Position) return "";
                if (id == 0x4282 && size is > 0 and <= 16)
                {
                    var bytes = new byte[(int)size.Value];
                    input.ReadExactly(bytes);
                    return System.Text.Encoding.ASCII.GetString(bytes).ToLowerInvariant();
                }
                input.Position += size.Value;
            }
            return "";
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        return null;
    }

    private static long? ReadVint(Stream input, bool preserveMarker = false)
    {
        var first = input.ReadByte();
        if (first <= 0) return null;
        var mask = 0x80;
        var width = 1;
        while ((first & mask) == 0 && width < 8) { mask >>= 1; width++; }
        if ((first & mask) == 0) return null;
        long value = preserveMarker ? first : first & (mask - 1);
        for (var i = 1; i < width; i++)
        {
            var next = input.ReadByte();
            if (next < 0) return null;
            value = (value << 8) | (uint)next;
        }
        return value;
    }
}
