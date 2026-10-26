using System;
using System.Collections.Generic;
using System.Linq;

namespace TrackExtractor.Models;

public enum SubtitleKind { Unknown, Text, Graphic }
public enum VisualKind { Unknown, Video, Image, Animation }

public sealed record MediaStreamInfo(
    int? Index, string? CodecType, string? Codec, string? Profile,
    int? Width, int? Height, double? FrameRate, long? BitRate, string? PixelFormat,
    TimeSpan? Duration, int? SampleRate, int? Channels, string? ChannelLayout,
    string? Language, string? Title, int? Default, int? Forced, SubtitleKind SubtitleKind,
    IReadOnlyDictionary<string, string> Tags, IReadOnlyDictionary<string, int?> Disposition,
    long? FrameCount, VisualKind VisualKind, string? ImageFormat, bool IsCoverArt);

public sealed record MediaInfo(
    string Name, string FullPath, long Size, string? Container,
    TimeSpan? Duration, long? BitRate, IReadOnlyDictionary<string, string> Tags,
    IReadOnlyList<MediaStreamInfo> Streams)
{
    public int VideoCount => Streams.Count(s => s.VisualKind == VisualKind.Video);
    public int ImageCount => Streams.Count(s => s.VisualKind == VisualKind.Image);
    public int AnimationCount => Streams.Count(s => s.VisualKind == VisualKind.Animation);
    public int UnknownVisualCount => Streams.Count(s => s.CodecType == "video" && s.VisualKind == VisualKind.Unknown);
    public int AudioCount => Streams.Count(s => s.CodecType == "audio");
    public int SubtitleCount => Streams.Count(s => s.CodecType == "subtitle");
}
