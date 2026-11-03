using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using TrackExtractor.Models;

namespace TrackExtractor.Services;

public enum ExportState { Pending, Running, Completed, Failed, Cancelled }

public sealed record TrackExportPlan(MediaStreamInfo Stream, string Extension, string Muxer,
    bool StillImage, bool Attachment = false, ConversionSettings? Conversion = null, bool VerifiedCopy = false,
    AttachmentFile? AttachmentFile = null);
public sealed record ExportDecision(TrackExportPlan? Plan, string? Reason)
{
    internal LocalizedMessage? LocalizedReason { get; init; }
}
public sealed record ExportOptions(bool PreserveMetadata = true, bool GroupByLanguage = false,
    bool CreateSourceFolder = false);
public sealed record ExportUpdate(int StreamIndex, ExportState State, string Message,
    double? OverallPercent, int Processed, int Total, TimeSpan? MediaPosition = null,
    double? CurrentPercent = null, int GroupCount = 1)
{
    internal LocalizedMessage? LocalizedMessage { get; init; }
}
public sealed record TrackExportResult(int StreamIndex, ExportState State, string Message, string? OutputPath,
    TimeSpan Elapsed = default, long? OutputBytes = null)
{
    internal LocalizedMessage? LocalizedMessage { get; init; }
}
public sealed record ExportResult(IReadOnlyList<TrackExportResult> Tracks, bool WasCancelled, TimeSpan Elapsed = default)
{
    public bool Success => !WasCancelled && Tracks.Count > 0 && Tracks.All(t => t.State == ExportState.Completed);
}

public sealed class TrackExporter(DiagnosticLog diagnostics)
{
    private static LocalizationManager L => LocalizationManager.Active;
    internal Func<string, string[], IProgress<TimeSpan>?, CancellationToken, Task<int>>? ProcessRunner { get; init; }
    public TimeSpan TimeoutPerTrack { get; set; } = TimeSpan.FromHours(2);

    public static ExportDecision Decide(MediaInfo media, MediaStreamInfo stream)
    {
        static ExportDecision Unavailable(string key, params object?[] arguments)
        {
            var message = new LocalizedMessage(key, arguments);
            return new(null, message.Resolve()) { LocalizedReason = message };
        }
        if (stream.Index is not >= 0) return Unavailable("MainReasonNoStreamIndex");
        var originalAnimatedWebp = IsStandaloneAnimatedWebp(media, stream);
        var codec = stream.Codec?.ToLowerInvariant();
        if (codec is null && stream.CodecType != "attachment" && !originalAnimatedWebp)
            return Unavailable("MainReasonUnknownCodec");
        if (stream.VisualKind == VisualKind.Unknown && stream.CodecType == "video")
            return Unavailable("MainReasonUnknownVisual");

        (string Extension, string Muxer, bool StillImage)? output = null;
        if (stream.VisualKind == VisualKind.Image)
        {
            if (stream.CodecType == "attachment")
                return stream.ImageFormat switch
                {
                    "JPEG" => new(new TrackExportPlan(stream, "jpg", "attachment", false, true), null),
                    "PNG" => new(new TrackExportPlan(stream, "png", "attachment", false, true), null),
                    _ => Unavailable("MainReasonUnknownAttachmentFormat")
                };
            output = codec switch
            {
                "mjpeg" or "jpeg" => ("jpg", "image2", true),
                "png" => ("png", "image2", true),
                "bmp" => ("bmp", "image2", true),
                "tiff" => ("tiff", "image2", true),
                "qoi" => ("qoi", "image2", true),
                "webp" => ("webp", "image2", true),
                "gif" => ("gif", "image2", true),
                _ => null
            };
        }
        else if (stream.VisualKind == VisualKind.Animation)
        {
            output = originalAnimatedWebp ? ("webp", "webp", false) : codec switch
            {
                "gif" => ("gif", "gif", false),
                "apng" => ("apng", "apng", false),
                "webp" => ("webp", "webp", false),
                _ => null
            };
        }
        else if (stream.VisualKind == VisualKind.Video)
        {
            var sourceExtension = Path.GetExtension(media.FullPath).ToLowerInvariant();
            output = sourceExtension == ".mp4" && media.Container?.Split(',').Contains("mp4") == true
                && codec is ("h264" or "hevc" or "mpeg4")
                ? ("mp4", "mp4", false)
                : sourceExtension == ".mov" && media.Container?.Split(',').Contains("mov") == true
                    && codec is ("h264" or "hevc" or "mpeg4" or "prores")
                ? ("mov", "mov", false)
                : codec switch
            {
                "h264" or "hevc" or "av1" or "ffv1" => ("mkv", "matroska", false),
                "vp8" or "vp9" => ("webm", "webm", false),
                "mpeg2video" => ("m2v", "mpeg2video", false),
                "mpeg4" or "mjpeg" => ("avi", "avi", false),
                "prores" => ("mov", "mov", false),
                _ => null
            };
        }
        else if (stream.CodecType == "audio")
        {
            // A standalone audio file keeps its actual container as the original format.
            // The codec mapping below remains the extraction fallback for streams in other files.
            var sourceExtension = Path.GetExtension(media.FullPath).ToLowerInvariant();
            var sourceContainers = media.Container?.Split(',') ?? [];
            bool HasContainer(string name) => sourceContainers.Contains(name, StringComparer.OrdinalIgnoreCase);
            output = sourceExtension switch
            {
                ".caf" when HasContainer("caf") => ("caf", "caf", false),
                ".m4v" when HasContainer("m4a") => ("m4v", "ipod", false),
                ".m4a" when HasContainer("m4a") => ("m4a", "ipod", false),
                ".ogg" when HasContainer("ogg") => ("ogg", "ogg", false),
                ".aif" when HasContainer("aiff") => ("aif", "aiff", false),
                ".au" when HasContainer("au") => ("au", "au", false),
                ".wav" when HasContainer("wav") => ("wav", "wav", false),
                _ => null
            };
            output ??= codec switch
            {
                "aac" => ("aac", "adts", false),
                "mp3" => ("mp3", "mp3", false),
                "flac" => ("flac", "flac", false),
                "opus" => ("opus", "opus", false),
                "vorbis" => ("ogg", "ogg", false),
                "ac3" => ("ac3", "ac3", false),
                "eac3" => ("eac3", "eac3", false),
                "dts" => ("dts", "dts", false),
                "pcm_s16le" or "pcm_s24le" or "pcm_s32le" or "pcm_f32le" => ("wav", "wav", false),
                "alac" => ("m4a", "ipod", false),
                _ => null
            };
        }
        else if (stream.CodecType == "subtitle")
        {
            var extension = Path.GetExtension(media.FullPath).ToLowerInvariant();
            var containers = media.Container?.Split(',') ?? [];
            bool HasContainer(string name) => containers.Contains(name, StringComparer.OrdinalIgnoreCase);
            output = extension switch
            {
                ".srt" when HasContainer("srt") => ("srt", "srt", false),
                ".ass" when HasContainer("ass") => ("ass", "ass", false),
                ".ssa" when HasContainer("ass") => ("ssa", "ass", false),
                ".vtt" when HasContainer("webvtt") => ("vtt", "webvtt", false),
                ".lrc" when HasContainer("lrc") => ("lrc", "lrc", false),
                _ => null
            };
            output ??= codec switch
            {
                "subrip" or "srt" => ("srt", "srt", false),
                "ass" or "ssa" => ("ass", "ass", false),
                "webvtt" => ("vtt", "webvtt", false),
                "hdmv_pgs_subtitle" => ("sup", "sup", false),
                _ => null
            };
        }
        else return Unavailable("MainReasonUnsupportedStreamType");

        return output is { } format
            ? new(new TrackExportPlan(stream, format.Extension, format.Muxer, format.StillImage), null)
            : Unavailable("MainReasonUnsupportedCodec", codec);
    }

