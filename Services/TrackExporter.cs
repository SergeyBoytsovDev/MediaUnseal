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
    bool StillImage, bool Attachment = false);
public sealed record ExportDecision(TrackExportPlan? Plan, string? Reason);
public sealed record ExportUpdate(int StreamIndex, ExportState State, string Message,
    double? OverallPercent, int Processed, int Total, TimeSpan? MediaPosition = null);
public sealed record TrackExportResult(int StreamIndex, ExportState State, string Message, string? OutputPath,
    TimeSpan Elapsed = default, long? OutputBytes = null);
public sealed record ExportResult(IReadOnlyList<TrackExportResult> Tracks, bool WasCancelled, TimeSpan Elapsed = default)
{
    public bool Success => !WasCancelled && Tracks.Count > 0 && Tracks.All(t => t.State == ExportState.Completed);
}

public sealed class TrackExporter(DiagnosticLog diagnostics)
{
    internal Func<string, string[], IProgress<TimeSpan>?, CancellationToken, Task<int>>? ProcessRunner { get; init; }
    public TimeSpan TimeoutPerTrack { get; set; } = TimeSpan.FromHours(2);

    public static ExportDecision Decide(MediaInfo media, MediaStreamInfo stream)
    {
        if (stream.Index is not >= 0) return new(null, "FFprobe не сообщил индекс потока.");
        var codec = stream.Codec?.ToLowerInvariant();
        if (codec is null && stream.CodecType != "attachment")
            return new(null, "Кодек не определён; безопасный формат без перекодирования неизвестен.");
        if (stream.VisualKind == VisualKind.Animation)
            return new(null, "Анимации пока только анализируются; извлечение добавим отдельно.");
        if (stream.VisualKind == VisualKind.Unknown && stream.CodecType == "video")
            return new(null, "Не удалось надёжно отличить видео от изображения.");

        (string Extension, string Muxer, bool StillImage)? output = null;
        if (stream.VisualKind == VisualKind.Image)
        {
            if (stream.CodecType == "attachment")
                return stream.ImageFormat switch
                {
                    "JPEG" => new(new TrackExportPlan(stream, "jpg", "attachment", false, true), null),
                    "PNG" => new(new TrackExportPlan(stream, "png", "attachment", false, true), null),
                    _ => new(null, "Формат графического вложения не подтверждён для извлечения без перекодирования.")
                };
            if (media.Streams.Count == 1 && media.Container?.Split(',').Any(format =>
                    format.Trim().EndsWith("_pipe", StringComparison.Ordinal) || format.Trim() == "image2") == true)
                return new(null, "Это отдельное изображение, а не дорожка медиаконтейнера.");
            output = codec switch
            {
                "mjpeg" or "jpeg" => ("jpg", "image2", true),
                "png" => ("png", "image2", true),
                _ => null
            };
        }
        else if (stream.VisualKind == VisualKind.Video)
        {
            output = codec switch
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
            output = codec switch
            {
                "aac" => ("aac", "adts", false),
                "mp3" => ("mp3", "mp3", false),
                "flac" => ("flac", "flac", false),
                "opus" => ("opus", "opus", false),
                "vorbis" => ("ogg", "ogg", false),
                "ac3" => ("ac3", "ac3", false),
                "eac3" => ("eac3", "eac3", false),
                "pcm_s16le" or "pcm_s24le" or "pcm_s32le" or "pcm_f32le" => ("wav", "wav", false),
                "alac" => ("m4a", "ipod", false),
                _ => null
            };
        }
        else if (stream.CodecType == "subtitle")
        {
            output = codec switch
            {
                "subrip" => ("srt", "srt", false),
                "ass" => ("ass", "ass", false),
                "webvtt" => ("vtt", "webvtt", false),
                "hdmv_pgs_subtitle" => ("sup", "sup", false),
                _ => null
            };
        }
        else return new(null, "Этот тип потока пока нельзя извлечь без перекодирования.");

        return output is { } format
            ? new(new TrackExportPlan(stream, format.Extension, format.Muxer, format.StillImage), null)
            : new(null, $"Для кодека {codec} нет подтверждённого способа копирования в отдельный файл.");
    }

