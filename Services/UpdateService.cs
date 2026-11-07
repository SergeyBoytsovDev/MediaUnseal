using System;
using System.IO;
using System.Collections.Generic;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using MediaUnseal.Services;

namespace MediaUnseal.Services;

internal enum UpdateCheckState { None, Checking, Current, Available, Failed }
internal enum UpdatePackageKind { WindowsInstaller, WindowsPortable, LinuxX64 }
internal sealed record UpdatePackage(string Name, long Size, string Sha256);
internal sealed record UpdateManifest(Version Version, string Tag, UpdatePackage WindowsInstaller,
    UpdatePackage WindowsPortable, UpdatePackage LinuxX64)
{
    internal UpdatePackage For(UpdatePackageKind kind) => kind switch
    {
        UpdatePackageKind.WindowsInstaller => WindowsInstaller,
        UpdatePackageKind.WindowsPortable => WindowsPortable,
        _ => LinuxX64
    };
}
internal sealed record UpdateCheckResult(UpdateCheckState State, UpdateManifest? Manifest = null,
    string? Error = null);

internal sealed class UpdateService
{
    internal static readonly Uri ManifestUri = new(
        "https://raw.githubusercontent.com/SergeyBoytsovDev/MediaUnseal/main/updates/stable.json");
    private const string ReleaseBase = "https://github.com/SergeyBoytsovDev/MediaUnseal/releases/download/";
    private static readonly HttpClient Client = new(new HttpClientHandler { AllowAutoRedirect = false })
    { Timeout = Timeout.InfiniteTimeSpan };
    private readonly SemaphoreSlim _operation = new(1, 1);
    private readonly Version _currentVersion;
    private UpdateCheckResult _result = new(UpdateCheckState.None);
    internal UpdateCheckResult Result => _result;
    internal event Action<UpdateCheckResult>? Changed;

    internal UpdateService(Version? currentVersion = null) =>
        _currentVersion = currentVersion ?? typeof(App).Assembly.GetName().Version ?? new Version(1, 0, 0);

    private void Set(UpdateCheckResult value) { _result = value; Changed?.Invoke(value); }

