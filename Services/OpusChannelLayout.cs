using System;
using MediaUnseal.Models;

namespace MediaUnseal.Services;

internal static class OpusChannelLayout
{
    // libopus requires Vorbis layouts with rear channels for these channel counts.
    // Preserve every channel while moving the side pair to the corresponding rear pair.
    internal const string SideToRear51 = "pan=5.1|FL=FL|FR=FR|FC=FC|LFE=LFE|BL=SL|BR=SR";
    internal const string SideToRear50 = "pan=5.0|FL=FL|FR=FR|FC=FC|BL=SL|BR=SR";
    internal const string QuadSideToRear = "pan=quad|FL=FL|FR=FR|BL=SL|BR=SR";

    internal static string? FilterFor(string encoder, MediaStreamInfo? source,
        int? outputChannels = null)
    {
        if (encoder != "libopus" || source?.Channels is not { } channels
            || outputChannels is not null && outputChannels != channels)
            return null;
        return (source.ChannelLayout?.ToLowerInvariant(), channels) switch
        {
            ("5.1(side)", 6) => SideToRear51,
            ("5.0(side)", 5) => SideToRear50,
            ("quad(side)", 4) => QuadSideToRear,
            _ => null
        };
    }
}
