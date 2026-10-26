using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Reflection.PortableExecutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace TrackExtractor.Services;

public sealed partial class FfmpegTools
{
    // Provider linked by https://ffmpeg.org/download.html; release essentials includes both tools.
    internal static readonly Uri ArchiveUri = new("https://www.gyan.dev/ffmpeg/builds/ffmpeg-release-essentials.zip");
    internal static readonly Uri ChecksumUri = new(ArchiveUri.AbsoluteUri + ".sha256");
    private const long ArchiveLimit = 512L * 1024 * 1024;
    private const long ExpandedLimit = 1536L * 1024 * 1024;

    public async Task<InstallResult> DownloadWindowsAsync(IProgress<InstallProgress>? progress, CancellationToken cancellationToken)
    {
        if (!CanDirectDownload)
            return new(false, "Прямая загрузка доступна только для Windows 10/11 x64. Укажите совместимые утилиты вручную.");

        var toolsRoot = Path.Combine(_dataDirectory, "tools");
        var work = Path.Combine(toolsRoot, ".download-" + Guid.NewGuid().ToString("N"));
        string? installed = null;
        var committed = false;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromMinutes(20));
        var token = deadline.Token;
        try
        {
            token.ThrowIfCancellationRequested();
            EnsureNoLinks(toolsRoot);
            Directory.CreateDirectory(work);
            using var http = _httpHandler is null
                ? new HttpClient(new HttpClientHandler { AllowAutoRedirect = false })
                : new HttpClient(_httpHandler, disposeHandler: false);
            http.Timeout = Timeout.InfiniteTimeSpan;
            http.DefaultRequestHeaders.UserAgent.ParseAdd("TrackExtractor/1.0");

            progress?.Report(new("Получение контрольной суммы с gyan.dev…"));
            await using var hashBytes = new MemoryStream();
            await DownloadAsync(http, ChecksumUri, hashBytes, 4096, null, token);
            var checksum = Encoding.UTF8.GetString(hashBytes.ToArray()).Trim().TrimStart('\uFEFF');
            var hashMatch = Regex.Match(checksum, @"\A([0-9a-fA-F]{64})(?:\s+\*?[^\r\n]+)?\z");
            if (!hashMatch.Success) throw new InvalidDataException("Источник не предоставил корректную SHA-256. Файлы не будут запущены.");
            var expected = Convert.FromHexString(hashMatch.Groups[1].Value);

            var archivePath = Path.Combine(work, "ffmpeg.zip");
            await using (var archive = new FileStream(archivePath, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 81920, useAsync: true))
            {
                progress?.Report(new("Загрузка FFmpeg и FFprobe с gyan.dev…", 0));
                await DownloadAsync(http, ArchiveUri, archive, ArchiveLimit, progress, token);
                archive.Position = 0;
                progress?.Report(new("Проверка SHA-256…"));
                var actual = await SHA256.HashDataAsync(archive, token);
                if (!CryptographicOperations.FixedTimeEquals(expected, actual))
                    throw new InvalidDataException("Контрольная сумма SHA-256 не совпала. Архив удалён; повторите загрузку. Файлы не были запущены.");
            }

            var extracted = Path.Combine(work, "extracted");
            progress?.Report(new("Безопасная распаковка архива…"));
            await ExtractAsync(archivePath, extracted, token);
            var ffmpeg = FindDownloadedTool(extracted, "ffmpeg.exe");
            var ffprobe = FindDownloadedTool(extracted, "ffprobe.exe");
            if (!string.Equals(Path.GetDirectoryName(ffmpeg), Path.GetDirectoryName(ffprobe), StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Архив не содержит согласованную пару FFmpeg и FFprobe.");
            VerifyWindowsX64(ffmpeg);
            VerifyWindowsX64(ffprobe);

            // Each attempt has its own directory: existing working tools are never overwritten.
            installed = Path.Combine(toolsRoot, "ffmpeg-" + Guid.NewGuid().ToString("N"));
            Directory.Move(extracted, installed);
            ffmpeg = Path.Combine(installed, Path.GetRelativePath(extracted, ffmpeg));
            ffprobe = Path.Combine(installed, Path.GetRelativePath(extracted, ffprobe));
            progress?.Report(new("Проверка установленных FFmpeg и FFprobe…"));
            var ffmpegStatus = await CheckCandidateAsync("ffmpeg", ffmpeg, token);
            var ffprobeStatus = await CheckCandidateAsync("ffprobe", ffprobe, token);
            if (!ffmpegStatus.IsReady || !ffprobeStatus.IsReady)
                throw new InvalidDataException($"Установленные утилиты не прошли проверку: {ffmpegStatus.Details}; {ffprobeStatus.Details}");

            var existing = await CheckAsync(token);
            await SavePathsAsync(new(existing.FFmpeg.IsReady ? existing.FFmpeg.Path : ffmpeg,
                existing.FFprobe.IsReady ? existing.FFprobe.Path : ffprobe), token);
            committed = true;
            OfferDirectDownload = false;
            progress?.Report(new("FFmpeg и FFprobe проверены и готовы.", 100));
            return new(true, "FFmpeg и FFprobe установлены в пользовательский каталог, проверены и готовы. Пути сохранены.");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new(false, "Истекло время загрузки или установки (20 минут). Проверьте сеть и повторите попытку.");
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidDataException or UnauthorizedAccessException or CryptographicException)
        {
            return new(false, $"Не удалось установить FFmpeg: {ex.Message} Повторите попытку или укажите пути вручную.");
        }
        finally
        {
            DeleteInstallationDirectory(work, toolsRoot);
            if (!committed && installed is not null) DeleteInstallationDirectory(installed, toolsRoot);
        }
    }

    private static async Task DownloadAsync(HttpClient http, Uri uri, Stream destination, long limit,
        IProgress<InstallProgress>? progress, CancellationToken cancellationToken)
    {
        using var response = await GetTrustedResponseAsync(http, uri, cancellationToken);
        var length = response.Content.Headers.ContentLength;
        if (length is > 0 && length > limit) throw new InvalidDataException("Файл превышает допустимый размер.");
        await using var input = await response.Content.ReadAsStreamAsync(cancellationToken);
        var buffer = new byte[81920];
        long total = 0;
        var lastProgress = Environment.TickCount64;
        int count;
        while ((count = await input.ReadAsync(buffer, cancellationToken)) != 0)
        {
            total += count;
            if (total > limit) throw new InvalidDataException("Файл превышает допустимый размер.");
            await destination.WriteAsync(buffer.AsMemory(0, count), cancellationToken);
            if (Environment.TickCount64 - lastProgress >= 100)
            {
                progress?.Report(new($"Загружено {total / 1048576.0:F1} МБ",
                    length is > 0 ? total * 100.0 / length : null));
                lastProgress = Environment.TickCount64;
            }
        }
        if (total == 0 || (length.HasValue && total != length)) throw new InvalidDataException("Загрузка завершилась не полностью.");
        progress?.Report(new("Загрузка завершена.", 100));
    }

    private static async Task<HttpResponseMessage> GetTrustedResponseAsync(HttpClient http, Uri uri, CancellationToken cancellationToken)
    {
        for (var redirects = 0; redirects < 6; redirects++)
        {
            ValidateDownloadUri(uri);
            var response = await http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            // Also validate the effective URI (defence against an accidentally auto-redirecting handler).
            try { ValidateDownloadUri(response.RequestMessage?.RequestUri ?? uri); }
            catch { response.Dispose(); throw; }
            if (response.StatusCode is HttpStatusCode.MovedPermanently or HttpStatusCode.Redirect or
                HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect or HttpStatusCode.SeeOther)
            {
                var location = response.Headers.Location;
                response.Dispose();
                if (location is null) throw new HttpRequestException("Источник вернул перенаправление без адреса.");
                uri = new Uri(uri, location);
                continue;
            }
            try { response.EnsureSuccessStatusCode(); return response; }
            catch { response.Dispose(); throw; }
        }
        throw new HttpRequestException("Слишком много перенаправлений источника.");
    }

    private static void ValidateDownloadUri(Uri uri)
    {
        if (!uri.IsAbsoluteUri || uri.Scheme != Uri.UriSchemeHttps || uri.Host != "www.gyan.dev" ||
            !uri.IsDefaultPort || uri.UserInfo.Length != 0 || !uri.AbsolutePath.StartsWith("/ffmpeg/builds/", StringComparison.Ordinal))
            throw new InvalidDataException("Загрузка остановлена: адрес не принадлежит разрешённому HTTPS-источнику gyan.dev.");
    }

    private static async Task ExtractAsync(string archivePath, string destination, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(destination);
        using var archive = ZipFile.OpenRead(archivePath);
        if (archive.Entries.Count == 0 || archive.Entries.Count > 4096) throw new InvalidDataException("Недопустимое количество файлов в ZIP.");
        long expanded = 0;
        var root = Path.GetFullPath(destination) + Path.DirectorySeparatorChar;
        foreach (var entry in archive.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var name = entry.FullName.Replace('\\', '/');
            var segments = name.TrimEnd('/').Split('/');
            if (Path.IsPathRooted(name) || segments.Any(s => s is "" or "." or ".." || s.Contains(':') ||
                    s.EndsWith('.') || s.EndsWith(' ') || s.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) ||
                ((entry.ExternalAttributes >> 16) & 0xF000) == 0xA000 ||
                (entry.ExternalAttributes & (int)FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("Небезопасный путь или ссылка внутри ZIP.");
            var target = Path.GetFullPath(Path.Combine(destination, name));
            if (!target.StartsWith(root, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Путь ZIP выходит за каталог установки.");
            if (entry.Length > ExpandedLimit - expanded) throw new InvalidDataException("Распакованный архив слишком велик.");
            expanded += entry.Length;
            if (name.EndsWith('/')) { Directory.CreateDirectory(target); continue; }
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            await using var input = entry.Open();
            await using var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, useAsync: true);
            var buffer = new byte[81920];
            long written = 0;
            int count;
            while ((count = await input.ReadAsync(buffer, cancellationToken)) != 0)
            {
                written += count;
                if (written > entry.Length) throw new InvalidDataException("Размер файла ZIP не соответствует описанию.");
                await output.WriteAsync(buffer.AsMemory(0, count), cancellationToken);
            }
            if (written != entry.Length) throw new InvalidDataException("Файл ZIP повреждён.");
        }
    }

    private static string FindDownloadedTool(string directory, string name)
    {
        var matches = Directory.GetFiles(directory, name, SearchOption.AllDirectories);
        if (matches.Length != 1) throw new InvalidDataException($"В архиве должна быть ровно одна утилита {name}.");
        return matches[0];
    }

    private static void VerifyWindowsX64(string path)
    {
        using var file = File.OpenRead(path);
        try
        {
            using var pe = new PEReader(file);
            if (pe.PEHeaders.CoffHeader.Machine != Machine.Amd64 || pe.PEHeaders.PEHeader is null)
                throw new InvalidDataException("Архив содержит несовместимый исполняемый файл; требуется Windows x64.");
        }
        catch (BadImageFormatException ex) { throw new InvalidDataException("Некорректный исполняемый файл в архиве.", ex); }
    }

    private static void EnsureNoLinks(string path)
    {
        for (var directory = new DirectoryInfo(path); directory is not null; directory = directory.Parent)
            if (directory.Exists && (directory.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Каталог установки содержит ссылку. Выберите пути к утилитам вручную.");
    }

    private static void DeleteInstallationDirectory(string path, string root)
    {
        if (!Directory.Exists(path)) return;
        if (!Path.GetFullPath(path).StartsWith(Path.GetFullPath(root) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new IOException("Небезопасный путь очистки временных файлов.");
        EnsureNoLinks(path);
        Directory.Delete(path, recursive: true);
    }
}
