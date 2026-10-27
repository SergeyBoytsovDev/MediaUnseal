using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using TrackExtractor.Models;

namespace TrackExtractor.Services;

internal static class MediaPreview
{
    internal const int MaxImageBytes = 8 * 1024 * 1024;
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(15);

    internal static async Task<byte[]?> LoadAsync(MediaInfo media, string ffmpegPath,
        CancellationToken cancellationToken)
    {
        var covers = media.Streams.Where(s => s.IsCoverArt && s.ImageFormat is "JPEG" or "PNG")
            .OrderBy(s => s.Index).ToArray();
        foreach (var cover in covers)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (cover.Width is > 6000 || cover.Height is > 6000) continue;
            try
            {
                if (MatroskaAttachments.IsCandidate(media, cover))
                {
                    await using var input = new FileStream(media.FullPath, FileMode.Open, FileAccess.Read,
                        FileShare.Read, 4096, FileOptions.Asynchronous | FileOptions.RandomAccess);
                    if (input.Length != media.Size) continue;
                    var entries = await MatroskaAttachments.ReadAsync(input, cancellationToken);
                    var match = MatroskaAttachments.Match(entries, media, cover);
                    if (match.Size > MaxImageBytes) continue;
                    await using var output = new MemoryStream((int)match.Size);
                    await MatroskaAttachments.CopyAsync(input, output, match, null, cancellationToken);
                    return output.ToArray();
                }
                if (cover.Index is { } index)
                {
                    var image = await GrabFrameAsync(media.FullPath, ffmpegPath, index, false, cancellationToken);
                    if (image is not null) return image;
                }
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException
                or Win32Exception or TimeoutException) { }
        }
        var video = media.Streams.FirstOrDefault(s => s.VisualKind == VisualKind.Video && s.Index is >= 0);
        if (video?.Index is { } videoIndex)
        {
            var image = await GrabFrameAsync(media.FullPath, ffmpegPath, videoIndex, true, cancellationToken);
            if (image is not null) return image;
            return await GrabFrameAsync(media.FullPath, ffmpegPath, videoIndex, false, cancellationToken);
        }
        return null;
    }

    private static async Task<byte[]?> GrabFrameAsync(string source, string ffmpegPath, int index,
        bool seek, CancellationToken cancellationToken)
    {
        var directory = Path.Combine(Path.GetTempPath(), "TrackExtractor-preview-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var output = Path.Combine(directory, "frame.png");
        try
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(Deadline);
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo(ffmpegPath)
                {
                    UseShellExecute = false, CreateNoWindow = true,
                    RedirectStandardError = true, RedirectStandardInput = true
                }
            };
            var args = process.StartInfo.ArgumentList;
            args.Add("-nostdin"); args.Add("-hide_banner"); args.Add("-loglevel"); args.Add("error");
            if (seek) { args.Add("-ss"); args.Add("00:00:01"); }
            args.Add("-i"); args.Add(source);
            args.Add("-map"); args.Add($"0:{index}");
            args.Add("-frames:v"); args.Add("1");
            args.Add("-vf"); args.Add("scale='min(360,iw)':-2");
            args.Add("-f"); args.Add("image2"); args.Add(output);
            if (!process.Start()) return null;
            try
            {
                var error = process.StandardError.ReadToEndAsync(deadline.Token);
                await process.WaitForExitAsync(deadline.Token);
                await error;
            }
            catch (OperationCanceledException)
            {
                if (!process.HasExited) process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync(CancellationToken.None);
                cancellationToken.ThrowIfCancellationRequested();
                return null;
            }
            if (process.ExitCode != 0 || !File.Exists(output)) return null;
            var length = new FileInfo(output).Length;
            return length is > 0 and <= MaxImageBytes
                ? await File.ReadAllBytesAsync(output, cancellationToken) : null;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or Win32Exception
            or InvalidOperationException) { return null; }
        finally
        {
            try { Directory.Delete(directory, recursive: true); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }
}
