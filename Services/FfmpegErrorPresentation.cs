using System;

namespace MediaUnseal.Services;

// Maps only explicit FFmpeg diagnostics to user-facing explanations. The original
// stderr remains in DiagnosticLog; ambiguous failures keep a neutral message.
internal static class FfmpegErrorPresentation
{
    internal static LocalizedMessage Explain(string? stderr)
    {
        if (string.IsNullOrWhiteSpace(stderr)) return new("LogMediaProcessingUnknown");
        if (Has(stderr, "h264_amf") && Has(stderr, "10-bit input video is not supported"))
            return new("LogAmfH264TenBitUnsupported");
        if (Has(stderr, "invalid channel layout") || Has(stderr, "unsupported channel layout"))
            return new("LogAudioChannelLayoutUnsupported");
        if (Has(stderr, "pixel format") && (Has(stderr, "not supported")
            || Has(stderr, "unsupported") || Has(stderr, "invalid")))
            return new("LogPixelFormatUnsupported");
        if (Has(stderr, "unknown encoder") || Has(stderr, "encoder not found")
            || Has(stderr, "unknown decoder") || Has(stderr, "decoder not found")
            || Has(stderr, "unknown codec")
            || Has(stderr, "encoder (") && Has(stderr, "not found")
            || Has(stderr, "decoder (") && Has(stderr, "not found"))
            return new("LogCodecUnsupported");
        if (Has(stderr, "invalid data found when processing input")
            || Has(stderr, "moov atom not found") || Has(stderr, "EBML header parsing failed"))
            return new("LogMediaSourceInvalid");
        if ((Has(stderr, "permission denied") || Has(stderr, "no space left on device"))
            && (Has(stderr, "opening output") || Has(stderr, "writing output")
                || Has(stderr, "output file")))
            return new("MainResultWriteFailed");
        return new("LogMediaProcessingUnknown");
    }

    private static bool Has(string text, string marker) =>
        text.Contains(marker, StringComparison.OrdinalIgnoreCase);
}