    internal async Task<UpdateCheckResult> CheckAsync(CancellationToken cancellationToken = default,
        Uri? manifestUri = null)
    {
        if (!await _operation.WaitAsync(0, cancellationToken)) return Result;
        try
        {
            Set(new UpdateCheckResult(UpdateCheckState.Checking));
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(TimeSpan.FromSeconds(1));
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, manifestUri ?? ManifestUri);
                using var response = await Client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead,
                    deadline.Token);
                response.EnsureSuccessStatusCode();
                if (response.Content.Headers.ContentLength is > 65536)
                    throw new InvalidDataException("Update metadata is too large.");
                await using var stream = await response.Content.ReadAsStreamAsync(deadline.Token);
                using var buffer = new MemoryStream();
                var block = new byte[8192];
                int read;
                while ((read = await stream.ReadAsync(block, deadline.Token)) != 0)
                {
                    if (buffer.Length + read > 65536) throw new InvalidDataException("Update metadata is too large.");
                    buffer.Write(block, 0, read);
                }
                var manifest = ParseManifest(buffer.ToArray());
                var state = manifest.Version > _currentVersion ? UpdateCheckState.Available : UpdateCheckState.Current;
                Set(new UpdateCheckResult(state, manifest));
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            { Set(new UpdateCheckResult(UpdateCheckState.Failed, Error: "Metadata check timed out after one second.")); }
            catch (Exception ex) when (ex is HttpRequestException or IOException or JsonException or KeyNotFoundException or InvalidOperationException or ArgumentException or FormatException or OverflowException
                or InvalidDataException or NotSupportedException)
            { Set(new UpdateCheckResult(UpdateCheckState.Failed, Error: ex.Message)); }
            return Result;
        }
        finally { _operation.Release(); }
    }

    internal static UpdateManifest ParseManifest(byte[] data)
    {
        using var json = JsonDocument.Parse(data);
        var root = json.RootElement;
        if (root.ValueKind != JsonValueKind.Object ||
            !TryVersion(root.GetProperty("version").GetString(), out var version) ||
            root.GetProperty("tag").GetString() != "v" + version.ToString(3))
            throw new InvalidDataException("Invalid update version or release tag.");
        var packages = root.GetProperty("packages");
        return new UpdateManifest(version, "v" + version.ToString(3),
            ReadPackage(packages.GetProperty("windowsInstaller"), version, "win-x64-setup.exe"),
            ReadPackage(packages.GetProperty("windowsPortable"), version, "win-x64-portable.zip"),
            ReadPackage(packages.GetProperty("linuxX64"), version, "linux-x64.tar.gz"));
    }

    internal static bool TryVersion(string? text, out Version version)
    {
        version = new Version(0, 0, 0);
        if (text is null || !Regex.IsMatch(text, @"^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)$") ||
            !Version.TryParse(text, out var parsed)) return false;
        version = parsed;
        return true;
    }

    private static UpdatePackage ReadPackage(JsonElement node, Version version, string suffix)
    {
        var name = node.GetProperty("name").GetString();
        var size = node.GetProperty("size").GetInt64();
        var sha = node.GetProperty("sha256").GetString();
        if (name != $"MediaUnseal-{version.ToString(3)}-{suffix}" || size <= 0 ||
            sha is null || !Regex.IsMatch(sha, "^[0-9a-fA-F]{64}$"))
            throw new InvalidDataException("Invalid update package metadata.");
        return new UpdatePackage(name, size, sha.ToUpperInvariant());
    }

    internal static Uri ReleaseUri(UpdateManifest manifest, UpdatePackageKind kind) =>
        new(ReleaseBase + Uri.EscapeDataString(manifest.Tag) + "/" +
            Uri.EscapeDataString(manifest.For(kind).Name));

    internal async Task<string> DownloadAsync(UpdateManifest manifest, UpdatePackageKind kind,
        IProgress<double>? progress, CancellationToken cancellationToken)
    {
        if (!await _operation.WaitAsync(0, cancellationToken))
            throw new InvalidOperationException("Another update operation is in progress.");
        var package = manifest.For(kind);
        var directory = Path.Combine(Path.GetTempPath(), "MediaUnseal-update-" + Guid.NewGuid().ToString("N"));
        var path = Path.Combine(directory, package.Name);
        try
        {
            Directory.CreateDirectory(directory);
            File.WriteAllText(Path.Combine(directory, TemporaryDirectory.Marker), string.Empty);
            using var request = new HttpRequestMessage(HttpMethod.Get, ReleaseUri(manifest, kind));
            using var response = await Client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            // GitHub release assets normally redirect to GitHub's asset CDN. Only this hop may leave github.com.
            if (response.StatusCode is System.Net.HttpStatusCode.Redirect or System.Net.HttpStatusCode.Found)
            {
                var location = response.Headers.Location;
                if (location is null || !location.IsAbsoluteUri || location.Scheme != Uri.UriSchemeHttps ||
                    location.Host is not ("release-assets.githubusercontent.com" or "objects.githubusercontent.com"))
                    throw new InvalidDataException("Untrusted release asset redirect.");
                using var redirected = await Client.GetAsync(location, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
                redirected.EnsureSuccessStatusCode();
                await CopyAndVerifyAsync(redirected.Content, package, path, progress, cancellationToken);
            }
            else
            {
                response.EnsureSuccessStatusCode();
                await CopyAndVerifyAsync(response.Content, package, path, progress, cancellationToken);
            }
            return path;
        }
        catch
        {
            try { Directory.Delete(directory, true); } catch (IOException) { }
            throw;
        }
        finally { _operation.Release(); }
    }

    internal static async Task CopyAndVerifyAsync(HttpContent content, UpdatePackage package, string path,
        IProgress<double>? progress, CancellationToken token)
    {
        if (content.Headers.ContentLength is long length && length != package.Size)
            throw new InvalidDataException("Update package size differs from metadata.");
        await using var source = await content.ReadAsStreamAsync(token);
        await using var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write,
            FileShare.None, 65536, FileOptions.Asynchronous);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[65536];
        long total = 0;
        int read;
        while ((read = await source.ReadAsync(buffer, token)) != 0)
        {
            total += read;
            if (total > package.Size) throw new InvalidDataException("Update package exceeds declared size.");
            hash.AppendData(buffer, 0, read);
            await output.WriteAsync(buffer.AsMemory(0, read), token);
            progress?.Report((double)total / package.Size);
        }
        await output.FlushAsync(token);
        if (total != package.Size || !Convert.ToHexString(hash.GetHashAndReset())
                .Equals(package.Sha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Update package size or SHA-256 verification failed.");
    }
}




