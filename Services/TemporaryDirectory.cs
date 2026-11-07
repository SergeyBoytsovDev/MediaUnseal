using System;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace MediaUnseal.Services;

// Only directories created with this marker may be removed after a crash.
internal sealed class TemporaryDirectory : IDisposable
{
    internal const string Marker = ".mediaunseal-owner";
    private readonly FileStream _owner;
    internal string Path { get; }

    private TemporaryDirectory(string path, FileStream owner)
    { Path = path; _owner = owner; }

    internal static TemporaryDirectory Create(string root, string prefix)
    {
        var path = System.IO.Path.Combine(root, prefix + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        try
        {
            var owner = new FileStream(System.IO.Path.Combine(path, Marker), FileMode.CreateNew,
                FileAccess.ReadWrite, FileShare.None);
            return new(path, owner);
        }
        catch
        {
            try { Directory.Delete(path); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            throw;
        }
    }

    public void Dispose() => _owner.Dispose();

    internal static async Task<bool> DeleteAsync(string path, Action<string>? report)
    {
        for (var attempt = 0; attempt < 6; attempt++)
        {
            if (!Directory.Exists(path)) return true;
            try
            {
                if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                    throw new IOException("Temporary directory is a link.");
                Directory.Delete(path, recursive: true);
                return true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Recursive deletion may have removed the marker before hitting a locked file.
                // Keep the remainder recognizable for the next launch.
                try
                {
                    if (Directory.Exists(path) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) == 0)
                    {
                        var marker = System.IO.Path.Combine(path, Marker);
                        if (!File.Exists(marker))
                            using (new FileStream(marker, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { }
                    }
                }
                catch (Exception restoreError) when (restoreError is IOException or UnauthorizedAccessException)
                { report?.Invoke($"Temporary directory marker restoration failed: {restoreError.GetType().Name}; " +
                    $"HResult 0x{restoreError.HResult:X8}."); }
                if (attempt == 5)
                {
                    report?.Invoke($"Temporary directory cleanup failed: {ex.GetType().Name}; HResult 0x{ex.HResult:X8}.");
                    return false;
                }
                await Task.Delay(100 * (attempt + 1)).ConfigureAwait(false);
            }
        }
        return false;
    }

    internal static async Task RecoverAsync(string root, string prefix, Action<string>? report,
        Func<string, bool>? retain = null)
    {
        if (!Directory.Exists(root)) return;
        try
        {
            foreach (var path in Directory.EnumerateDirectories(root, prefix + "*", SearchOption.TopDirectoryOnly))
            {
                var name = System.IO.Path.GetFileName(path);
                if (!Regex.IsMatch(name, "\\A" + Regex.Escape(prefix) + "[0-9a-f]{32}\\z")) continue;
                if (retain?.Invoke(path) == true) continue;
                if (!Directory.Exists(path)) continue;
                try { if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) continue; }
                catch (FileNotFoundException) { continue; }
                var marker = System.IO.Path.Combine(path, Marker);
                if (!File.Exists(marker)) continue;
                try
                {
                    using (new FileStream(marker, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) { }
                }
                catch (IOException) { continue; } // An active operation still owns this directory.
                await DeleteAsync(path, report).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            report?.Invoke($"Temporary directory recovery failed: {ex.GetType().Name}; HResult 0x{ex.HResult:X8}.");
        }
    }

    internal static async Task DeleteFileAsync(string path, Action<string>? report)
    {
        for (var attempt = 0; attempt < 6; attempt++)
        {
            try { File.Delete(path); return; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
            {
                if (attempt == 5)
                {
                    report?.Invoke($"Temporary settings file cleanup failed: {ex.GetType().Name}; HResult 0x{ex.HResult:X8}.");
                    return;
                }
                await Task.Delay(100 * (attempt + 1)).ConfigureAwait(false);
            }
        }
    }

    internal static async Task RecoverFilesAsync(string root, string prefix, Action<string>? report)
    {
        if (!Directory.Exists(root)) return;
        try
        {
            foreach (var path in Directory.EnumerateFiles(root, prefix + "*.tmp", SearchOption.TopDirectoryOnly))
            {
                var name = System.IO.Path.GetFileName(path);
                if (!Regex.IsMatch(name, "\\A" + Regex.Escape(prefix) + "[0-9a-f]{32}\\.tmp\\z")) continue;
                // A concurrent save may still be writing a newly created file.
                if (File.GetLastWriteTimeUtc(path) > DateTime.UtcNow.AddMinutes(-1)) continue;
                await DeleteFileAsync(path, report).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        { report?.Invoke($"Temporary settings file recovery failed: {ex.GetType().Name}; HResult 0x{ex.HResult:X8}."); }
    }
}
