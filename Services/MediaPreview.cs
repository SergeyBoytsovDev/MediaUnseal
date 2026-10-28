using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Collections.Generic;
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
        CancellationToken cancellationToken, Action<string>? report = null)
    {
        var search = Stopwatch.StartNew();
        var covers = media.Streams.Where(s => s.ImageFormat is "JPEG" or "PNG"
                && (s.IsCoverArt || MatroskaAttachments.IsCandidate(media, s)))
            .OrderByDescending(s => s.IsCoverArt).ThenBy(s => s.Index).ToArray();
        report?.Invoke($"Поиск обложки: {search.Elapsed.TotalMilliseconds:0} мс; кандидатов {covers.Length}.");
        IReadOnlyList<MatroskaAttachments.Attachment>? matroskaEntries = null;
        var matroskaReadAttempted = false;
        foreach (var cover in covers)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (cover.Width is > 6000 || cover.Height is > 6000)
            { report?.Invoke("Обложка пропущена: размеры превышают предел."); continue; }
            var stage = "поиск";
            var watch = Stopwatch.StartNew();
            MatroskaAttachments.NavigationStats? navigation = null;
            try
            {
                if (MatroskaAttachments.IsCandidate(media, cover))
                {
                    if (matroskaReadAttempted && matroskaEntries is null) continue;
                    await using var input = new FileStream(media.FullPath, FileMode.Open, FileAccess.Read,
                        FileShare.Read, 4096, FileOptions.Asynchronous | FileOptions.RandomAccess);
                    if (input.Length != media.Size)
                    { report?.Invoke("Поиск вложений Matroska: размер файла изменился после анализа."); continue; }
                    if (matroskaEntries is null)
                    {
                        matroskaReadAttempted = true;
                        navigation = new MatroskaAttachments.NavigationStats();
                        stage = "чтение списка Matroska";
                        matroskaEntries = await MatroskaAttachments.ReadPreviewAsync(input, cancellationToken, navigation);
                        report?.Invoke($"Поиск вложений Matroska: {watch.Elapsed.TotalMilliseconds:0} мс; " +
                            $"SeekHead={navigation.UsedSeekHead}; переходов={navigation.IndexJumps}; " +
                            $"элементов={navigation.Elements}; прочитано {navigation.SearchBytesRead} байт; " +
                            $"найдено {matroskaEntries.Count}.");
                        watch.Restart();
                    }
                    stage = "сопоставление вложения";
                    var match = MatroskaAttachments.Match(matroskaEntries, media, cover);
                    if (match.Size > MaxImageBytes)
                    { report?.Invoke("Чтение вложения: размер превышает предел предпросмотра."); continue; }
                    report?.Invoke($"Сопоставление вложения: {watch.Elapsed.TotalMilliseconds:0} мс; " +
                        $"размер {match.Size} байт.");
                    watch.Restart();
                    stage = "чтение вложения";
                    await using var output = new MemoryStream((int)match.Size);
                    await MatroskaAttachments.CopyAsync(input, output, match, null, cancellationToken);
                    report?.Invoke($"Чтение вложения: {watch.Elapsed.TotalMilliseconds:0} мс; успешно.");
                    return output.ToArray();
                }
                if (cover.Index is { } index)
                {
                    var image = await GrabFrameAsync(media.FullPath, ffmpegPath, index, false,
                        cancellationToken, report);
                    if (image.Image is not null) return image.Image;
                }
            }
            catch (OperationCanceledException)
            {
                report?.Invoke($"{stage}: {watch.Elapsed.TotalMilliseconds:0} мс; отменено или тайм-аут" +
                    (navigation is null ? "." : $"; SeekHead={navigation.UsedSeekHead}; " +
                        $"переходов={navigation.IndexJumps}; элементов={navigation.Elements}; " +
                        $"прочитано {navigation.SearchBytesRead} байт."));
                throw;
            }
            catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException
                or Win32Exception or TimeoutException)
            { report?.Invoke($"{stage}: {watch.Elapsed.TotalMilliseconds:0} мс; {ex.GetType().Name}" +
                (navigation is null ? "." : $"; SeekHead={navigation.UsedSeekHead}; " +
                    $"переходов={navigation.IndexJumps}; элементов={navigation.Elements}; " +
                    $"прочитано {navigation.SearchBytesRead} байт.")); }
        }
        var video = media.Streams.FirstOrDefault(s => s.VisualKind == VisualKind.Video && s.Index is >= 0);
        if (video?.Index is { } videoIndex)
        {
            var image = await GrabFrameAsync(media.FullPath, ffmpegPath, videoIndex, true,
                cancellationToken, report);
            if (image.Image is not null || image.TimedOut) return image.Image;
            return (await GrabFrameAsync(media.FullPath, ffmpegPath, videoIndex, false,
                cancellationToken, report)).Image;
        }
        report?.Invoke("Кадр FFmpeg: не запущен; подходящего видеопотока нет.");
        return null;
    }

    private sealed record FrameResult(byte[]? Image, bool TimedOut = false);

    private static async Task<FrameResult> GrabFrameAsync(string source, string ffmpegPath, int index,
        bool seek, CancellationToken cancellationToken, Action<string>? report)
    {
        var watch = Stopwatch.StartNew();
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
            if (!process.Start()) { report?.Invoke("Запуск FFmpeg: не удался."); return new(null); }
            report?.Invoke($"Запуск FFmpeg: {watch.Elapsed.TotalMilliseconds:0} мс; " +
                $"поток #{index}, {(seek ? "с переходом" : "с начала")}.");
            watch.Restart();
            try
            {
                var error = process.StandardError.ReadToEndAsync(deadline.Token);
                await process.WaitForExitAsync(deadline.Token);
                await error;
            }
            catch (OperationCanceledException)
            {
                if (!process.HasExited)
                    try { process.Kill(entireProcessTree: true); }
                    catch (InvalidOperationException) { }
                await process.WaitForExitAsync(CancellationToken.None);
                if (cancellationToken.IsCancellationRequested)
                    report?.Invoke($"Получение кадра FFmpeg: {watch.Elapsed.TotalMilliseconds:0} мс; отменено.");
                cancellationToken.ThrowIfCancellationRequested();
                report?.Invoke($"Получение кадра FFmpeg: {watch.Elapsed.TotalMilliseconds:0} мс; тайм-аут.");
                return new(null, true);
            }
            if (process.ExitCode != 0 || !File.Exists(output))
            {
                report?.Invoke($"Получение кадра FFmpeg: {watch.Elapsed.TotalMilliseconds:0} мс; " +
                    $"код {process.ExitCode}, файл не создан.");
                return new(null);
            }
            var length = new FileInfo(output).Length;
            if (length is not (> 0 and <= MaxImageBytes))
            {
                report?.Invoke($"Получение кадра FFmpeg: {watch.Elapsed.TotalMilliseconds:0} мс; " +
                    "размер изображения вне допустимых пределов.");
                return new(null);
            }
            var bytes = await File.ReadAllBytesAsync(output, cancellationToken);
            report?.Invoke($"Получение кадра FFmpeg: {watch.Elapsed.TotalMilliseconds:0} мс; " +
                $"успешно, {bytes.Length} байт.");
            return new(bytes);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or Win32Exception
            or InvalidOperationException)
        {
            report?.Invoke($"Получение кадра FFmpeg: {watch.Elapsed.TotalMilliseconds:0} мс; " +
                $"{ex.GetType().Name}.");
            return new(null);
        }
        finally
        {
            try { Directory.Delete(directory, recursive: true); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }
}
