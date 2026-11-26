using System;
using System.Formats.Tar;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Net;
using System.Security.Authentication;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace MediaUnseal.Services;

internal sealed record TesseractPackage(string Version, Uri Url, long Size, string Sha256);
internal sealed class TesseractDownloadException(string stage, string endpoint, int attempts,
    bool interrupted, Exception cause) : IOException("Tesseract download failed.", cause)
{
    internal string Stage => stage;
    internal string Endpoint => endpoint;
    internal int Attempts => attempts;
    internal bool Interrupted => interrupted;
}

internal static class TesseractInstaller
{
    private const string ManifestUrl = "https://raw.githubusercontent.com/SergeyBoytsovDev/MediaUnseal/main/dependencies/tesseract/manifest.json";
    private const long MaxExpandedBytes = 650_000_000;
    private static readonly HttpClient Client = new(new HttpClientHandler
    {
        AllowAutoRedirect = true,
        UseProxy = true
    }) { Timeout = TimeSpan.FromMinutes(5) };
    internal static string InstalledRoot => Path.Combine(SubtitleOcr.ManagedRoot, "current");

    internal static async Task<(TesseractPackage Package, bool Online)> CheckAsync(CancellationToken token)
    {
        byte[] data;
        var online = false;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(TimeSpan.FromSeconds(3));
            data = await Client.GetByteArrayAsync(ManifestUrl, timeout.Token);
            online = true;
        }
        catch (Exception ex) when (!token.IsCancellationRequested && ex is HttpRequestException
            or TaskCanceledException or IOException)
        {
            using var resource = Assembly.GetExecutingAssembly().GetManifestResourceStream(
                "MediaUnseal.TesseractManifest.json") ?? throw new InvalidDataException("Embedded OCR manifest is missing.");
            using var buffer = new MemoryStream();
            await resource.CopyToAsync(buffer, token);
            data = buffer.ToArray();
        }
        var package = Parse(data);
        if (!online)
        {
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
                timeout.CancelAfter(TimeSpan.FromSeconds(3));
                using var request = new HttpRequestMessage(HttpMethod.Head, package.Url);
                using var response = await Client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead,
                    timeout.Token);
                online = response.IsSuccessStatusCode;
            }
            catch (Exception ex) when (!token.IsCancellationRequested && ex is HttpRequestException
                or TaskCanceledException or IOException) { }
        }
        return (package, online);
    }

    internal static TesseractPackage Parse(ReadOnlySpan<byte> bytes)
    {
        using var document = JsonDocument.Parse(bytes.ToArray());
        var root = document.RootElement;
        if (root.GetProperty("schemaVersion").GetInt32() != 1) throw new InvalidDataException("Unsupported OCR manifest schema.");
        var version = root.GetProperty("version").GetString() ?? "";
        if (!Version.TryParse(version, out var parsed) || parsed.Major != 5)
            throw new InvalidDataException("Invalid Tesseract version.");
        var tag = root.GetProperty("releaseTag").GetString();
        if (tag != "tesseract-v" + version) throw new InvalidDataException("Invalid OCR release tag.");
        var languages = root.GetProperty("models").EnumerateArray().Select(item => item.GetString()).ToArray();
        var profiles = root.GetProperty("profiles").EnumerateArray().Select(item => item.GetString()).ToArray();
        if (!languages.SequenceEqual(SubtitleOcr.Languages, StringComparer.Ordinal)
            || !profiles.SequenceEqual(new[] { "tessdata_fast", "tessdata_best" }, StringComparer.Ordinal))
            throw new InvalidDataException("Unexpected OCR model set.");
        var platform = OperatingSystem.IsWindows() ? "windows-x64" : OperatingSystem.IsLinux() ? "linux-x64" : "";
        if (platform == "" || System.Runtime.InteropServices.RuntimeInformation.OSArchitecture
            != System.Runtime.InteropServices.Architecture.X64)
            throw new PlatformNotSupportedException("OCR packages require Windows or Linux x64.");
        var package = root.GetProperty("packages").GetProperty(platform);
        var name = package.GetProperty("name").GetString();
        var expected = $"tesseract-{version}-{platform}." + (platform == "windows-x64" ? "zip" : "tar.gz");
        var url = package.GetProperty("url").GetString();
        var canonical = $"https://github.com/SergeyBoytsovDev/MediaUnseal/releases/download/{tag}/{expected}";
        var size = package.GetProperty("size").GetInt64();
        var hash = package.GetProperty("sha256").GetString() ?? "";
        if (name != expected || url != canonical || size is < 1 or > 500_000_000
            || hash.Length != 64 || !hash.All(Uri.IsHexDigit))
            throw new InvalidDataException("Invalid OCR package metadata.");
        return new(version, new Uri(canonical), size, hash);
    }

    internal static async Task InstallAsync(TesseractPackage package, IProgress<double>? progress,
        CancellationToken token, string? destinationRoot = null, Action<int, string>? retry = null)
    {
        var root = destinationRoot ?? SubtitleOcr.ManagedRoot;
        var current = Path.Combine(root, "current");
        Directory.CreateDirectory(root);
        var archive = Path.Combine(root, "download-" + Guid.NewGuid().ToString("N"));
        var stage = Path.Combine(root, "stage-" + Guid.NewGuid().ToString("N"));
        var backup = Path.Combine(root, "backup-" + Guid.NewGuid().ToString("N"));
        var oldMoved = false;
        var newMoved = false;
        try
        {
            await DownloadVerifiedAsync(package, archive, progress, token, retry);
            Directory.CreateDirectory(stage);
            ExtractArchive(archive, stage, token);
            ValidateStage(stage);
            var executable = Path.Combine(stage, "bin", OperatingSystem.IsWindows() ? "tesseract.exe" : "tesseract");
            var checkedEngine = await SubtitleOcr.DiscoverAsync(executable, stage, token);
            if (checkedEngine is null || checkedEngine.Executable != executable
                || checkedEngine.FastLanguages.Count != 10 || checkedEngine.BestLanguages.Count != 10)
                throw new InvalidDataException("The extracted OCR engine or its language models failed validation.");
            token.ThrowIfCancellationRequested();
            if (Directory.Exists(current)) { Directory.Move(current, backup); oldMoved = true; }
            try { Directory.Move(stage, current); newMoved = true; }
            catch
            {
                if (oldMoved) Directory.Move(backup, current);
                oldMoved = false;
                throw;
            }
            try { if (destinationRoot is null) OcrPathSelection.Clear(); }
            catch
            {
                Directory.Move(current, stage);
                newMoved = false;
                if (oldMoved) { Directory.Move(backup, current); oldMoved = false; }
                throw;
            }
        }
        finally
        {
            if (!newMoved && oldMoved && !Directory.Exists(current)) Directory.Move(backup, current);
            TryDelete(archive);
            TryDelete(stage);
            if (newMoved) TryDelete(backup);
        }
    }

    internal static async Task DownloadVerifiedAsync(TesseractPackage package, string archive,
        IProgress<double>? progress, CancellationToken token, Action<int, string>? retry,
        HttpClient? httpClient = null)
    {
        var client = httpClient ?? Client;
        for (var attempt = 1; attempt <= 3; attempt++)
        {
            token.ThrowIfCancellationRequested();
            var stage = "request";
            var endpoint = SafeEndpoint(package.Url);
            try
            {
                using var response = await client.GetAsync(package.Url, HttpCompletionOption.ResponseHeadersRead, token);
                endpoint = SafeEndpoint(response.RequestMessage?.RequestUri ?? package.Url);
                response.EnsureSuccessStatusCode();
                stage = "response stream";
                await using var source = await response.Content.ReadAsStreamAsync(token);
                await using var target = new FileStream(archive, FileMode.Create, FileAccess.Write, FileShare.None,
                    131072, FileOptions.Asynchronous);
                using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                var buffer = new byte[131072];
                long total = 0;
                int count;
                while (true)
                {
                    stage = "network read";
                    count = await source.ReadAsync(buffer, token);
                    if (count == 0) break;
                    total += count;
                    if (total > package.Size) throw new InvalidDataException("OCR download exceeds manifest size.");
                    hash.AppendData(buffer, 0, count);
                    stage = "local write";
                    await target.WriteAsync(buffer.AsMemory(0, count), token);
                    progress?.Report(total * 100.0 / package.Size);
                }
                if (total != package.Size) throw new IOException("OCR response ended before the expected archive size.");
                if (!hash.GetHashAndReset().AsSpan().SequenceEqual(Convert.FromHexString(package.Sha256)))
                    throw new InvalidDataException("OCR download SHA-256 mismatch.");
                return;
            }
            catch (Exception ex) when (!token.IsCancellationRequested && IsTransient(ex, stage))
            {
                if (attempt == 3)
                    throw new TesseractDownloadException(stage, endpoint, attempt,
                        ex is IOException || ex.InnerException is IOException, ex);
                retry?.Invoke(attempt, $"stage={stage}; endpoint={endpoint}; {ex.GetType().Name}: {ex.Message}");
                progress?.Report(0);
                await Task.Delay(TimeSpan.FromMilliseconds(350 * attempt), token);
            }
        }
    }

    private static bool IsTransient(Exception ex, string stage)
    {
        if (ex is AuthenticationException || ex.InnerException is AuthenticationException)
            return ex.InnerException is IOException;
        if (ex is HttpRequestException http)
            return http.StatusCode is null or HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests
                or HttpStatusCode.BadGateway or HttpStatusCode.ServiceUnavailable or HttpStatusCode.GatewayTimeout
                || (int?)http.StatusCode >= 500;
        return ex is TaskCanceledException || ex is IOException && stage is "network read" or "response stream";
    }

    private static string SafeEndpoint(Uri uri) => uri.GetLeftPart(UriPartial.Path);

    private static string Destination(string root, string name)
    {
        name = name.Replace('\\', '/');
        if (name.StartsWith("./", StringComparison.Ordinal)) name = name[2..];
        if (name.StartsWith('/') || name.Contains(':') || name.Split('/').Any(p => p is "" or "." or ".."))
            throw new InvalidDataException("Unsafe OCR archive path.");
        var parts = name.Split('/');
        if (parts[0] is not ("bin" or "lib" or "licenses" or "tessdata_fast" or "tessdata_best"))
            throw new InvalidDataException("Unexpected OCR archive entry.");
        var path = Path.GetFullPath(Path.Combine(root, Path.Combine(parts)));
        if (!path.StartsWith(Path.GetFullPath(root) + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            throw new InvalidDataException("OCR archive entry escapes staging directory.");
        return path;
    }

    internal static void ExtractArchive(string archive, string root, CancellationToken token)
    {
        if (OperatingSystem.IsWindows()) ExtractZip(archive, root, token);
        else ExtractTar(archive, root, token);
    }

    private static void ExtractZip(string archive, string root, CancellationToken token)
    {
        using var zip = ZipFile.OpenRead(archive);
        long total = 0;
        foreach (var entry in zip.Entries)
        {
            token.ThrowIfCancellationRequested();
            if (entry.FullName.EndsWith('/') || entry.FullName.EndsWith('\\')) continue;
            var type = (entry.ExternalAttributes >> 16) & 0xF000;
            if (type == 0xA000) throw new InvalidDataException("OCR archive contains a link.");
            total += entry.Length;
            if (total > MaxExpandedBytes) throw new InvalidDataException("OCR archive is too large.");
            var destination = Destination(root, entry.FullName);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            entry.ExtractToFile(destination);
        }
    }

    private static void ExtractTar(string archive, string root, CancellationToken token)
    {
        using var file = File.OpenRead(archive);
        using var gzip = new GZipStream(file, CompressionMode.Decompress);
        using var tar = new TarReader(gzip);
        long total = 0;
        TarEntry? entry;
        while ((entry = tar.GetNextEntry()) is not null)
        {
            token.ThrowIfCancellationRequested();
            if (entry.EntryType == TarEntryType.Directory) continue;
            if (entry.EntryType is not (TarEntryType.RegularFile or TarEntryType.V7RegularFile))
                throw new InvalidDataException("OCR archive contains an unsupported entry.");
            total += entry.Length;
            if (total > MaxExpandedBytes) throw new InvalidDataException("OCR archive is too large.");
            var destination = Destination(root, entry.Name);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write);
            entry.DataStream!.CopyTo(output);
            if (OperatingSystem.IsLinux() && entry is PosixTarEntry posix)
                File.SetUnixFileMode(destination, posix.Mode);
        }
    }

    private static void ValidateStage(string stage)
    {
        var executable = Path.Combine(stage, "bin", OperatingSystem.IsWindows() ? "tesseract.exe" : "tesseract");
        if (!File.Exists(executable) || !File.Exists(Path.Combine(stage, "licenses", "Tesseract-LICENSE.txt")))
            throw new InvalidDataException("OCR executable or license is missing.");
        foreach (var profile in new[] { "tessdata_fast", "tessdata_best" })
            foreach (var language in SubtitleOcr.Languages)
                if (!File.Exists(Path.Combine(stage, profile, language + ".traineddata")))
                    throw new InvalidDataException($"OCR model {profile}/{language} is missing.");
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); else if (Directory.Exists(path)) Directory.Delete(path, true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
