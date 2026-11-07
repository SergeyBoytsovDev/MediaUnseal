using System.Diagnostics;
using System.Formats.Tar;
using System.IO.Compression;
using System.Reflection;
using System.Security.Cryptography;

if (args.Length != 8 || args[0] != "--apply" ||
    !Enum.TryParse<PackageKind>(args[1], out var kind) ||
    !int.TryParse(args[2], out var parentPid) ||
    !Version.TryParse(args[5], out var version) ||
    !long.TryParse(args[7], out var expectedSize))
    return 2;
var packagePath = Path.GetFullPath(args[3]);
var target = Path.GetFullPath(args[4]);
var expectedHash = args[6];
using var updateOwner = new FileStream(Path.Combine(Path.GetDirectoryName(packagePath)!, ".mediaunseal-owner"),
    FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
var expectedName = kind switch
{
    PackageKind.WindowsInstaller => $"MediaUnseal-{version.ToString(3)}-win-x64-setup.exe",
    PackageKind.WindowsPortable => $"MediaUnseal-{version.ToString(3)}-win-x64-portable.zip",
    _ => $"MediaUnseal-{version.ToString(3)}-linux-x64.tar.gz"
};

try
{
    using var parent = Process.GetProcessById(parentPid);
    if (!parent.WaitForExit(120000)) return 4;
}
catch (ArgumentException) { } // The app has already exited.

try
{
    if (Path.GetFileName(packagePath) != expectedName || expectedSize <= 0 ||
        expectedHash.Length != 64 || !expectedHash.All(Uri.IsHexDigit) ||
        new FileInfo(packagePath).Length != expectedSize ||
        !HashFile(packagePath).Equals(expectedHash, StringComparison.OrdinalIgnoreCase))
        throw new InvalidDataException("Package metadata verification failed.");
}
catch (Exception ex)
{
    WriteFailure("Package verification failed: " + ex.GetType().Name + "; HResult 0x" +
        ex.HResult.ToString("X8"));
    RestartOriginal(target);
    return 3;
}

if (kind == PackageKind.WindowsInstaller)
{
    try
    {
        using var setup = Process.Start(new ProcessStartInfo(packagePath)
        {
            UseShellExecute = true,
            ArgumentList = { "/SILENT", "/NORESTART", "/CLOSEAPPLICATIONS" }
        }) ?? throw new IOException("Could not start installer.");
        await setup.WaitForExitAsync();
        if (setup.ExitCode == 0)
        {
            RestartOriginal(target);
            return 0;
        }
        WriteFailure("Installer exit code " + setup.ExitCode);
    }
    catch (Exception ex)
    {
        WriteFailure("Installer failed: " + ex.GetType().Name + "; HResult 0x" +
            ex.HResult.ToString("X8"));
    }
    RestartOriginal(target);
    return 6;
}

var work = Path.Combine(Path.GetDirectoryName(packagePath)!, "staged");
var backup = Path.Combine(Path.GetDirectoryName(packagePath)!, "backup");
try
{
    Directory.CreateDirectory(work);
    var prefix = kind == PackageKind.WindowsPortable
        ? $"MediaUnseal-{version.ToString(3)}-win-x64-portable/"
        : $"MediaUnseal-{version.ToString(3)}-linux-x64/";
    if (kind == PackageKind.WindowsPortable)
    {
        using var archive = ZipFile.OpenRead(packagePath);
        foreach (var entry in archive.Entries)
        {
            if (entry.FullName.EndsWith('/')) continue;
            if ((entry.ExternalAttributes >> 16 & 0xF000) == 0xA000)
                throw new InvalidDataException("Archive contains a symbolic link.");
            var destination = StagedPath(work, entry.FullName, prefix);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            entry.ExtractToFile(destination);
        }
    }
    else
    {
        await using var source = File.OpenRead(packagePath);
        await using var gzip = new GZipStream(source, CompressionMode.Decompress);
        using var archive = new TarReader(gzip);
        TarEntry? entry;
        while ((entry = archive.GetNextEntry()) is not null)
        {
            if (entry.EntryType is TarEntryType.Directory) continue;
            if (entry.EntryType is not TarEntryType.RegularFile and not TarEntryType.V7RegularFile)
                throw new InvalidDataException("Archive contains a non-regular entry.");
            var destination = StagedPath(work, entry.Name, prefix);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            await using var output = File.Create(destination);
            if (entry.DataStream is not null) await entry.DataStream.CopyToAsync(output);
            if (OperatingSystem.IsLinux()) File.SetUnixFileMode(destination, entry.Mode | UnixFileMode.UserWrite);
        }
        var apphost = Path.Combine(work, "MediaUnseal");
        if (OperatingSystem.IsLinux()) File.SetUnixFileMode(apphost, UnixFileMode.UserRead | UnixFileMode.UserWrite |
            UnixFileMode.UserExecute | UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
            UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
    }

    var assembly = AssemblyName.GetAssemblyName(Path.Combine(work, "MediaUnseal.dll"));
    if (assembly.Name != "MediaUnseal" || assembly.Version?.ToString(3) != version.ToString(3))
        throw new InvalidDataException("Package platform or version does not match metadata.");
    var apphostName = kind == PackageKind.WindowsPortable ? "MediaUnseal.exe" : "MediaUnseal";
    if (!File.Exists(Path.Combine(work, apphostName)))
        throw new InvalidDataException("Application executable is missing.");

    var files = Directory.EnumerateFiles(work, "*", SearchOption.AllDirectories).ToArray();
    var replaced = new List<(string Destination, string? Backup)>();
    try
    {
        foreach (var file in files)
        {
            var relative = Path.GetRelativePath(work, file);
            var destination = Path.GetFullPath(Path.Combine(target, relative));
            if (!destination.StartsWith(target.TrimEnd(Path.DirectorySeparatorChar) +
                    Path.DirectorySeparatorChar, OperatingSystem.IsWindows()
                    ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
                throw new InvalidDataException("Archive path escapes the installation directory.");
            RejectLinks(target, destination);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            string? original = null;
            if (File.Exists(destination))
            {
                original = Path.Combine(backup, relative);
                Directory.CreateDirectory(Path.GetDirectoryName(original)!);
                File.Copy(destination, original);
            }
            replaced.Add((destination, original));
            File.Copy(file, destination, true);
            if (OperatingSystem.IsLinux())
                File.SetUnixFileMode(destination, File.GetUnixFileMode(file));
        }
        if (Environment.GetEnvironmentVariable("MEDIA_UNSEAL_UPDATE_TEST_NO_RESTART") != "1" &&
            Process.Start(new ProcessStartInfo(Path.Combine(target, apphostName))
            { UseShellExecute = true, WorkingDirectory = target }) is null)
            throw new IOException("Could not restart the updated application.");
    }
    catch
    {
        for (var i = replaced.Count - 1; i >= 0; i--)
        {
            var (destination, original) = replaced[i];
            try
            {
                if (original is null) File.Delete(destination);
                else File.Copy(original, destination, true);
            }
            catch (Exception ex)
            {
                try { File.AppendAllText(Path.Combine(backup, "rollback-errors.txt"),
                    ex.GetType().Name + ": HResult 0x" + ex.HResult.ToString("X8") + Environment.NewLine); }
                catch (Exception) { }
            }
        }
        throw;
    }
    return 0;
}
catch (Exception ex)
{
    try { File.WriteAllText(Path.Combine(Path.GetDirectoryName(packagePath)!, "update-error.txt"),
        ex.ToString()); } catch (IOException) { }
    return 7;
}

static void WriteFailure(string reason)
{
    try
    {
        var root = OperatingSystem.IsWindows()
            ? Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData)
            : Environment.GetEnvironmentVariable("XDG_CONFIG_HOME") is { Length: > 0 } xdg &&
              Path.IsPathRooted(xdg) ? xdg
                : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config");
        var current = Path.Combine(root, "MediaUnseal");
        var previous = Path.Combine(root, "TrackExtractor");
        var directory = File.Exists(Path.Combine(current, "ui.json")) ? current
            : File.Exists(Path.Combine(previous, "ui.json")) ? previous : current;
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "update-result.txt"), reason);
    }
    catch (IOException) { }
}

static void RestartOriginal(string target)
{
    try
    {
        var executable = Path.Combine(target, OperatingSystem.IsWindows() ? "MediaUnseal.exe" : "MediaUnseal");
        if (File.Exists(executable))
            Process.Start(new ProcessStartInfo(executable) { UseShellExecute = true, WorkingDirectory = target });
    }
    catch (Exception) { }
}
static string HashFile(string path)
{
    using var stream = File.OpenRead(path);
    return Convert.ToHexString(SHA256.HashData(stream));
}

static string StagedPath(string stage, string name, string prefix)
{
    if (!name.StartsWith(prefix, StringComparison.Ordinal) || name.Contains('\\') ||
        name.Contains(':') || name.Split('/').Any(part => part is "." or ".."))
        throw new InvalidDataException("Unsafe archive path.");
    var relative = name[prefix.Length..];
    if (relative.Length == 0 || Path.IsPathRooted(relative))
        throw new InvalidDataException("Unsafe archive path.");
    var result = Path.GetFullPath(Path.Combine(stage, relative));
    if (!result.StartsWith(stage + Path.DirectorySeparatorChar,
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
        throw new InvalidDataException("Archive path escapes staging.");
    return result;
}

static void RejectLinks(string root, string destination)
{
    var current = Path.GetDirectoryName(destination);
    while (current is not null && current.Length >= root.Length)
    {
        if (File.Exists(current) || Directory.Exists(current))
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Installation contains a link or reparse point.");
        if (current == root) break;
        current = Path.GetDirectoryName(current);
    }
    if (File.Exists(destination) &&
        (File.GetAttributes(destination) & FileAttributes.ReparsePoint) != 0)
        throw new IOException("Installation contains a linked file.");
}
enum PackageKind { WindowsInstaller, WindowsPortable, LinuxX64 }










