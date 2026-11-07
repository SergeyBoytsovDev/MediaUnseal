using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32;

namespace MediaUnseal.Services;

internal static class UpdateInstaller
{
    private static int _launching;
    private const string AppId = "{D8AF4D2A-89B2-4D64-9019-5C36A41E6E48}_is1";

    internal static UpdatePackageKind DetectCurrentPackage()
    {
        if (RuntimeInformation.ProcessArchitecture != Architecture.X64)
            throw new PlatformNotSupportedException("Updates are available only for x64 packages.");
        if (OperatingSystem.IsLinux()) return UpdatePackageKind.LinuxX64;
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        using var key = Registry.CurrentUser.OpenSubKey(
            @"Software\Microsoft\Windows\CurrentVersion\Uninstall\" + AppId);
        var installed = key?.GetValue("InstallLocation") as string
            ?? key?.GetValue("Inno Setup: App Path") as string;
        if (installed is not null &&
            Path.GetFullPath(installed).TrimEnd(Path.DirectorySeparatorChar)
                .Equals(Path.GetFullPath(AppContext.BaseDirectory).TrimEnd(Path.DirectorySeparatorChar),
                    StringComparison.OrdinalIgnoreCase))
            return UpdatePackageKind.WindowsInstaller;
        return UpdatePackageKind.WindowsPortable;
    }

    internal static bool CanReplaceCurrentInstallation()
    {
        var directory = Path.GetFullPath(AppContext.BaseDirectory);
        if (OperatingSystem.IsLinux() &&
            (directory.StartsWith("/usr/", StringComparison.Ordinal) ||
             directory.StartsWith("/opt/", StringComparison.Ordinal) ||
             directory.StartsWith("/snap/", StringComparison.Ordinal) ||
             directory.StartsWith("/var/lib/", StringComparison.Ordinal) ||
             directory.StartsWith("/app/", StringComparison.Ordinal) ||
             directory.StartsWith("/nix/store/", StringComparison.Ordinal) ||
             directory.Contains("/.local/share/flatpak/", StringComparison.Ordinal) ||
             Environment.GetEnvironmentVariable("FLATPAK_ID") is not null ||
             Environment.GetEnvironmentVariable("SNAP") is not null))
            return false;
        try
        {
            var probe = Path.Combine(directory, ".mediaunseal-update-" + Guid.NewGuid().ToString("N"));
            using (new FileStream(probe, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                1, FileOptions.DeleteOnClose)) { }
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
    }

    internal static string? ConsumePreviousFailure()
    {
        var root = OperatingSystem.IsWindows()
            ? Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData)
            : Environment.GetEnvironmentVariable("XDG_CONFIG_HOME") is { Length: > 0 } xdg &&
              Path.IsPathRooted(xdg) ? xdg
                : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config");
        var directory = AppDirectories.Resolve(root, "ui.json");
        var result = Path.Combine(directory, "update-result.txt");
        try
        {
            if (!File.Exists(result)) return null;
            var message = File.ReadAllText(result);
            File.Delete(result);
            return message;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { return "Could not read update result: " + ex.GetType().Name; }
    }
    internal static Task LaunchAsync(string packagePath, UpdateManifest manifest,
        UpdatePackageKind kind, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (Interlocked.Exchange(ref _launching, 1) != 0)
            throw new InvalidOperationException("An update is already being installed.");
        try
        {
            if (kind != UpdatePackageKind.WindowsInstaller && !CanReplaceCurrentInstallation())
                throw new UnauthorizedAccessException("Application directory is not writable.");
            var helper = Path.Combine(AppContext.BaseDirectory,
                OperatingSystem.IsWindows() ? "MediaUnseal.Updater.exe" : "MediaUnseal.Updater");
            if (!File.Exists(helper)) throw new FileNotFoundException("Update helper is missing.", helper);
            var temporary = Path.Combine(Path.GetDirectoryName(packagePath)!, Path.GetFileName(helper));
            File.Copy(helper, temporary, true);
            if (OperatingSystem.IsLinux())
                File.SetUnixFileMode(temporary, UnixFileMode.UserRead | UnixFileMode.UserWrite |
                    UnixFileMode.UserExecute);
            var info = new ProcessStartInfo(temporary)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = Path.GetDirectoryName(packagePath)!
            };
            info.ArgumentList.Add("--apply");
            info.ArgumentList.Add(kind.ToString());
            info.ArgumentList.Add(Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture));
            info.ArgumentList.Add(Path.GetFullPath(packagePath));
            info.ArgumentList.Add(Path.GetFullPath(AppContext.BaseDirectory));
            info.ArgumentList.Add(manifest.Version.ToString(3));
            info.ArgumentList.Add(manifest.For(kind).Sha256);
            info.ArgumentList.Add(manifest.For(kind).Size.ToString(System.Globalization.CultureInfo.InvariantCulture));
            if (Process.Start(info) is null) throw new IOException("Could not start update helper.");
            return Task.CompletedTask;
        }
        catch
        {
            Interlocked.Exchange(ref _launching, 0);
            throw;
        }
    }
}