    public async Task<ExportResult> ExportAsync(MediaInfo media, IReadOnlyList<TrackExportPlan> selected,
        string outputDirectory, string ffmpegPath, IProgress<ExportUpdate>? progress, CancellationToken cancellationToken)
    {
        if (selected.Count == 0) throw new ArgumentException("Выберите хотя бы одну дорожку.", nameof(selected));
        if (TimeoutPerTrack <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(TimeoutPerTrack));
        if (!Directory.Exists(outputDirectory)) throw new DirectoryNotFoundException("Выбранная папка сохранения не найдена.");
        if (!File.Exists(media.FullPath)) throw new FileNotFoundException("Исходный файл больше не найден.", media.FullPath);
        if (selected.Any(p => p.Stream.Index is not >= 0 || !media.Streams.Contains(p.Stream)) ||
            selected.Select(p => p.Stream.Index).Distinct().Count() != selected.Count)
            throw new ArgumentException("Выбор потоков устарел или содержит повторения.", nameof(selected));
        if (selected.Any(p => Decide(media, p.Stream).Plan is not { } supported || supported != p))
            throw new ArgumentException("Один из выбранных форматов несовместим с потоком.", nameof(selected));
        var directory = Path.GetFullPath(outputDirectory);
        var stage = Path.Combine(directory, ".trackextractor-" + Guid.NewGuid().ToString("N"));
        var results = new List<TrackExportResult>();
        var reserved = new HashSet<string>(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        var cancelled = false;
        var createdStage = false;
        var groupFallbackUntil = -1;
        var operationWatch = Stopwatch.StartNew();
        diagnostics.Record(DiagnosticLevel.Info, "Экспорт",
            $"Начало {DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz}; потоков: {selected.Count}.");
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
                var group = selected.Skip(i).TakeWhile(p => !p.StillImage && !p.Attachment).ToArray();
                if (i > groupFallbackUntil && group.Length > 1)
                {
                    var grouped = await TryExportGroupAsync(media, group, directory, stage, ffmpegPath,
                        reserved, results, progress, i, selected.Count, cancellationToken).ConfigureAwait(false);
                    if (grouped == GroupOutcome.Completed) { i += group.Length - 1; continue; }
                    if (grouped == GroupOutcome.Cancelled) { cancelled = true; break; }
                    groupFallbackUntil = i + group.Length - 1;
                    // If a shared FFmpeg process fails, retry each track separately so one bad stream
                    // cannot hide successful results from the others.
                }
                var plan = selected[i];
                var ordinal = i;
                var index = plan.Stream.Index!.Value;
                var target = ReserveName(media, plan, directory, reserved);
                var temp = Path.Combine(stage, $"{index}-{Guid.NewGuid():N}.{plan.Extension}");
                progress?.Report(new(index, ExportState.Running,
                    $"Поток #{index}: извлечение в {Path.GetFileName(target)}", null, i, selected.Count));
                diagnostics.Record(DiagnosticLevel.Info, "Экспорт",
                    $"Начато извлечение: тип {plan.Stream.CodecType}, поток #{index}, кодек {plan.Stream.Codec}, " +
                    $"формат {plan.Muxer}; копирование без перекодирования.");
                var trackWatch = Stopwatch.StartNew();
                int? exitCode = null;
                TimeSpan? lastPosition = null;
                var nativeAttachment = MatroskaAttachments.IsCandidate(media, plan.Stream);
                var nativeCompleted = false;
                try
                {
                    using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                    deadline.CancelAfter(TimeoutPerTrack);
                    var duration = plan.Stream.Duration ?? media.Duration;
                    if (!nativeAttachment && duration is null or { Ticks: <= 0 })
                        diagnostics.Record(DiagnosticLevel.Info, "Экспорт",
                            $"Поток #{index}: длительность неизвестна, процент хода недоступен.");
                    var lastLoggedBucket = -1;
                    var position = new InlineProgress<TimeSpan>(time =>
                    {
                        lastPosition = time;
                        if (duration is not { } known || known <= TimeSpan.Zero)
                        {
                            progress?.Report(new(index, ExportState.Running,
                                $"Поток #{index}: позиция FFmpeg {FormatElapsed(time)}",
                                null, ordinal, selected.Count, time));
                            return;
                        }
                        var part = Math.Clamp(time.TotalSeconds / known.TotalSeconds, 0, 0.99);
                        var overall = Math.Min(99.0, (ordinal + part) * 100.0 / selected.Count);
                        var bucket = (int)(part * 10);
                        if (bucket > lastLoggedBucket)
                        {
                            lastLoggedBucket = bucket;
                            diagnostics.Record(DiagnosticLevel.Info, "Экспорт",
                                $"Поток #{index}: обработано примерно {part * 100:0}%.");
                        }
                        progress?.Report(new(index, ExportState.Running,
                            $"Поток #{index}: ≈{overall:0.#}% общей очереди", overall, ordinal, selected.Count, time));
                    });
                    if (nativeAttachment)
                    {
                        var lastCopyBucket = -1;
                        var copyProgress = new InlineProgress<double>(part =>
                        {
                            progress?.Report(new(index, ExportState.Running,
                                $"Поток #{index}: копирование вложения Matroska, {part:P0}",
                                Math.Min(99, (ordinal + part) * 100 / selected.Count), ordinal, selected.Count));
                            var bucket = (int)(part * 10);
                            if (bucket > lastCopyBucket)
                            {
                                lastCopyBucket = bucket;
                                diagnostics.Record(DiagnosticLevel.Info, "Экспорт",
                                    $"Вложение Matroska #{index}: скопировано {part:P0} байтов.");
                            }
                        });
                        var attachment = await MatroskaAttachments.TryExtractAsync(media, plan.Stream, temp,
                            copyProgress, deadline.Token, stats => diagnostics.Record(DiagnosticLevel.Info, "Экспорт",
                                $"Matroska #{index}: SeekHead={stats.UsedSeekHead}; переходов по индексу={stats.IndexJumps}; " +
                                $"перемещений указателя={stats.PositionChanges}; элементов={stats.Elements}; " +
                                $"поиск: прочитано {stats.SearchBytesRead} байт за {stats.SearchElapsed.TotalMilliseconds:0} мс; " +
                                $"сопоставление={stats.MatchElapsed.TotalMilliseconds:0} мс; " +
                                $"копирование: прочитано {stats.CopyBytesRead} байт за {stats.CopyElapsed.TotalMilliseconds:0} мс."))
                            .ConfigureAwait(false);
                        nativeCompleted = attachment is not null;
                        nativeAttachment = nativeCompleted;
                        if (attachment is not null)
                            diagnostics.Record(DiagnosticLevel.Info, "Экспорт",
                                $"Matroska: поток FFprobe #{index} сопоставлен с FileUID {attachment.Uid}; " +
                                $"размер {attachment.Size} байт; копирование завершено.");
                    }
                    if (!nativeCompleted)
                    {
                        var args = BuildArguments(media.FullPath, index, plan, temp);
                        string? lastError = null;
                        exitCode = ProcessRunner is null
                            ? await RunFfmpegAsync(ffmpegPath, args, position, line => lastError = line, deadline.Token).ConfigureAwait(false)
                            : await ProcessRunner(ffmpegPath, args, position, deadline.Token).WaitAsync(deadline.Token).ConfigureAwait(false);
                        if (exitCode != 0) throw new InvalidDataException($"FFmpeg завершился с кодом {exitCode}." +
                            (lastError is null ? "" : " " + lastError));
                    }
                    if (!File.Exists(temp) || new FileInfo(temp).Length == 0)
                        throw new InvalidDataException("Не создан непустой файл дорожки.");
                    target = MoveWithoutOverwrite(temp, target, reserved);
                    var bytes = new FileInfo(target).Length;
                    var result = new TrackExportResult(index, ExportState.Completed,
                        $"Сохранён: {Path.GetFileName(target)}; {FormatElapsed(trackWatch.Elapsed)}; {bytes:N0} байт", target,
                        trackWatch.Elapsed, bytes);
                    results.Add(result);
                    progress?.Report(new(index, result.State, result.Message,
                        Math.Min(99.0, (i + 1) * 100.0 / selected.Count), i + 1, selected.Count));
                    diagnostics.Record(DiagnosticLevel.Info, "Экспорт", $"Завершён поток #{index}; контейнер {plan.Muxer}.",
                        $"Время {FormatElapsed(trackWatch.Elapsed)}; размер {bytes:N0} байт; " +
                        $"Путь результата: {Path.GetFileName(target)}");
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    cancelled = true;
                    results.Add(new(index, ExportState.Cancelled,
                        $"Извлечение отменено через {FormatElapsed(trackWatch.Elapsed)}.", null, trackWatch.Elapsed));
                    progress?.Report(new(index, ExportState.Cancelled, "Извлечение отменено.", null, i, selected.Count));
                    diagnostics.Record(DiagnosticLevel.Warn, "Экспорт", $"Отменён поток #{index}.");
                    break;
                }
                catch (OperationCanceledException)
                {
                    var result = new TrackExportResult(index, ExportState.Failed,
                        $"Превышено время ожидания извлечения ({FormatElapsed(trackWatch.Elapsed)}).", null,
                        trackWatch.Elapsed);
                    results.Add(result);
                    progress?.Report(new(index, result.State, result.Message, null, i + 1, selected.Count));
                    diagnostics.Record(DiagnosticLevel.Error, "Экспорт", $"Тайм-аут потока #{index}.");
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or Win32Exception)
                {
                    var message = ex switch
                    {
                        InvalidDataException => ex.Message,
                        UnauthorizedAccessException => "Нет доступа к папке или выходному файлу.",
                        IOException => "Не удалось записать файл. Проверьте свободное место и права доступа.",
                        _ => "Не удалось запустить FFmpeg. Повторите проверку зависимостей."
                    };
                    results.Add(new(index, ExportState.Failed,
                        $"{message} Время: {FormatElapsed(trackWatch.Elapsed)}.", null, trackWatch.Elapsed));
                    progress?.Report(new(index, ExportState.Failed, message, null, i + 1, selected.Count));
                    diagnostics.Record(DiagnosticLevel.Error, "Экспорт",
                        $"Ошибка потока #{index}: " +
                        (nativeAttachment && ex is InvalidDataException ? ex.Message : ex.GetType().Name) + "; " +
                        (nativeAttachment ? "чтение Matroska; " : $"код FFmpeg {(exitCode?.ToString(CultureInfo.InvariantCulture) ?? "нет")}; ") +
                        $"время {FormatElapsed(trackWatch.Elapsed)}.");
                }
                catch (Exception ex)
                {
                    results.Add(new(index, ExportState.Failed,
                        $"Не удалось извлечь поток. Время: {FormatElapsed(trackWatch.Elapsed)}. Проверьте лог и повторите попытку.",
                        null, trackWatch.Elapsed));
                    progress?.Report(new(index, ExportState.Failed, "Ошибка извлечения.", null, i + 1, selected.Count));
                    diagnostics.Record(DiagnosticLevel.Error, "Экспорт", $"Неожиданная ошибка потока #{index}: {ex.GetType().Name}.");
                }
                finally
                {
                    diagnostics.Record(exitCode == 0 || nativeCompleted ? DiagnosticLevel.Info : DiagnosticLevel.Warn, "Экспорт",
                        nativeAttachment
                            ? $"Чтение вложения Matroska #{index}: время {FormatElapsed(trackWatch.Elapsed)}; " +
                                $"результат {(nativeCompleted ? "байты скопированы" : "не завершено")}."
                            : $"FFmpeg для потока #{index}: время {FormatElapsed(trackWatch.Elapsed)}, " +
                                $"код завершения {(exitCode?.ToString(CultureInfo.InvariantCulture) ?? "нет")}, " +
                                $"прогресс {(lastPosition is null ? "не получен" : "получен")}.");
                    if (File.Exists(temp))
                    {
                        try { File.Delete(temp); }
                        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                        {
                            diagnostics.Record(DiagnosticLevel.Warn, "Экспорт", $"Не удалось удалить временный файл потока #{index}.");
                        }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            diagnostics.Record(DiagnosticLevel.Error, "Экспорт",
                $"Очередь прервана: {ex.GetType().Name}; время {FormatElapsed(operationWatch.Elapsed)}; " +
                $"окончание {DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz}.");
            throw;
        }
        finally
        {
            var expectedRoot = Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (createdStage && Path.GetFullPath(stage).StartsWith(expectedRoot,
                    OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)
                && Directory.Exists(stage))
            {
                try
                {
                    if ((File.GetAttributes(stage) & FileAttributes.ReparsePoint) == 0)
                        Directory.Delete(stage, recursive: false);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    diagnostics.Record(DiagnosticLevel.Warn, "Экспорт", "Временный каталог не удалось удалить автоматически.");
                }
            }
        }
        if (cancelled)
            foreach (var remaining in selected.Skip(results.Count))
                results.Add(new(remaining.Stream.Index!.Value, ExportState.Cancelled, "Не запускалось из-за отмены.", null));
        operationWatch.Stop();
        if (results.Count == selected.Count && results.All(r => r.State == ExportState.Completed))
        {
            progress?.Report(new(selected[^1].Stream.Index!.Value, ExportState.Completed,
                "Все дорожки извлечены.", 100, selected.Count, selected.Count));
            diagnostics.Record(DiagnosticLevel.Info, "Экспорт",
                $"Очередь завершена успешно в {DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz}; " +
                $"время {FormatElapsed(operationWatch.Elapsed)}.");
        }
        else diagnostics.Record(cancelled ? DiagnosticLevel.Warn : DiagnosticLevel.Error, "Экспорт",
            (cancelled ? "Очередь остановлена пользователем" : "Очередь завершена с ошибками") +
            $" в {DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz}; время {FormatElapsed(operationWatch.Elapsed)}.");
        return new ExportResult(results, cancelled, operationWatch.Elapsed);
    }

    private enum GroupOutcome { Completed, RetrySeparately, Cancelled }

    private async Task<GroupOutcome> TryExportGroupAsync(MediaInfo media, TrackExportPlan[] group,
        string directory, string stage, string ffmpegPath, HashSet<string> reserved,
        List<TrackExportResult> results, IProgress<ExportUpdate>? progress, int processed, int total,
        CancellationToken cancellationToken)
    {
        var targets = group.Select(p => ReserveName(media, p, directory, reserved)).ToArray();
        var temps = group.Select(p => Path.Combine(stage,
            $"{p.Stream.Index}-{Guid.NewGuid():N}.{p.Extension}")).ToArray();
        var indexes = string.Join(", ", group.Select(p => $"#{p.Stream.Index}"));
        var watch = Stopwatch.StartNew();
        int? exitCode = null;
        TimeSpan? lastPosition = null;
        var completed = false;
        diagnostics.Record(DiagnosticLevel.Info, "Экспорт",
            $"Начат общий FFmpeg для потоков {indexes}; " +
            string.Join("; ", group.Select(p => $"#{p.Stream.Index} {p.Stream.CodecType}/{p.Stream.Codec} → {p.Muxer}")));
        foreach (var plan in group)
            progress?.Report(new(plan.Stream.Index!.Value, ExportState.Running,
                $"Поток #{plan.Stream.Index}: общий проход FFmpeg", null, processed, total));
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
                    ? Math.Min(99, (processed + value) * 100 / total) : (double?)null;
                progress?.Report(new(group[0].Stream.Index!.Value, ExportState.Running,
                    $"Потоки {indexes}: общий проход, позиция FFmpeg {FormatElapsed(time)}",
                    overall, processed, total, time));
            });
            string? lastError = null;
            var args = BuildGroupArguments(media.FullPath, group, temps);
            exitCode = ProcessRunner is null
                ? await RunFfmpegAsync(ffmpegPath, args, position, line => lastError = line,
                    deadline.Token).ConfigureAwait(false)
                : await ProcessRunner(ffmpegPath, args, position, deadline.Token)
                    .WaitAsync(deadline.Token).ConfigureAwait(false);
            if (exitCode != 0 || temps.Any(t => !File.Exists(t) || new FileInfo(t).Length == 0))
            {
                diagnostics.Record(DiagnosticLevel.Warn, "Экспорт",
                    $"Общий FFmpeg для {indexes}: код {exitCode}; " +
                    "результат неполный, повтор по отдельным потокам." +
                    (lastError is null ? "" : $" Диагностика: {SafeFfmpegError(lastError)}"));
                return GroupOutcome.RetrySeparately;
            }
            for (var j = 0; j < group.Length; j++)
            {
                var index = group[j].Stream.Index!.Value;
                try
                {
                    var target = MoveWithoutOverwrite(temps[j], targets[j], reserved);
                    var bytes = new FileInfo(target).Length;
                    var message = $"Сохранён: {Path.GetFileName(target)}; {FormatElapsed(watch.Elapsed)}; {bytes:N0} байт";
                    results.Add(new(index, ExportState.Completed, message, target, watch.Elapsed, bytes));
                    progress?.Report(new(index, ExportState.Completed, message,
                        Math.Min(99, (processed + j + 1) * 100.0 / total), processed + j + 1, total));
                    diagnostics.Record(DiagnosticLevel.Info, "Экспорт",
                        $"Поток #{index} завершён; формат {group[j].Muxer}; " +
                        $"время {FormatElapsed(watch.Elapsed)}; размер {bytes:N0} байт; " +
                        $"Путь результата: {Path.GetFileName(target)}");
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    var message = $"Не удалось сохранить поток #{index}. Время: {FormatElapsed(watch.Elapsed)}.";
                    results.Add(new(index, ExportState.Failed, message, null, watch.Elapsed));
                    progress?.Report(new(index, ExportState.Failed, message, null, processed + j + 1, total));
                    diagnostics.Record(DiagnosticLevel.Error, "Экспорт",
                        $"Поток #{index}: ошибка переноса результата {ex.GetType().Name}.");
                }
            }
            completed = true;
            return GroupOutcome.Completed;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            var index = group[0].Stream.Index!.Value;
            results.Add(new(index, ExportState.Cancelled,
                $"Извлечение отменено через {FormatElapsed(watch.Elapsed)}.", null, watch.Elapsed));
            progress?.Report(new(index, ExportState.Cancelled, "Извлечение отменено.", null, processed, total));
            return GroupOutcome.Cancelled;
        }
        catch (OperationCanceledException)
        {
            for (var j = 0; j < group.Length; j++)
            {
                var plan = group[j];
                var index = plan.Stream.Index!.Value;
                var message = $"Превышено время ожидания FFmpeg ({FormatElapsed(watch.Elapsed)}).";
                results.Add(new(index, ExportState.Failed, message, null, watch.Elapsed));
                progress?.Report(new(index, ExportState.Failed, message, null, processed + j + 1, total));
            }
            diagnostics.Record(DiagnosticLevel.Error, "Экспорт",
                $"Общий FFmpeg для {indexes}: тайм-аут через {FormatElapsed(watch.Elapsed)}.");
            completed = true;
            return GroupOutcome.Completed;
        }
        catch (Exception ex)
        {
            diagnostics.Record(DiagnosticLevel.Warn, "Экспорт",
                $"Общий FFmpeg для {indexes}: {ex.GetType().Name}; повтор по отдельным потокам.");
            return GroupOutcome.RetrySeparately;
        }
        finally
        {
            diagnostics.Record(exitCode == 0 ? DiagnosticLevel.Info : DiagnosticLevel.Warn, "Экспорт",
                $"Общий FFmpeg для {indexes}: время {FormatElapsed(watch.Elapsed)}, " +
                $"код завершения {(exitCode?.ToString(CultureInfo.InvariantCulture) ?? "нет")}, " +
                $"прогресс {(lastPosition is null ? "не получен" : "получен")}.");
            foreach (var temp in temps)
                if (File.Exists(temp))
                    try { File.Delete(temp); }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    { diagnostics.Record(DiagnosticLevel.Warn, "Экспорт", "Не удалось удалить временный файл общего прохода."); }
            if (!completed)
                foreach (var target in targets) reserved.Remove(target);
        }
    }

    internal static string[] BuildGroupArguments(string input, IReadOnlyList<TrackExportPlan> group,
        IReadOnlyList<string> temps)
    {
        var args = new List<string> { "-hide_banner", "-loglevel", "error", "-nostdin",
            "-stats_period", "0.5", "-progress", "pipe:2", "-i", input };
        for (var i = 0; i < group.Count; i++)
            args.AddRange(["-map", $"0:{group[i].Stream.Index}", "-c", "copy",
                "-f", group[i].Muxer, temps[i]]);
        return args.ToArray();
    }

    internal static string FormatElapsed(TimeSpan time) =>
        $"{(long)time.TotalHours:00}:{time.Minutes:00}:{time.Seconds:00}";

    private static string SafeFfmpegError(string line)
    {
        // FFmpeg errors may include full input paths; record only a short generic hint.
        return line.Contains("Permission denied", StringComparison.OrdinalIgnoreCase)
            ? "отказ в доступе" : "подробности показаны в сообщении об ошибке";
    }

    internal static string[] BuildArguments(string input, int index, TrackExportPlan plan, string temp) =>
        plan.Attachment
            ? ["-hide_banner", "-loglevel", "error", "-nostdin", $"-dump_attachment:{index}", temp, "-i", input]
            : plan.StillImage
            ? ["-hide_banner", "-loglevel", "error", "-nostdin", "-stats_period", "0.5", "-progress", "pipe:2",
                "-i", input, "-map", $"0:{index}", "-c", "copy", "-frames:v", "1", "-update", "1",
                "-f", plan.Muxer, temp]
            : ["-hide_banner", "-loglevel", "error", "-nostdin", "-stats_period", "0.5", "-progress", "pipe:2",
                "-i", input, "-map", $"0:{index}", "-c", "copy", "-f", plan.Muxer, temp];

    internal static string BuildFileName(MediaInfo media, TrackExportPlan plan)
    {
        var stream = plan.Stream;
        var kind = stream.VisualKind switch
        {
            VisualKind.Video => "video", VisualKind.Image => "image",
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