    private static bool IsStandaloneAnimatedWebp(MediaInfo media, MediaStreamInfo stream) =>
        stream.VisualKind == VisualKind.Animation && stream.CodecType == "video"
        && stream.ImageFormat == "WebP" && stream.Index == 0 && media.Streams.Count == 1
        && Path.GetExtension(media.FullPath).Equals(".webp", StringComparison.OrdinalIgnoreCase)
        && MediaAnalyzer.ReadImageSignature(media.FullPath) is ("WebP", true);

    public async Task<ExportResult> ExportAsync(MediaInfo media, IReadOnlyList<TrackExportPlan> selected,
        string outputDirectory, string ffmpegPath, IProgress<ExportUpdate>? progress, CancellationToken cancellationToken,
        ExportOptions? options = null)
    {
        options ??= new ExportOptions();
        if (selected.Count == 0) throw new ArgumentException("Выберите хотя бы одну дорожку.", nameof(selected));
        if (TimeoutPerTrack <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(TimeoutPerTrack));
        if (!Directory.Exists(outputDirectory)) throw new DirectoryNotFoundException("Выбранная папка сохранения не найдена.");
        if (!File.Exists(media.FullPath)) throw new FileNotFoundException("Исходный файл больше не найден.", media.FullPath);
        if (selected.Any(p => p.AttachmentFile is null
                ? p.Stream.Index is not >= 0 || !media.Streams.Contains(p.Stream)
                : !MatroskaAttachments.IsMatroska(media) || p.Stream.Index is not < 0
                    || p.Conversion is not null || !p.Attachment || p.Muxer != "attachment"
                    || p.AttachmentFile.Uid == 0 || p.AttachmentFile.Size <= 0
                    || string.IsNullOrWhiteSpace(p.AttachmentFile.Name)
                    || string.IsNullOrWhiteSpace(p.AttachmentFile.Mime)
                    || !System.Text.RegularExpressions.Regex.IsMatch(p.Extension, @"\A[a-zA-Z0-9]{1,10}\z")) ||
            selected.Select(p => p.Stream.Index).Distinct().Count() != selected.Count)
            throw new ArgumentException("Выбор потоков устарел или содержит повторения.", nameof(selected));
        if (selected.Where(p => p.AttachmentFile is not null)
            .Select(p => p.AttachmentFile!.Uid).Distinct().Count()
            != selected.Count(p => p.AttachmentFile is not null))
            throw new ArgumentException("Выбор вложений содержит повторения.", nameof(selected));
        if (selected.Any(p => p.AttachmentFile is not null ? false : p.Conversion is { } conversion
            ? conversion.Validate(p.Stream) is not null || p.Extension != conversion.Format.Extension
                || p.Muxer != conversion.Format.Muxer || p.Attachment || p.StillImage
            : p.VerifiedCopy ? p.StillImage || p.Attachment ||
                !System.Text.RegularExpressions.Regex.IsMatch(p.Extension, @"\A[a-zA-Z0-9]{1,10}\z") ||
                !System.Text.RegularExpressions.Regex.IsMatch(p.Muxer, @"\A[a-zA-Z0-9_]{1,64}\z")
              : Decide(media, p.Stream).Plan is not { } supported || supported != p))
            throw new ArgumentException("Один из выбранных форматов несовместим с потоком.", nameof(selected));
        var directory = Path.GetFullPath(outputDirectory);
        if (options.CreateSourceFolder)
            directory = Path.Combine(directory, SafeFolderName(Path.GetFileNameWithoutExtension(media.Name)));
        Directory.CreateDirectory(directory);
        var stage = Path.Combine(directory, ".trackextractor-" + Guid.NewGuid().ToString("N"));
        var results = new List<TrackExportResult>();
        var reserved = new HashSet<string>(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        var cancelled = false;
        var createdStage = false;
        var groupFallbackUntil = -1;
        var operationWatch = Stopwatch.StartNew();
        diagnostics.Record(DiagnosticLevel.Info, "Export",
            $"Started {DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz}; streams: {selected.Count}.",
            userMessageKey: "LogProcessingStartedCount", userMessageArgs: [selected.Count]);
        try
        {
            if (Directory.Exists(stage)) throw new IOException("Временный каталог уже существует.");
            Directory.CreateDirectory(stage);
            createdStage = true;
            if ((File.GetAttributes(stage) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Временный каталог не должен быть ссылкой.");
            for (var i = 0; i < selected.Count; i++)
            {
                if (cancellationToken.IsCancellationRequested) { cancelled = true; break; }
                var group = selected.Skip(i).TakeWhile(p => p.Conversion is null && !p.StillImage && !p.Attachment
                    && CopyVideoOptions(p).Length == 0).ToArray();
                if (i > groupFallbackUntil && group.Length > 1)
                {
                    var grouped = await TryExportGroupAsync(media, group, directory, stage, ffmpegPath,
                        reserved, results, progress, i, selected.Count, cancellationToken, options).ConfigureAwait(false);
                    if (grouped == GroupOutcome.Completed) { i += group.Length - 1; continue; }
                    if (grouped == GroupOutcome.Cancelled) { cancelled = true; break; }
                    groupFallbackUntil = i + group.Length - 1;
                    // If a shared FFmpeg process fails, retry each track separately so one bad stream
                    // cannot hide successful results from the others.
                }
                var plan = selected[i];
                var specialCopyOptions = CopyVideoOptions(plan);
                var specialCopyKey = specialCopyOptions.Contains("h264_mp4toannexb") ? "LogAviH264Copy"
                    : specialCopyOptions.Contains("unofficial") ? "LogMxfUnofficialCopy" : null;
                var ordinal = i;
                var index = plan.Stream.Index!.Value;
                var visualOutput = plan.Stream.VisualKind is VisualKind.Image or VisualKind.Animation;
                var target = ReserveName(media, plan, TargetDirectory(directory, plan, options), reserved);
                var temp = Path.Combine(stage, $"{index}-{Guid.NewGuid():N}.{plan.Extension}");
                var attachmentLabel = plan.AttachmentFile is { } namedAttachment
                    ? SafeAttachmentName(namedAttachment.Name) : null;
                progress?.Report(new(index, ExportState.Running,
                    attachmentLabel is null
                        ? L.Format(plan.Conversion is null ? "MainProgressExtractingTo" : "MainProgressConvertingTo",
                            index, Path.GetFileName(target))
                        : L.Format("MainProgressListedAttachment", attachmentLabel), null, i, selected.Count)
                { LocalizedMessage = attachmentLabel is null
                    ? new(plan.Conversion is null ? "MainProgressExtractingTo" : "MainProgressConvertingTo",
                        index, Path.GetFileName(target))
                    : new("MainProgressListedAttachment", attachmentLabel) });
                diagnostics.Record(DiagnosticLevel.Info, "Export",
                    $"{(plan.Conversion is null ? "Extraction started" : "Conversion started")}: type " +
                    $"{(plan.Stream.VisualKind is VisualKind.Image or VisualKind.Animation ? plan.Stream.VisualKind.ToString().ToLowerInvariant() : plan.Stream.CodecType)}, stream #{index}, " +
                    $"codec {plan.Stream.Codec}, format {plan.Muxer}; " +
                    (plan.Conversion is null ? "stream copy without re-encoding." : $"encoder {plan.Conversion.Encoder.Name}."),
                    userMessageKey: attachmentLabel is not null ? "LogAttachmentStarted" : visualOutput
                        ? plan.Conversion is null ? "LogVisualExtractionStarted" : "LogVisualConversionStarted"
                        : plan.Conversion is null ? "LogExtractingStreamFormat" : "LogConvertingStreamFormat",
                    userMessageArgs: attachmentLabel is not null ? [attachmentLabel] : [index, plan.Muxer]);
                if (plan.Stream.HeifGroupIndex is { } heifGroup)
                    diagnostics.Record(DiagnosticLevel.Info, "Export",
                        $"HEIF tile grid group #{heifGroup} selected for FFmpeg image conversion; " +
                        $"dimensions {plan.Stream.Width}x{plan.Stream.Height}.");
                var trackWatch = Stopwatch.StartNew();
                int? exitCode = null;
                TimeSpan? lastPosition = null;
                string? lastError = null;
                var ffmpegErrors = new Queue<string>();
                var nativeAttachment = plan.AttachmentFile is not null
                    || plan.Conversion is null && MatroskaAttachments.IsCandidate(media, plan.Stream);
                var nativeWebp = plan.Conversion is null && !plan.VerifiedCopy
                    && plan is { Muxer: "webp", Extension: "webp" }
                    && IsStandaloneAnimatedWebp(media, plan.Stream);
                var nativeCompleted = false;
                try
                {
                    using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                    deadline.CancelAfter(TimeoutPerTrack);
                    var duration = plan.Stream.Duration ?? media.Duration;
                    if (!nativeAttachment && !nativeWebp && duration is null or { Ticks: <= 0 })
                        diagnostics.Record(DiagnosticLevel.Info, "Export",
                            $"Stream #{index}: duration unknown; percentage unavailable.");
                    var lastLoggedBucket = -1;
                    var position = new InlineProgress<TimeSpan>(time =>
                    {
                        lastPosition = time;
                        if (duration is not { } known || known <= TimeSpan.Zero)
                        {
                            progress?.Report(new(index, ExportState.Running,
                                L.Format("MainProgressFfmpegPosition", index, FormatElapsed(time)),
                                null, ordinal, selected.Count, time)
                            { LocalizedMessage = new("MainProgressFfmpegPosition", index, FormatElapsed(time)) });
                            return;
                        }
                        var part = Math.Clamp(time.TotalSeconds / known.TotalSeconds, 0, 0.99);
                        var overall = Math.Min(99.0, (ordinal + part) * 100.0 / selected.Count);
                        var bucket = (int)(part * 10);
                        if (bucket > lastLoggedBucket)
                        {
                            lastLoggedBucket = bucket;
                            diagnostics.Record(DiagnosticLevel.Info, "Export",
                                $"Stream #{index}: processed approximately {part * 100:0}%.");
                        }
                        progress?.Report(new(index, ExportState.Running,
                            L.Format("MainProgressStream", index), overall, ordinal, selected.Count, time, part * 100)
                        { LocalizedMessage = new("MainProgressStream", index) });
                    });
                    if (nativeWebp)
                    {
                        await using var source = new FileStream(media.FullPath, FileMode.Open, FileAccess.Read,
                            FileShare.Read, 81920, FileOptions.Asynchronous | FileOptions.SequentialScan);
                        await using var destination = new FileStream(temp, FileMode.CreateNew, FileAccess.Write,
                            FileShare.None, 81920, FileOptions.Asynchronous);
                        await source.CopyToAsync(destination, deadline.Token).ConfigureAwait(false);
                        nativeCompleted = true;
                        diagnostics.Record(DiagnosticLevel.Info, "Export",
                            $"Animated WebP stream #{index}: original file bytes copied without re-encoding; " +
                            $"size {source.Length} bytes.");
                    }
                    if (nativeAttachment)
                    {
                        var lastCopyBucket = -1;
                        var copyProgress = new InlineProgress<double>(part =>
                        {
                            progress?.Report(new(index, ExportState.Running,
                                attachmentLabel is null ? L.Format("MainProgressAttachment", index, part)
                                    : L.Format("MainProgressListedAttachmentPercent", attachmentLabel, part),
                                Math.Min(99, (ordinal + part) * 100 / selected.Count), ordinal, selected.Count,
                                CurrentPercent: attachmentLabel is null ? part * 100 : null)
                            { LocalizedMessage = attachmentLabel is null
                                ? new("MainProgressAttachment", index, part)
                                : new("MainProgressListedAttachmentPercent", attachmentLabel, part) });
                            var bucket = (int)(part * 10);
                            if (bucket > lastCopyBucket)
                            {
                                lastCopyBucket = bucket;
                                diagnostics.Record(DiagnosticLevel.Info, "Export",
                                    $"Matroska attachment #{index}: copied {part:P0} of attachment.");
                            }
                        });
                        var attachment = plan.AttachmentFile is { } file
                            ? await CopyListedAttachmentAsync(media, file, temp, copyProgress, deadline.Token)
                            : await MatroskaAttachments.TryExtractAsync(media, plan.Stream, temp,
                            copyProgress, deadline.Token, stats => diagnostics.Record(DiagnosticLevel.Info, "Export",
                                $"Matroska #{index}: SeekHead={stats.UsedSeekHead}; index jumps={stats.IndexJumps}; " +
                                $"position changes={stats.PositionChanges}; elements={stats.Elements}; " +
                                $"search: read {stats.SearchBytesRead} bytes in {stats.SearchElapsed.TotalMilliseconds:0} ms; " +
                                $"matching={stats.MatchElapsed.TotalMilliseconds:0} ms; " +
                                $"copy: read {stats.CopyBytesRead} bytes in {stats.CopyElapsed.TotalMilliseconds:0} ms."))
                            .ConfigureAwait(false);
                        nativeCompleted = attachment is not null;
                        nativeAttachment = nativeCompleted;
                        if (attachment is not null)
                            diagnostics.Record(DiagnosticLevel.Info, "Export",
                                $"Matroska: FFprobe stream #{index} matched to FileUID {attachment.Uid}; " +
                                $"size {attachment.Size} bytes; copy completed.");
                    }
                    if (!nativeCompleted)
                    {
                        var conversionSource = media.FullPath;
                        var conversionIndex = index;
                        string? extractedCover = null;
                        if (plan.Conversion is not null && MatroskaAttachments.IsCandidate(media, plan.Stream))
                        {
                            diagnostics.Record(DiagnosticLevel.Info, "Export",
                                $"Matroska image conversion: extracting source attachment for stream #{index}.");
                            extractedCover = Path.Combine(stage, $"cover-{index}-{Guid.NewGuid():N}." +
                                (plan.Stream.ImageFormat == "PNG" ? "png" : "jpg"));
                            var found = await MatroskaAttachments.TryExtractAsync(media, plan.Stream,
                                extractedCover, null, deadline.Token).ConfigureAwait(false);
                            if (found is null) throw new InvalidDataException(L.Get("MainResultAttachmentFailed"));
                            conversionSource = extractedCover;
                            conversionIndex = 0;
                            diagnostics.Record(DiagnosticLevel.Info, "Export",
                                $"Matroska image conversion: source attachment found for stream #{index}; " +
                                $"FileUID {found.Uid}; {found.Size} bytes.");
                        }
                        try
                        {
                        var args = BuildArguments(conversionSource, conversionIndex, plan, temp, options.PreserveMetadata);
                        if (specialCopyKey is not null)
                        {
                            var rate = plan.Stream.ExtraProperties?.GetValueOrDefault("avg_frame_rate")
                                ?? plan.Stream.ExtraProperties?.GetValueOrDefault("r_frame_rate")
                                ?? plan.Stream.FrameRate?.ToString("R", CultureInfo.InvariantCulture) ?? "unknown";
                            diagnostics.Record(DiagnosticLevel.Info, "Export",
                                $"Video stream copy #{index}: source container {media.Container ?? "unknown"}; target {plan.Muxer}; " +
                                $"codec {plan.Stream.Codec ?? "unknown"}; profile {plan.Stream.Profile ?? "unknown"}; " +
                                $"resolution {plan.Stream.Width}x{plan.Stream.Height}; fps {rate}; mode copy; " +
                                $"options {string.Join(' ', specialCopyOptions)}; bitstream filter " +
                                $"{(specialCopyOptions.Contains("h264_mp4toannexb") ? "h264_mp4toannexb" : "none")}; " +
                                $"FFmpeg version {await ReadFfmpegVersionAsync(ffmpegPath, deadline.Token)}.",
                                "Command: " + Path.GetFileName(ffmpegPath) + " " + string.Join(' ', args.Select(arg => arg == conversionSource ? "[source]"
                                    : arg == temp ? "[output]" : arg)));
                        }
                        exitCode = ProcessRunner is null
                            ? await RunFfmpegAsync(ffmpegPath, args, position, line =>
                            {
                                lastError = line;
                                if (specialCopyKey is not null)
                                {
                                    if (ffmpegErrors.Count == 12) ffmpegErrors.Dequeue();
                                    ffmpegErrors.Enqueue(line);
                                }
                            }, deadline.Token).ConfigureAwait(false)
                            : await ProcessRunner(ffmpegPath, args, position, deadline.Token).WaitAsync(deadline.Token).ConfigureAwait(false);
                        if (specialCopyKey is not null)
                        {
                            diagnostics.Record(exitCode == 0 ? DiagnosticLevel.Info : DiagnosticLevel.Error, "Export",
                                $"Video stream copy #{index}: FFmpeg result {(exitCode == 0 ? "success" : "failure")}; " +
                                $"exit code {exitCode}.");
                            foreach (var line in ffmpegErrors)
                                diagnostics.Record(exitCode == 0 ? DiagnosticLevel.Info : DiagnosticLevel.Error,
                                    "Export", "FFmpeg output: " + DiagnosticLog.RedactPersonalPaths(
                                        line.Replace(conversionSource, "[source]", StringComparison.OrdinalIgnoreCase)
                                            .Replace(temp, "[output]", StringComparison.OrdinalIgnoreCase)));
                        }
                        if (exitCode != 0) throw new InvalidDataException(L.Format("MainResultFfmpegExitCode", exitCode) +
                            (lastError is null ? "" : " " + lastError));
                        }
                        finally
                        {
                            if (extractedCover is not null) await DeleteOwnTempAsync(extractedCover).ConfigureAwait(false);
                        }
                    }
                    if (!File.Exists(temp) || new FileInfo(temp).Length == 0)
                        throw new InvalidDataException(L.Get("MainResultEmptyOutput"));
                    target = MoveWithoutOverwrite(temp, target, reserved);
                    var bytes = new FileInfo(target).Length;
                    var result = new TrackExportResult(index, ExportState.Completed,
                        L.Format("MainResultSaved", Path.GetFileName(target), FormatElapsed(trackWatch.Elapsed), FormatByteSize(bytes)), target,
                        trackWatch.Elapsed, bytes)
                    { LocalizedMessage = SavedMessage(target, trackWatch.Elapsed, bytes) };
                    results.Add(result);
                    progress?.Report(new(index, result.State, result.Message,
                        Math.Min(99.0, (i + 1) * 100.0 / selected.Count), i + 1, selected.Count)
                    { LocalizedMessage = result.LocalizedMessage });
                    diagnostics.Record(specialCopyKey == "LogMxfUnofficialCopy" ? DiagnosticLevel.Warn : DiagnosticLevel.Info,
                        "Export", $"Completed stream #{index}; container {plan.Muxer}; " +
                        $"mode {(plan.Conversion is null ? "extraction" : "conversion")}.",
                        $"Elapsed {FormatElapsed(trackWatch.Elapsed)}; size {bytes:N0} bytes; " +
                        $"Output path: {Path.GetFileName(target)}",
                        userMessageKey: specialCopyKey ?? (attachmentLabel is not null ? "LogAttachmentCompleted" : visualOutput
                            ? plan.Conversion is null ? "LogVisualExtractionCompleted" : "LogVisualConversionCompleted"
                            : plan.Conversion is null ? "LogStreamExtracted" : "LogStreamConverted"),
                        userMessageArgs: attachmentLabel is not null ? [attachmentLabel, FormatByteSize(bytes)] : visualOutput
                            ? [index, plan.Muxer, FormatByteSize(bytes)]
                            : [index, $" ({plan.Muxer})", $", {FormatByteSize(bytes)}"], userSuccess: true);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    cancelled = true;
                    results.Add(new(index, ExportState.Cancelled,
                        L.Format("MainResultCancelledAfter", FormatElapsed(trackWatch.Elapsed)), null, trackWatch.Elapsed)
                    { LocalizedMessage = new("MainResultCancelledAfter", FormatElapsed(trackWatch.Elapsed)) });
                    progress?.Report(new ExportUpdate(index, ExportState.Cancelled, L.Get("MainResultCancelled"), null, i, selected.Count)
                    { LocalizedMessage = new("MainResultCancelled") });
                    diagnostics.Record(DiagnosticLevel.Warn, "Export", $"Stream #{index} cancelled.",
                        userMessageKey: "LogStreamCancelled", userMessageArgs: [index]);
                    break;
                }
                catch (OperationCanceledException)
                {
                    var result = new TrackExportResult(index, ExportState.Failed,
                        L.Format("MainResultTimeout", FormatElapsed(trackWatch.Elapsed)), null,
                        trackWatch.Elapsed)
                    { LocalizedMessage = new("MainResultTimeout", FormatElapsed(trackWatch.Elapsed)) };
                    results.Add(result);
                    progress?.Report(new ExportUpdate(index, result.State, result.Message, null, i + 1, selected.Count)
                    { LocalizedMessage = result.LocalizedMessage });
                    diagnostics.Record(DiagnosticLevel.Error, "Export", $"Stream #{index} timed out.",
                        userMessageKey: "LogStreamTimeout", userMessageArgs: [index]);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or Win32Exception)
                {
                    LocalizedMessage? reasonMessage = ex switch
                    {
                        InvalidDataException when nativeAttachment => new("MainResultAttachmentFailed"),
                        InvalidDataException when exitCode is { } code and not 0 =>
                            lastError is null ? new("MainResultFfmpegExitCode", code)
                                : new("MainResultTechnicalDetail", new LocalizedMessage("MainResultFfmpegExitCode", code), lastError),
                        InvalidDataException => new("MainResultEmptyOutput"),
                        UnauthorizedAccessException => new("MainResultOutputAccessDenied"),
                        IOException => new("MainResultWriteFailed"),
                        _ => new("MainResultFfmpegLaunchFailed")
                    };
                    var message = ex switch
                    {
                        InvalidDataException when nativeAttachment => L.Get("MainResultAttachmentFailed"),
                        InvalidDataException => ex.Message,
                        UnauthorizedAccessException => L.Get("MainResultOutputAccessDenied"),
                        IOException => L.Get("MainResultWriteFailed"),
                        _ => L.Get("MainResultFfmpegLaunchFailed")
                    };
                    results.Add(new(index, ExportState.Failed,
                        L.Format("MainResultFailureWithTime", message, FormatElapsed(trackWatch.Elapsed)), null, trackWatch.Elapsed)
                    { LocalizedMessage = new("MainResultFailureWithTime", reasonMessage, FormatElapsed(trackWatch.Elapsed)) });
                    progress?.Report(new ExportUpdate(index, ExportState.Failed, message, null, i + 1, selected.Count)
                    { LocalizedMessage = reasonMessage });
                    diagnostics.Record(DiagnosticLevel.Error, "Export",
                        $"Stream #{index} failed: " +
                        (nativeAttachment && plan.AttachmentFile is null && ex is InvalidDataException
                            ? ex.Message : ex.GetType().Name) + "; " +
                        (nativeAttachment ? "Matroska reading; " : $"FFmpeg exit code {(exitCode?.ToString(CultureInfo.InvariantCulture) ?? "unknown")}; ") +
                        $"elapsed {FormatElapsed(trackWatch.Elapsed)}.",
                        userMessageKey: specialCopyKey is not null ? "LogSpecialCopyFailed" : attachmentLabel is not null ? "LogAttachmentFailed"
                            : plan.Conversion is null ? "LogStreamFailed" : "LogStreamConversionFailed",
                        userMessageArgs: specialCopyKey is not null ? [message]
                            : attachmentLabel is not null ? [attachmentLabel, message] : [index, message]);
                }
                catch (Exception ex)
                {
                    results.Add(new(index, ExportState.Failed,
                        L.Format("MainResultUnexpectedFailure", FormatElapsed(trackWatch.Elapsed)),
                        null, trackWatch.Elapsed)
                    { LocalizedMessage = new("MainResultUnexpectedFailure", FormatElapsed(trackWatch.Elapsed)) });
                    progress?.Report(new ExportUpdate(index, ExportState.Failed, L.Get("MainExtractionError"), null, i + 1, selected.Count)
                    { LocalizedMessage = new("MainExtractionError") });
                    diagnostics.Record(DiagnosticLevel.Error, "Export", $"Unexpected error in stream #{index}: {ex.GetType().Name}.",
                        userMessageKey: attachmentLabel is not null ? "LogAttachmentFailed"
                            : plan.Conversion is null ? "LogStreamFailed" : "LogStreamConversionFailed",
                        userMessageArgs: attachmentLabel is not null
                            ? [attachmentLabel, L.Get("MainExtractionError")]
                            : [index, L.Get("MainExtractionError")]);
                }
                finally
                {
                    diagnostics.Record(exitCode == 0 || nativeCompleted ? DiagnosticLevel.Info : DiagnosticLevel.Warn, "Export",
                        nativeAttachment
                            ? $"Matroska attachment reading #{index}: elapsed {FormatElapsed(trackWatch.Elapsed)}; " +
                                $"result {(nativeCompleted ? "bytes copied" : "incomplete")}."
                            : nativeWebp
                            ? $"Animated WebP original copy #{index}: elapsed {FormatElapsed(trackWatch.Elapsed)}; " +
                                $"result {(nativeCompleted ? "bytes copied" : "incomplete")}."
                            : $"FFmpeg for stream #{index}: elapsed {FormatElapsed(trackWatch.Elapsed)}, " +
                                $"exit code {(exitCode?.ToString(CultureInfo.InvariantCulture) ?? "unknown")}, " +
                                $"progress {(lastPosition is null ? "unavailable" : "received")}.");
                    if (!await DeleteOwnTempAsync(temp).ConfigureAwait(false))
                        diagnostics.Record(DiagnosticLevel.Warn, "Export", $"Failed to delete temporary file for stream #{index}.");
                }
            }
        }
        catch (Exception ex)
        {
            diagnostics.Record(DiagnosticLevel.Error, "Export",
                $"Queue interrupted: {ex.GetType().Name}; elapsed {FormatElapsed(operationWatch.Elapsed)}; " +
                $"finished {DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz}.");
            throw;
        }
        finally
        {
            var expectedRoot = Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (createdStage && Path.GetFullPath(stage).StartsWith(expectedRoot,
                    OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)
                && Directory.Exists(stage))
            {
                if (!await DeleteOwnStageAsync(stage).ConfigureAwait(false))
                    diagnostics.Record(DiagnosticLevel.Warn, "Export", "Temporary directory could not be deleted automatically.");
            }
        }
        if (cancelled)
            foreach (var remaining in selected.Skip(results.Count))
                results.Add(new TrackExportResult(remaining.Stream.Index!.Value, ExportState.Cancelled,
                    L.Get("MainResultNotStartedCancelled"), null)
                { LocalizedMessage = new("MainResultNotStartedCancelled") });
        operationWatch.Stop();
        if (results.Count == selected.Count && results.All(r => r.State == ExportState.Completed))
        {
            progress?.Report(new(selected[^1].Stream.Index!.Value, ExportState.Completed,
                L.Get("MainAllTracksExtracted"), 100, selected.Count, selected.Count)
            { LocalizedMessage = new("MainAllTracksExtracted") });
            diagnostics.Record(DiagnosticLevel.Info, "Export",
                $"Queue completed successfully at {DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz}; " +
                $"elapsed {FormatElapsed(operationWatch.Elapsed)}.",
                userMessageKey: "LogProcessingComplete", userSuccess: true);
        }
        else diagnostics.Record(cancelled ? DiagnosticLevel.Warn : DiagnosticLevel.Error, "Export",
            (cancelled ? "Queue cancelled by user" : "Queue completed with errors") +
            $" at {DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz}; elapsed {FormatElapsed(operationWatch.Elapsed)}.",
            userMessageKey: cancelled ? "LogProcessingCancelled" : "LogProcessingErrors");
        return new ExportResult(results, cancelled, operationWatch.Elapsed);
    }

    private enum GroupOutcome { Completed, RetrySeparately, Cancelled }

    private async Task<GroupOutcome> TryExportGroupAsync(MediaInfo media, TrackExportPlan[] group,
        string directory, string stage, string ffmpegPath, HashSet<string> reserved,
        List<TrackExportResult> results, IProgress<ExportUpdate>? progress, int processed, int total,
        CancellationToken cancellationToken, ExportOptions options)
    {
        var targets = group.Select(p => ReserveName(media, p, TargetDirectory(directory, p, options), reserved)).ToArray();
        var temps = group.Select(p => Path.Combine(stage,
            $"{p.Stream.Index}-{Guid.NewGuid():N}.{p.Extension}")).ToArray();
        var indexes = string.Join(", ", group.Select(p => $"#{p.Stream.Index}"));
        var watch = Stopwatch.StartNew();
        int? exitCode = null;
        TimeSpan? lastPosition = null;
        var completed = false;
        diagnostics.Record(DiagnosticLevel.Info, "Export",
            $"Shared FFmpeg pass started for streams {indexes}; " +
            string.Join("; ", group.Select(p => $"#{p.Stream.Index} {p.Stream.CodecType}/{p.Stream.Codec} → {p.Muxer}")));
        foreach (var plan in group)
            progress?.Report(new(plan.Stream.Index!.Value, ExportState.Running,
                L.Format("MainProgressSharedPass", plan.Stream.Index), null, processed, total)
            { LocalizedMessage = new("MainProgressSharedPass", plan.Stream.Index) });
        try
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var groupTicks = TimeoutPerTrack.Ticks > TimeSpan.MaxValue.Ticks / group.Length
                ? TimeSpan.MaxValue.Ticks : TimeoutPerTrack.Ticks * group.Length;
            deadline.CancelAfter(TimeSpan.FromTicks(groupTicks));
            var duration = media.Duration is { Ticks: > 0 } known ? known : (TimeSpan?)null;
            var position = new InlineProgress<TimeSpan>(time =>
            {
                lastPosition = time;
                var part = duration is { } length
                    ? Math.Clamp(time.TotalSeconds / length.TotalSeconds, 0, 0.99) : (double?)null;
                var overall = part is { } value
                    ? Math.Min(99, (processed + value * group.Length) * 100 / total) : (double?)null;
                progress?.Report(new(group[0].Stream.Index!.Value, ExportState.Running,
                    L.Format("MainProgressStreams", indexes), overall, processed, total, time, GroupCount: group.Length)
                { LocalizedMessage = new("MainProgressStreams", indexes) });
            });
            string? lastError = null;
            var args = BuildGroupArguments(media.FullPath, group, temps, options.PreserveMetadata);
            exitCode = ProcessRunner is null
                ? await RunFfmpegAsync(ffmpegPath, args, position, line => lastError = line,
                    deadline.Token).ConfigureAwait(false)
                : await ProcessRunner(ffmpegPath, args, position, deadline.Token)
                    .WaitAsync(deadline.Token).ConfigureAwait(false);
            if (exitCode != 0 || temps.Any(t => !File.Exists(t) || new FileInfo(t).Length == 0))
            {
                diagnostics.Record(DiagnosticLevel.Warn, "Export",
                    $"Shared FFmpeg pass for {indexes}: exit code {exitCode}; " +
                    "incomplete output; retrying streams separately." +
                    (lastError is null ? "" : $" Diagnostics: {SafeFfmpegError(lastError)}"));
                return GroupOutcome.RetrySeparately;
            }
            for (var j = 0; j < group.Length; j++)
            {
                var index = group[j].Stream.Index!.Value;
                try
                {
                    var target = MoveWithoutOverwrite(temps[j], targets[j], reserved);
                    var bytes = new FileInfo(target).Length;
                    var message = L.Format("MainResultSaved", Path.GetFileName(target), FormatElapsed(watch.Elapsed), FormatByteSize(bytes));
                    var savedMessage = SavedMessage(target, watch.Elapsed, bytes);
                    results.Add(new TrackExportResult(index, ExportState.Completed, message, target, watch.Elapsed, bytes)
                    { LocalizedMessage = savedMessage });
                    progress?.Report(new(index, ExportState.Completed, message,
                        Math.Min(99, (processed + j + 1) * 100.0 / total), processed + j + 1, total)
                    { LocalizedMessage = savedMessage });
                    diagnostics.Record(DiagnosticLevel.Info, "Export",
                        $"Stream #{index} completed; format {group[j].Muxer}; " +
                        $"elapsed {FormatElapsed(watch.Elapsed)}; size {bytes:N0} bytes; " +
                        $"Output path: {Path.GetFileName(target)}",
                        userMessageKey: "LogStreamExtracted",
                        userMessageArgs: [index, $" ({group[j].Muxer})", $", {FormatByteSize(bytes)}"], userSuccess: true);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    var message = L.Format("MainResultSaveStreamFailed", index, FormatElapsed(watch.Elapsed));
                    var failureMessage = new LocalizedMessage("MainResultSaveStreamFailed", index, FormatElapsed(watch.Elapsed));
                    results.Add(new TrackExportResult(index, ExportState.Failed, message, null, watch.Elapsed)
                    { LocalizedMessage = failureMessage });
                    progress?.Report(new ExportUpdate(index, ExportState.Failed, message, null, processed + j + 1, total)
                    { LocalizedMessage = failureMessage });
                    diagnostics.Record(DiagnosticLevel.Error, "Export",
                        $"Stream #{index}: failed to move output: {ex.GetType().Name}.",
                        userMessageKey: "LogStreamFailed", userMessageArgs: [index, L.Get("MainResultWriteFailed")]);
                }
            }
            completed = true;
            return GroupOutcome.Completed;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            var index = group[0].Stream.Index!.Value;
            results.Add(new TrackExportResult(index, ExportState.Cancelled,
                L.Format("MainResultExtractionCancelledAfter", FormatElapsed(watch.Elapsed)), null, watch.Elapsed)
            { LocalizedMessage = new("MainResultExtractionCancelledAfter", FormatElapsed(watch.Elapsed)) });
            progress?.Report(new ExportUpdate(index, ExportState.Cancelled, L.Get("MainExtractionCancelled"), null, processed, total)
            { LocalizedMessage = new("MainExtractionCancelled") });
            return GroupOutcome.Cancelled;
        }
        catch (OperationCanceledException)
        {
            for (var j = 0; j < group.Length; j++)
            {
                var plan = group[j];
                var index = plan.Stream.Index!.Value;
                var message = L.Format("MainResultFfmpegTimeout", FormatElapsed(watch.Elapsed));
                var timeoutMessage = new LocalizedMessage("MainResultFfmpegTimeout", FormatElapsed(watch.Elapsed));
                results.Add(new TrackExportResult(index, ExportState.Failed, message, null, watch.Elapsed)
                { LocalizedMessage = timeoutMessage });
                progress?.Report(new ExportUpdate(index, ExportState.Failed, message, null, processed + j + 1, total)
                { LocalizedMessage = timeoutMessage });
            }
            diagnostics.Record(DiagnosticLevel.Error, "Export",
                $"Shared FFmpeg pass for {indexes}: timed out after {FormatElapsed(watch.Elapsed)}.");
            completed = true;
            return GroupOutcome.Completed;
        }
        catch (Exception ex)
        {
            diagnostics.Record(DiagnosticLevel.Warn, "Export",
                $"Shared FFmpeg pass for {indexes}: {ex.GetType().Name}; retrying streams separately.");
            return GroupOutcome.RetrySeparately;
        }
        finally
        {
            diagnostics.Record(exitCode == 0 ? DiagnosticLevel.Info : DiagnosticLevel.Warn, "Export",
                $"Shared FFmpeg pass for {indexes}: elapsed {FormatElapsed(watch.Elapsed)}, " +
                $"exit code {(exitCode?.ToString(CultureInfo.InvariantCulture) ?? "unknown")}, " +
                $"progress {(lastPosition is null ? "unavailable" : "received")}.");
            foreach (var temp in temps)
                if (!await DeleteOwnTempAsync(temp).ConfigureAwait(false))
                    diagnostics.Record(DiagnosticLevel.Warn, "Export", "Failed to delete temporary file from shared pass.");
            if (!completed)
                foreach (var target in targets) reserved.Remove(target);
        }
    }

    internal static string[] BuildGroupArguments(string input, IReadOnlyList<TrackExportPlan> group,
        IReadOnlyList<string> temps, bool preserveMetadata = true)
    {
        var args = new List<string> { "-hide_banner", "-loglevel", "error", "-nostdin",
            "-stats_period", "0.5", "-progress", "pipe:2", "-i", input };
        for (var i = 0; i < group.Count; i++)
        {
            args.AddRange(["-map", $"0:{group[i].Stream.Index}", "-c", "copy"]);
            args.AddRange(["-map_metadata", preserveMetadata ? "0" : "-1",
                "-f", group[i].Muxer, temps[i]]);
        }
        return args.ToArray();
    }

    internal static string FormatElapsed(TimeSpan time) =>
        $"{(long)time.TotalHours:00}:{time.Minutes:00}:{time.Seconds:00}";

    private static LocalizedMessage SavedMessage(string path, TimeSpan elapsed, long bytes) =>
        new("MainResultSaved", Path.GetFileName(path), FormatElapsed(elapsed), ByteSizeMessage(bytes));

    private static async Task<MatroskaAttachments.Attachment> CopyListedAttachmentAsync(
        MediaInfo media, AttachmentFile file, string destination, IProgress<double>? progress,
        CancellationToken cancellationToken)
    {
        await MatroskaAttachments.CopySelectedAsync(media, file, destination, progress, cancellationToken)
            .ConfigureAwait(false);
        return new(file.Uid, file.Name, file.Mime, 0, file.Size, file.Description);
    }

    private static LocalizedMessage ByteSizeMessage(long bytes) => bytes switch
    {
        >= 1L << 30 => new("MainResultSizeGb", bytes / (double)(1L << 30)),
        >= 1L << 20 => new("MainResultSizeMb", bytes / (double)(1L << 20)),
        >= 1L << 10 => new("MainResultSizeKb", bytes / (double)(1L << 10)),
        _ => new("MainSizeBytes", bytes)
    };

    internal static string FormatByteSize(long bytes) => bytes switch
    {
        >= 1L << 30 => L.Format("MainResultSizeGb", bytes / (double)(1L << 30)),
        >= 1L << 20 => L.Format("MainResultSizeMb", bytes / (double)(1L << 20)),
        >= 1L << 10 => L.Format("MainResultSizeKb", bytes / (double)(1L << 10)),
        _ => L.Format("MainSizeBytes", bytes)
    };

    private static string SafeFfmpegError(string line)
    {
        // FFmpeg errors may include full input paths; record only a short generic hint.
        return line.Contains("Permission denied", StringComparison.OrdinalIgnoreCase)
            ? "permission denied" : "details are shown in the error message";
    }

    internal static string[] BuildArguments(string input, int index, TrackExportPlan plan, string temp,
        bool preserveMetadata = true) =>
        plan.Conversion is { } conversion
            ? WithMetadata(BuildConversionArguments(input, index, conversion, temp,
                plan.Stream.VisualKind == VisualKind.Animation,
                plan.Stream.PixelFormat is { } pixelFormat &&
                    (pixelFormat is "rgba" or "bgra" or "argb" or "abgr" or "pal8"
                        || pixelFormat.StartsWith("yuva", StringComparison.OrdinalIgnoreCase)
                        || pixelFormat.StartsWith("gbrap", StringComparison.OrdinalIgnoreCase)
                        || pixelFormat.StartsWith("ya", StringComparison.OrdinalIgnoreCase)),
                MediaAnalyzer.HeifGroupMap(plan.Stream)), preserveMetadata)
            : plan.Attachment
            ? ["-hide_banner", "-loglevel", "error", "-nostdin", $"-dump_attachment:{index}", temp, "-i", input]
            : plan.StillImage
            ? WithMetadata(["-hide_banner", "-loglevel", "error", "-nostdin", "-stats_period", "0.5", "-progress", "pipe:2",
                "-i", input, "-map", $"0:{index}", "-c", "copy", "-frames:v", "1", "-update", "1",
                "-f", plan.Muxer, temp], preserveMetadata)
            : WithMetadata(["-hide_banner", "-loglevel", "error", "-nostdin", "-stats_period", "0.5", "-progress", "pipe:2",
                "-i", input, "-map", $"0:{index}", "-c", "copy", ..CopyVideoOptions(plan), "-f", plan.Muxer, temp], preserveMetadata);

    internal static string[] CopyVideoOptions(TrackExportPlan plan)
    {
        if (plan.Conversion is not null || plan.Stream.CodecType != "video") return [];
        if (plan.Muxer == "avi" && plan.Stream.Codec == "h264")
            return ["-bsf:v", "h264_mp4toannexb"];
        if (plan.Muxer == "mxf" && !IsStandardMxfRate(plan.Stream))
            return ["-strict", "unofficial"];
        return [];
    }

    private static bool IsStandardMxfRate(MediaStreamInfo stream)
    {
        var rate = stream.FrameRate;
        if (rate is null or <= 0) return true;
        return new[] { 24d, 25d, 30d, 50d, 60d, 24000d / 1001, 30000d / 1001, 60000d / 1001 }
            .Any(standard => Math.Abs(rate.Value - standard) < 0.0001);
    }

    private static string[] WithMetadata(string[] args, bool preserve)
    {
        var result = args.ToList();
        result.InsertRange(result.Count - 3, ["-map_metadata", preserve ? "0" : "-1"]);
        return result.ToArray();
    }

    internal static string[] BuildConversionArguments(string input, int index, ConversionSettings options, string temp,
        bool animationInput = false, bool sourceHasAlpha = false, string? mapOverride = null)
    {
        var args = new List<string> { "-hide_banner", "-loglevel", "error", "-nostdin",
            "-stats_period", "0.5", "-progress", "pipe:2", "-i", input,
            "-map", mapOverride ?? $"0:{index}", "-c", options.Encoder.Name };
        AppendConversionOptions(args, options);
        AppendVisualOutputOptions(args, options, animationInput, sourceHasAlpha);
        if (options.Format.Muxer == "image2") args.AddRange(["-frames:v", "1", "-update", "1"]);
        args.AddRange(["-f", options.Format.Muxer, temp]);
        return args.ToArray();
    }

    internal static void AppendVisualOutputOptions(List<string> args, ConversionSettings options,
        bool animationInput = false, bool sourceHasAlpha = false)
    {
        if (options.Format.Muxer == "image2" && options.Format.Extension == "tiff")
            args.AddRange(["-pix_fmt", sourceHasAlpha ? "rgba" : "rgb24"]);
        if (animationInput && options.Encoder.Name is ("libvpx" or "libvpx-vp9"))
            args.AddRange(["-pix_fmt", sourceHasAlpha ? "yuva420p" : "yuv420p"]);
        if (animationInput && options.Encoder.Name == "libvpx")
            args.AddRange(["-auto-alt-ref", "0"]);
        if (animationInput && (options.Encoder.Name == "mpeg1video" && options.Format.Muxer == "mpeg"
            || options.Encoder.Name == "mpeg2video" && options.Format.Muxer == "mxf"))
            AppendMpegVideoOutputOptions(args, options);
    }

    internal static void AppendMpegVideoOutputOptions(List<string> args, ConversionSettings options)
    {
            args.AddRange(["-pix_fmt", "yuv420p"]);
            var scaleIndex = args.IndexOf("-vf");
            if (scaleIndex >= 0) args.RemoveRange(scaleIndex, 2);
            var width = options.Width?.ToString(CultureInfo.InvariantCulture) ?? "iw";
            var height = options.Height?.ToString(CultureInfo.InvariantCulture) ?? "ih";
            args.AddRange(["-vf", $"scale=trunc(({width})/2)*2:trunc(({height})/2)*2"]);
            var rateIndex = args.IndexOf("-r");
            if (rateIndex >= 0) args.RemoveRange(rateIndex, 2);
            var rate = options.FrameRate switch
            {
                24 => "24", 25 => "25", 30 => "30", 50 => "50", 60 => "60",
                var fps when fps is >= 23.97 and <= 23.98 => "24000/1001",
                var fps when fps is >= 29.96 and <= 29.98 => "30000/1001",
                var fps when fps is >= 59.93 and <= 59.95 => "60000/1001",
                _ => "25"
            };
            args.AddRange(["-r", rate]);
    }

    internal static void AppendConversionOptions(List<string> args, ConversionSettings options)
    {
        var kind = options.Encoder.Kind;
        if (options.Encoder.Name == "dca") args.AddRange(["-strict", "-2"]);
        if (options.Crf is { } crf) args.AddRange(["-crf", crf.ToString(CultureInfo.InvariantCulture)]);
        if (options.BitRateKbps is { } bitrate)
            args.AddRange([kind == "video" ? "-b:v" : "-b:a", bitrate.ToString(CultureInfo.InvariantCulture) + "k"]);
        if (options.Width is not null || options.Height is not null)
            args.AddRange(["-vf", $"scale={options.Width?.ToString(CultureInfo.InvariantCulture) ?? "-2"}:{options.Height?.ToString(CultureInfo.InvariantCulture) ?? "-2"}"]);
        if (options.FrameRate is { } fps) args.AddRange(["-r", fps.ToString(CultureInfo.InvariantCulture)]);
        if (options.SampleRate is { } sampleRate)
            args.AddRange(["-ar", sampleRate.ToString(CultureInfo.InvariantCulture)]);
        if (options.Channels is { } channels)
            args.AddRange(["-ac", channels.ToString(CultureInfo.InvariantCulture)]);
        if (options.Quality is { } quality)
            args.AddRange(["-q:a", quality.ToString(CultureInfo.InvariantCulture)]);
    }

    internal static string BuildFileName(MediaInfo media, TrackExportPlan plan)
    {
        if (plan.AttachmentFile is { } attachment)
            return SafeAttachmentName(attachment.Name);
        var stream = plan.Stream;
        var kind = stream.VisualKind switch
        {
            VisualKind.Video => "video", VisualKind.Image => "image", VisualKind.Animation => "animation",
            _ when stream.CodecType == "audio" => "audio",
            _ => "subtitle"
        };
        var parts = new[] { Path.GetFileNameWithoutExtension(media.Name), kind, stream.Index!.Value.ToString(CultureInfo.InvariantCulture),
            stream.Language, stream.Title }.Where(s => !string.IsNullOrWhiteSpace(s));
        var name = Sanitize(string.Join("_", parts));
        if (name.Length > 120) name = name[..120].TrimEnd(' ', '.');
        return name + "." + plan.Extension;
    }

    private static string Sanitize(string value)
    {
        var text = new StringBuilder(value.Length);
        foreach (var c in value)
            text.Append(char.IsControl(c) || c is '<' or '>' or ':' or '"' or '/' or '\\' or '|' or '?' or '*'
                ? '_' : c);
        var clean = text.ToString().Trim(' ', '.');
        return clean.Length == 0 ? "track" : clean;
    }

    private static string SafeFolderName(string value)
    {
        var name = Sanitize(value);
        if (name.Length > 100) name = name[..100].TrimEnd(' ', '.');
        if (name is "CON" or "PRN" or "AUX" or "NUL") name = "_" + name;
        return name.Length == 0 ? "media" : name;
    }

    private static string SafeAttachmentName(string name)
    {
        var leaf = name.Replace('\\', '/').Split('/').LastOrDefault() ?? "";
        var safe = Sanitize(leaf);
        if (safe.Length > 120) safe = safe[..120].TrimEnd(' ', '.');
        var stem = Path.GetFileNameWithoutExtension(safe).ToUpperInvariant();
        if (stem is "CON" or "PRN" or "AUX" or "NUL"
            || System.Text.RegularExpressions.Regex.IsMatch(stem, @"\A(?:COM|LPT)[1-9]\z"))
            safe = "_" + safe;
        return safe.Length == 0 ? "attachment" : safe;
    }

    private static string TargetDirectory(string root, TrackExportPlan plan, ExportOptions options)
    {
        if (!options.GroupByLanguage || plan.Stream.CodecType is not ("audio" or "subtitle")) return root;
        var language = SafeFolderName(string.IsNullOrWhiteSpace(plan.Stream.Language)
            ? L.Get("OutputLanguageUnspecified") : plan.Stream.Language);
        var target = Path.Combine(root, language);
        Directory.CreateDirectory(target);
        return target;
    }

    private static string ReserveName(MediaInfo media, TrackExportPlan plan, string directory, HashSet<string> reserved)
    {
        var requested = BuildFileName(media, plan);
        var stem = Path.GetFileNameWithoutExtension(requested);
        var extension = Path.GetExtension(requested);
        for (var attempt = 1; attempt < 10_000; attempt++)
        {
            var filename = attempt == 1 ? requested : $"{stem} ({attempt}){extension}";
            var target = Path.Combine(directory, filename);
            if (!File.Exists(target) && !Directory.Exists(target) && reserved.Add(target)) return target;
        }
        throw new IOException("Не удалось подобрать свободное имя выходного файла.");
    }

    private static string MoveWithoutOverwrite(string temp, string target, HashSet<string> reserved)
    {
        var stem = Path.GetFileNameWithoutExtension(target);
        var extension = Path.GetExtension(target);
        for (var attempt = 1; attempt < 10_000; attempt++)
        {
            var candidate = attempt == 1 ? target : Path.Combine(Path.GetDirectoryName(target)!, $"{stem} ({attempt + 1}){extension}");
            if (attempt > 1 && reserved.Contains(candidate)) continue;
            try
            {
                File.Move(temp, candidate, overwrite: false);
                reserved.Add(candidate);
                return candidate;
            }
            catch (IOException) when (File.Exists(candidate) || Directory.Exists(candidate)) { }
        }
        throw new IOException("Не удалось подобрать свободное имя выходного файла.");
    }

    private static async Task<bool> DeleteOwnTempAsync(string path)
    {
        for (var attempt = 0; attempt < 6; attempt++)
        {
            if (!File.Exists(path)) return true;
            try { File.Delete(path); return true; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                if (attempt == 5) return false;
                await Task.Delay(100).ConfigureAwait(false);
            }
        }
        return false;
    }

    private static async Task<bool> DeleteOwnStageAsync(string path)
    {
        for (var attempt = 0; attempt < 6; attempt++)
        {
            if (!Directory.Exists(path)) return true;
            try
            {
                if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) return false;
                Directory.Delete(path, recursive: false);
                return true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                if (attempt == 5) return false;
                await Task.Delay(100).ConfigureAwait(false);
            }
        }
        return false;
    }

    private static async Task<int> RunFfmpegAsync(string executable, string[] arguments,
        IProgress<TimeSpan>? progress, Action<string> onError, CancellationToken cancellationToken)
    {
        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true,
            RedirectStandardError = true, StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8
        };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = new Process { StartInfo = start };
        var started = false;
        try
        {
            if (!process.Start()) throw new Win32Exception("FFmpeg не запустился.");
            started = true;
            var output = DrainAsync(process.StandardOutput, cancellationToken);
            var errors = ReadProgressAsync(process.StandardError, progress, onError, cancellationToken);
            await Task.WhenAll(output, errors, process.WaitForExitAsync(cancellationToken)).ConfigureAwait(false);
            return process.ExitCode;
        }
        finally
        {
            if (started && !process.HasExited)
            {
                try { process.Kill(entireProcessTree: true); }
                catch (InvalidOperationException) { }
                try { await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false); }
                catch (InvalidOperationException) { }
            }
        }
    }

    private static async Task<string> ReadFfmpegVersionAsync(string executable, CancellationToken token)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo(executable)
            {
                UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true,
                RedirectStandardError = true, Arguments = "-version"
            }
        };
        var started = false;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(TimeSpan.FromSeconds(3));
            if (!process.Start()) return "unavailable";
            started = true;
            var firstLine = await process.StandardOutput.ReadLineAsync(timeout.Token).ConfigureAwait(false);
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            return process.ExitCode == 0 ? firstLine ?? "unknown" : "unavailable";
        }
        catch (Exception ex) when (ex is Win32Exception or IOException or OperationCanceledException)
        {
            return "unavailable";
        }
        finally
        {
            if (started && !process.HasExited)
            {
                try { process.Kill(entireProcessTree: true); }
                catch (InvalidOperationException) { }
                try { await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false); }
                catch (InvalidOperationException) { }
            }
        }
    }

    private static async Task DrainAsync(StreamReader reader, CancellationToken cancellationToken)
    {
        var buffer = new char[4096];
        while (await reader.ReadAsync(buffer, cancellationToken).ConfigureAwait(false) != 0) { }
    }

    private static async Task ReadProgressAsync(StreamReader reader, IProgress<TimeSpan>? progress,
        Action<string> onError, CancellationToken cancellationToken)
    {
        var buffer = new char[4096];
        var line = new StringBuilder(128);
        int count;
        while ((count = await reader.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) != 0)
            for (var i = 0; i < count; i++)
            {
                if (buffer[i] == '\n')
                {
                    var value = line.ToString().TrimEnd('\r');
                    if (value.StartsWith("out_time_us=", StringComparison.Ordinal) &&
                        long.TryParse(value.AsSpan(12), NumberStyles.Integer, CultureInfo.InvariantCulture, out var microseconds)
                        && microseconds >= 0 && microseconds <= TimeSpan.MaxValue.Ticks / 10)
                        progress?.Report(TimeSpan.FromMicroseconds(microseconds));
                    else if (value.StartsWith("out_time=", StringComparison.Ordinal) &&
                        TimeSpan.TryParse(value.AsSpan(9), CultureInfo.InvariantCulture, out var elapsed)
                        && elapsed >= TimeSpan.Zero)
                        progress?.Report(elapsed);
                    else if (value.Length > 0 && !value.StartsWith("progress=", StringComparison.Ordinal)
                        && !value.StartsWith("out_time_", StringComparison.Ordinal)
                        && !value.StartsWith("frame=", StringComparison.Ordinal)
                        && !value.StartsWith("fps=", StringComparison.Ordinal)
                        && !value.StartsWith("speed=", StringComparison.Ordinal)
                        && !value.StartsWith("bitrate=", StringComparison.Ordinal)
                        && !value.StartsWith("total_size=", StringComparison.Ordinal))
                        onError(value.Length > 300 ? value[..300] : value);
                    line.Clear();
                }
                else if (line.Length < 4096) line.Append(buffer[i]);
            }
    }

    private sealed class InlineProgress<T>(Action<T> action) : IProgress<T>
    {
        public void Report(T value) => action(value);
    }
}
