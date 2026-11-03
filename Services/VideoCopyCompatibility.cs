using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using TrackExtractor.Models;

namespace TrackExtractor.Services;

internal enum VideoCopyOutcome { Standard, Adapted, Error }
internal enum VideoCopyError { Unknown, Codec, Profile, FrameRate, Resolution, PixelFormat, Timestamps, Headers, Bitstream }

internal sealed record VideoCopyDiagnosis(VideoCopyOutcome Outcome, string[] Options, string Basis,
    string? SuccessMessageKey = null, VideoCopyError Error = VideoCopyError.Unknown)
{
    internal string ErrorMessageKey => Error switch
    {
        VideoCopyError.Codec => "LogVideoCopyCodecError",
        VideoCopyError.Profile => "LogVideoCopyProfileError",
        VideoCopyError.FrameRate => "LogVideoCopyFrameRateError",
        VideoCopyError.Resolution => "LogVideoCopyResolutionError",
        VideoCopyError.PixelFormat => "LogVideoCopyPixelFormatError",
        VideoCopyError.Timestamps => "LogVideoCopyTimestampsError",
        VideoCopyError.Headers => "LogVideoCopyHeadersError",
        VideoCopyError.Bitstream => "LogVideoCopyBitstreamError",
        _ => "LogVideoCopyUnknownError"
    };
}

internal static class VideoCopyCompatibility
{
    // Only documented FFmpeg adaptations are allowed here. The muxer remains the final
    // authority for every codec/container combination; this code never re-encodes.
    internal static VideoCopyDiagnosis Plan(MediaStreamInfo stream, string muxer)
    {
        if (muxer == "avi" && stream.Codec == "h264")
            return new(VideoCopyOutcome.Adapted, ["-bsf:v", "h264_mp4toannexb"],
                "H.264 length-prefixed to Annex B bitstream filter for AVI", "LogAviH264Copy");
        if (muxer == "mxf" && !IsStandardMxfRate(stream))
            return new(VideoCopyOutcome.Adapted, ["-strict", "unofficial"],
                "MXF muxer permits unsupported content-package rates with FF_COMPLIANCE_UNOFFICIAL",
                "LogMxfUnofficialCopy");
        return new(VideoCopyOutcome.Standard, [], "No documented adaptation required from available stream metadata");
    }

    internal static VideoCopyDiagnosis Complete(VideoCopyDiagnosis planned, int exitCode,
        IEnumerable<string> ffmpegOutput) => exitCode == 0 ? planned
        : planned with { Outcome = VideoCopyOutcome.Error, Error = ClassifyError(ffmpegOutput) };

    private static bool IsStandardMxfRate(MediaStreamInfo stream)
    {
        // FFmpeg mxf.c, ff_mxf_get_content_package_rate (content package rate table).
        (int Numerator, int Denominator)[] rates =
        [
            (24, 1), (24000, 1001), (25, 1), (30, 1), (30000, 1001),
            (48, 1), (48000, 1001), (50, 1), (60, 1), (60000, 1001),
            (72, 1), (72000, 1001), (75, 1), (90, 1), (90000, 1001),
            (96, 1), (96000, 1001), (100, 1), (120, 1), (120000, 1001)
        ];
        var raw = stream.ExtraProperties?.GetValueOrDefault("avg_frame_rate")
            ?? stream.ExtraProperties?.GetValueOrDefault("r_frame_rate");
        if (raw is not null)
        {
            var parts = raw.Split('/');
            if (parts.Length == 2
                && long.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var numerator)
                && long.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var denominator)
                && numerator > 0 && denominator > 0)
                return rates.Any(rate => (decimal)numerator * rate.Denominator
                    == (decimal)denominator * rate.Numerator);
        }
        if (stream.FrameRate is not { } fps || fps <= 0) return true;
        return rates.Any(rate => Math.Abs(fps - (double)rate.Numerator / rate.Denominator) < 0.000001);
    }

    internal static VideoCopyError ClassifyError(IEnumerable<string> output)
    {
        var text = string.Join('\n', output);
        if (Match(text, @"unsupported frame rate|frame rate .*not supported|unofficial frame rate"))
            return VideoCopyError.FrameRate;
        if (Match(text, @"unsupported profile|profile .*not supported|profile .*unsupported"))
            return VideoCopyError.Profile;
        if (Match(text, @"could not find tag for codec|codec .*not currently supported in container|codec .*not supported in (?:container|format)|unsupported codec"))
            return VideoCopyError.Codec;
        if (Match(text, @"unsupported (?:resolution|dimensions)|invalid (?:video )?dimensions|width .*height .*not supported"))
            return VideoCopyError.Resolution;
        if (Match(text, @"(?:pixel|pix) format .*not supported|unsupported pixel format"))
            return VideoCopyError.PixelFormat;
        if (Match(text, @"non.monoton(?:ic|ically).*dts|timestamps? .*unset|pts .*has no value|invalid timestamps?"))
            return VideoCopyError.Timestamps;
        if (Match(text, @"(?:missing|invalid|malformed) (?:sequence )?header|header .*not found|extradata .*missing"))
            return VideoCopyError.Headers;
        if (Match(text, @"invalid nal unit|bitstream filter .*failed|error applying bitstream filter|invalid bitstream"))
            return VideoCopyError.Bitstream;
        return VideoCopyError.Unknown;
    }

    private static bool Match(string text, string pattern) =>
        Regex.IsMatch(text, pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
            TimeSpan.FromMilliseconds(50));
}
