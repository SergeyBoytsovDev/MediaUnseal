using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace MediaUnseal.Services;

public sealed record ToolStatus(string Name, bool IsReady, string Details, string? Path = null, bool IsMissing = false)
{
    internal LocalizedMessage? LocalizedDetails { get; init; }
}

public sealed record ToolsStatus(ToolStatus FFmpeg, ToolStatus FFprobe)
{
    public bool IsReady => FFmpeg.IsReady && FFprobe.IsReady;
}

public sealed record InstallResult(bool Success, string Message)
{
    internal LocalizedMessage? LocalizedMessage { get; init; }
}
public sealed record InstallProgress(string Message, double? Percent = null)
{
    internal LocalizedMessage? LocalizedMessage { get; init; }
}

public sealed partial class FfmpegTools
{
    public enum RepairSource { None, WinGet, Local }
    public RepairSource RequiredRepair { get; private set; }
    private static LocalizationManager L => LocalizationManager.Active;
    private readonly string _settingsPath;
    private readonly string _appDirectory;
    private readonly string? _searchPathOverride;
    private readonly string _dataDirectory;
    private readonly HttpMessageHandler? _httpHandler;
    internal Func<string, string[], TimeSpan, CancellationToken, Task<ProcessResult>>? ProcessRunner { get; init; }
    internal Func<Uri, CancellationToken, Task<bool>>? ProbeRunner { get; init; }
    internal Func<bool>? NetworkAvailable { get; init; }
    public bool OfferDirectDownload { get; private set; }
    public bool? WinGetAvailable { get; private set; }
    public bool NetworkCheckFailed { get; private set; }
    public event Action<string>? EventLogged;
    public DiagnosticLog Diagnostics { get; }
    private bool _started;
    private void Log(string message, DiagnosticLevel level = DiagnosticLevel.Info, string operation = "Dependencies",
        string? userMessageKey = null, params object?[] userArgs)
    {
        Diagnostics.Record(level, operation, message, userMessageKey: userMessageKey,
            userMessageArgs: userArgs.Length == 0 ? null : userArgs,
            userSuccess: userMessageKey is "LogToolReadyVersion" or "ToolsVerifiedReady" or "ToolsDownloadedReady");
        EventLogged?.Invoke(message);
    }
    public bool CanDirectDownload => OperatingSystem.IsWindowsVersionAtLeast(10) && RuntimeInformation.OSArchitecture == Architecture.X64;
    private ToolPaths _savedPaths = new();

    public FfmpegTools() : this(null, null, null) { }

    internal FfmpegTools(string? settingsPath, string? appDirectory, string? searchPathOverride,
        string? dataDirectory = null, HttpMessageHandler? httpHandler = null)
    {
        _appDirectory = appDirectory ?? AppContext.BaseDirectory;
        _searchPathOverride = searchPathOverride;
        _dataDirectory = Path.GetFullPath(dataDirectory ?? AppDirectories.Resolve(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)));
        Diagnostics = new DiagnosticLog(Path.Combine(_dataDirectory, "logs"));
        _httpHandler = httpHandler;
        var settingsDirectory = OperatingSystem.IsWindows()
            ? Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData)
            : OperatingSystem.IsMacOS()
                ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Library", "Application Support")
                : Environment.GetEnvironmentVariable("XDG_CONFIG_HOME") is { Length: > 0 } xdg && Path.IsPathRooted(xdg)
                    ? xdg
                    : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config");
        _settingsPath = settingsPath ?? Path.Combine(AppDirectories.Resolve(settingsDirectory, "settings.json"), "settings.json");
        try
        {
            if (File.Exists(_settingsPath))
                _savedPaths = JsonSerializer.Deserialize<ToolPaths>(File.ReadAllText(_settingsPath)) ?? new();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            SettingsError = L.Get("ToolsSavedPathsReadFailed");
            SettingsErrorMessage = new("ToolsSavedPathsReadFailed");
        }
    }

    public string? SettingsError { get; private set; }
    internal LocalizedMessage? SettingsErrorMessage { get; private set; }
    internal LocalizedMessage? ManualPathErrorMessage { get; private set; }
    internal LocalizedMessage? LastInstallOptionMessage { get; private set; }

    public async Task<ToolsStatus> CheckAsync(CancellationToken cancellationToken)
    {
        var priorPaths = _savedPaths;
        var status = await CheckCoreAsync(cancellationToken, saveRecoveredPaths: true);
        RequiredRepair = RepairSource.None;
        if (status.IsReady || !OperatingSystem.IsWindows()) return status;
        var ownRoot = Path.Combine(_dataDirectory, "tools");
        bool hasManagedCopy;
        try { hasManagedCopy = Directory.Exists(ownRoot)
            && Directory.EnumerateDirectories(ownRoot, "ffmpeg-*", SearchOption.TopDirectoryOnly).Any(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { hasManagedCopy = false; }
        var managedPath = new[] { priorPaths.FFmpeg, priorPaths.FFprobe }.Any(path => path is not null
            && path.StartsWith(ownRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase));
        if (hasManagedCopy && managedPath)
        {
            RequiredRepair = RepairSource.Local;
            Log("Managed FFmpeg installation is incomplete or cannot start.", DiagnosticLevel.Warn,
                "Verification", "ToolsLocalRepairNeeded");
            return status;
        }
        var winget = await FindWorkingCommandAsync("winget", cancellationToken);
        if (winget is null)
        {
            if (hasManagedCopy) MarkLocalRepair();
            return status;
        }
        try
        {
            Log("Command: winget list --id Gyan.FFmpeg --exact --source winget --disable-interactivity --accept-source-agreements",
                operation: "WinGet");
            var listed = await RunAsync(winget, ["list", "--id", "Gyan.FFmpeg", "--exact", "--source", "winget",
                "--disable-interactivity", "--accept-source-agreements"], TimeSpan.FromSeconds(20), cancellationToken);
            Log($"WinGet package registration check: exit {listed.ExitCode}; {listed.Stdout}; {listed.Stderr}",
                listed.ExitCode == 0 ? DiagnosticLevel.Info : DiagnosticLevel.Warn, "WinGet");
            if (listed.ExitCode == 0 && listed.Stdout.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
                .Any(line => line.Split(' ', StringSplitOptions.RemoveEmptyEntries)
                    .Any(column => column.Equals("Gyan.FFmpeg", StringComparison.OrdinalIgnoreCase))))
            {
                RequiredRepair = RepairSource.WinGet;
                Log("Registered Gyan.FFmpeg package has missing or unusable executables.", DiagnosticLevel.Warn,
                    "WinGet", "ToolsWingetRepairNeeded");
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) when (ex is TimeoutException or IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        { Log($"WinGet registration check failed: {ex.GetType().Name}: {ex.Message}", DiagnosticLevel.Warn, "WinGet"); }
        if (RequiredRepair == RepairSource.None && hasManagedCopy) MarkLocalRepair();
        return status;

        void MarkLocalRepair()
        {
            RequiredRepair = RepairSource.Local;
            Log("Managed FFmpeg installation is incomplete or cannot start.", DiagnosticLevel.Warn,
                "Verification", "ToolsLocalRepairNeeded");
        }
    }

    private async Task<ToolsStatus> CheckCoreAsync(CancellationToken cancellationToken, bool saveRecoveredPaths)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!_started)
        {
            _started = true;
            Log("Application dependency check started.", operation: "Startup");
        }
        Log("FFmpeg and FFprobe verification started.", operation: "Verification",
            userMessageKey: "InstallerCheckingTools");
        var executableSuffix = OperatingSystem.IsWindows() ? ".exe" : "";
        var ownPairs = OwnInstallationPairs(executableSuffix).ToArray();
        var pathDirectories = PathDirectories().ToArray();
        var knownFFmpeg = OperatingSystem.IsWindows() && _searchPathOverride is null
            ? KnownWindowsPaths("ffmpeg.exe").Where(File.Exists).ToArray() : [];
        var knownFFprobe = OperatingSystem.IsWindows() && _searchPathOverride is null
            ? KnownWindowsPaths("ffprobe.exe").Where(File.Exists).ToArray() : [];
        var comparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        var checkedPaths = new ConcurrentDictionary<string, ToolStatus>(comparer);

        async Task<ToolStatus> Check(string name, string path)
        {
            var key = name + "\0" + path;
            if (checkedPaths.TryGetValue(key, out var cached)) return cached;
            Log($"Checking {name}: {path}", operation: "Verification",
                userMessageKey: "LogCheckingTool", userArgs: [name]);
            var result = await CheckCandidateAsync(name, path, cancellationToken);
            checkedPaths[key] = result;
            Log(result.IsReady ? $"{name} {result.Details}: {path}"
                    : $"{name} failed verification ({DiagnosticFailure(result)}): {path}",
                result.IsReady ? DiagnosticLevel.Info : DiagnosticLevel.Warn, "Verification",
                result.IsReady ? "LogToolReadyVersion" : "LogToolUnavailable",
                result.IsReady ? [name, result.Details.Split(' ', StringSplitOptions.RemoveEmptyEntries)[2]] : [name]);
            return result;
        }

        static string DiagnosticFailure(ToolStatus result) => result.LocalizedDetails?.Key switch
        {
            "ToolsFileNotFoundPath" => "file not found",
            "ToolsExecutableExitFailure" => "nonzero exit code",
            "ToolsWrongExecutable" => "unexpected version output",
            "ToolsVersionTimeout" => "version check timed out",
            "ToolsLaunchFailed" => "process launch failed",
            _ => result.IsMissing ? "file not found" : "unknown failure"
        };

        var pairs = new List<ToolPair>();
        if (!string.IsNullOrWhiteSpace(_savedPaths.FFmpeg) && !string.IsNullOrWhiteSpace(_savedPaths.FFprobe))
            pairs.Add(new(_savedPaths.FFmpeg, _savedPaths.FFprobe));
        pairs.AddRange(ownPairs);
        pairs.Add(new(Path.Combine(_appDirectory, "ffmpeg" + executableSuffix),
            Path.Combine(_appDirectory, "ffprobe" + executableSuffix)));
        pairs.AddRange(pathDirectories.Select(directory => new ToolPair(
            Path.Combine(directory, "ffmpeg" + executableSuffix),
            Path.Combine(directory, "ffprobe" + executableSuffix))));
        pairs.AddRange(knownFFmpeg.Select(path => new ToolPair(path,
            Path.Combine(Path.GetDirectoryName(path)!, "ffprobe.exe"))));

        foreach (var pair in pairs)
        {
            if (!File.Exists(pair.FFmpeg) || !File.Exists(pair.FFprobe)) continue;
            var ffmpegTask = Check("ffmpeg", pair.FFmpeg);
            var ffprobeTask = Check("ffprobe", pair.FFprobe);
            await Task.WhenAll(ffmpegTask, ffprobeTask);
            if (ffmpegTask.Result.IsReady && ffprobeTask.Result.IsReady)
                return await FinishAsync(new(ffmpegTask.Result, ffprobeTask.Result));
        }

        async Task<ToolStatus> FirstWorking(string name, string? saved, IEnumerable<string> known)
        {
            var candidates = new List<string>();
            if (!string.IsNullOrWhiteSpace(saved)) candidates.Add(saved);
            candidates.Add(Path.Combine(_appDirectory, name + executableSuffix));
            candidates.AddRange(pathDirectories.Select(directory => Path.Combine(directory, name + executableSuffix)));
            candidates.AddRange(known);
            var failure = string.IsNullOrWhiteSpace(saved) ? L.Get("ToolsFileNotFound") : L.Format("ToolsFileNotFoundPath", saved);
            LocalizedMessage failureMessage = string.IsNullOrWhiteSpace(saved)
                ? new("ToolsFileNotFound") : new("ToolsFileNotFoundPath", saved);
            var missing = true;
            foreach (var candidate in candidates.Distinct(comparer))
            {
                var result = await Check(name, candidate);
                if (result.IsReady) return result;
                if (File.Exists(candidate))
                {
                    failure = result.Details;
                    failureMessage = result.LocalizedDetails ?? new("ToolsLaunchFailed", name);
                    missing = false;
                }
            }
            return new ToolStatus(name, false, failure, IsMissing: missing)
            { LocalizedDetails = failureMessage };
        }

        var ffmpeg = FirstWorking("ffmpeg", _savedPaths.FFmpeg, knownFFmpeg);
        var ffprobe = FirstWorking("ffprobe", _savedPaths.FFprobe, knownFFprobe);
        await Task.WhenAll(ffmpeg, ffprobe);
        return await FinishAsync(new(await ffmpeg, await ffprobe));

        async Task<ToolsStatus> FinishAsync(ToolsStatus status)
        {
            var updated = new ToolPaths(status.FFmpeg.IsReady ? status.FFmpeg.Path : null,
                status.FFprobe.IsReady ? status.FFprobe.Path : null);
            if (saveRecoveredPaths && updated != _savedPaths)
            {
                try
                {
                    await SavePathsAsync(updated, cancellationToken);
                    Log("Working FFmpeg/FFprobe paths saved.");
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    SettingsError = L.Get("ToolsRecoveredPathsSaveFailed");
                    SettingsErrorMessage = new("ToolsRecoveredPathsSaveFailed");
                    Log("Failed to save discovered paths.");
                }
            }
            return status;
        }
    }

    public async Task<string?> SetManualPathAsync(string name, string path, CancellationToken cancellationToken)
    {
        if (name is not ("ffmpeg" or "ffprobe"))
            throw new ArgumentException("Unknown tool.", nameof(name));

        var result = await CheckCandidateAsync(name, path, cancellationToken);
        if (!result.IsReady)
        {
            ManualPathErrorMessage = result.LocalizedDetails;
            return result.Details;
        }

        var updated = name == "ffmpeg"
            ? _savedPaths with { FFmpeg = path }
            : _savedPaths with { FFprobe = path };
        ManualPathErrorMessage = null;
        try
        {
            await SavePathsAsync(updated, cancellationToken);
            Log($"{name} path saved: {path}");
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            SettingsError = L.Get("ToolsManualPathSaveFailed");
            SettingsErrorMessage = new("ToolsManualPathSaveFailed");
            ManualPathErrorMessage = SettingsErrorMessage;
            Log($"Failed to save {name} path.");
            return SettingsError;
        }
    }

    public async Task<string> GetInstallOptionAsync(CancellationToken cancellationToken)
    {
        if (RequiredRepair != RepairSource.None)
        {
            var key = RequiredRepair == RepairSource.WinGet ? "ToolsWingetRepairNeeded" : "ToolsLocalRepairNeeded";
            LastInstallOptionMessage = new(key);
            return L.Get(key);
        }
        if (OperatingSystem.IsWindows())
        {
            var winget = await CheckCommandAsync("winget", cancellationToken);
            WinGetAvailable = winget;
            NetworkCheckFailed = false;
            if (winget && await ProbeAsync(new Uri("https://cdn.winget.microsoft.com/cache"), cancellationToken))
            {
                OfferDirectDownload = false;
                LastInstallOptionMessage = new("ToolsWingetOption");
                return L.Get("ToolsWingetOption");
            }
            if (CanDirectDownload && await ProbeAsync(new Uri("https://www.gyan.dev/ffmpeg/builds/"), cancellationToken))
            {
                OfferDirectDownload = true;
                LastInstallOptionMessage = new(winget ? "ToolsWingetSourceFallback" : "ToolsNoWingetFallback");
                return winget
                    ? L.Get("ToolsWingetSourceFallback")
                    : L.Get("ToolsNoWingetFallback");
            }
            OfferDirectDownload = !winget && CanDirectDownload;
            NetworkCheckFailed = true;
            Log("Neither installation source responded to the preliminary check.", DiagnosticLevel.Warn, "HTTPS");
            LastInstallOptionMessage = new("ToolsInstallSourcesUnavailable");
            return L.Get("ToolsInstallSourcesUnavailable");
        }

        if (OperatingSystem.IsMacOS())
        {
            LastInstallOptionMessage = new(await CheckCommandAsync("brew", cancellationToken)
                ? "ToolsMacBrewOption" : "ToolsMacNoBrewOption");
            return LastInstallOptionMessage.Resolve();
        }

        if (OperatingSystem.IsLinux())
        {
            foreach (var (manager, command) in new[]
            {
                ("apt-get", "sudo apt-get install ffmpeg"),
                ("dnf", "sudo dnf install ffmpeg"),
                ("pacman", "sudo pacman -S ffmpeg"),
                ("zypper", "sudo zypper install ffmpeg")
            })
                if (await CheckCommandAsync(manager, cancellationToken))
                {
                    LastInstallOptionMessage = new("ToolsLinuxManagerOption", command);
                    return L.Format("ToolsLinuxManagerOption", command);
                }
            LastInstallOptionMessage = new("ToolsLinuxNoManagerOption");
            return L.Get("ToolsLinuxNoManagerOption");
        }

        LastInstallOptionMessage = new("ToolsManualInstallOption");
        return L.Get("ToolsManualInstallOption");
    }

    public async Task<InstallResult> InstallOnWindowsAsync(CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsWindows())
            return new(false, await GetInstallOptionAsync(cancellationToken))
            { LocalizedMessage = LastInstallOptionMessage };
        if ((await CheckAsync(cancellationToken)).IsReady)
            return new(true, L.Get("ToolsAlreadyInstalled"))
            { LocalizedMessage = new("ToolsAlreadyInstalled") };
        var priorPaths = _savedPaths;
        var wingetPath = await FindWorkingCommandAsync("winget", cancellationToken);
        WinGetAvailable = wingetPath is not null;
        if (wingetPath is null)
            return RequiredRepair == RepairSource.WinGet ? WindowsRepairFailure("ToolsWingetRepairFailed")
                : WindowsFailure(L.Get("ToolsWingetUnavailable"), new("ToolsWingetUnavailable"));

        try
        {
            var repairing = RequiredRepair == RepairSource.WinGet;
            if (repairing)
            {
                Log("Gyan.FFmpeg repair started: uninstall registered package before reinstalling.",
                    operation: "WinGet", userMessageKey: "ToolsWingetRepairStarted");
                Log("Command: winget uninstall --id Gyan.FFmpeg --exact --source winget --disable-interactivity --accept-source-agreements",
                    operation: "WinGet");
                var removed = await RunAsync(wingetPath, ["uninstall", "--id", "Gyan.FFmpeg", "--exact",
                    "--source", "winget", "--disable-interactivity", "--accept-source-agreements"],
                    TimeSpan.FromMinutes(10), cancellationToken);
                Log($"WinGet uninstall completed: exit {removed.ExitCode}; {removed.Stdout}; {removed.Stderr}",
                    removed.ExitCode == 0 ? DiagnosticLevel.Info : DiagnosticLevel.Error, "WinGet");
                if (removed.ExitCode != 0) return WindowsRepairFailure("ToolsWingetRepairFailed");
            }
            Log("Gyan.FFmpeg installation via WinGet started.", operation: "WinGet",
                userMessageKey: "InstallerInstallingWithWinget");
            Log("Command: winget install --id Gyan.FFmpeg --exact --source winget --scope user --accept-package-agreements --accept-source-agreements --disable-interactivity",
                operation: "WinGet");
            var result = await RunAsync(wingetPath, ["install", "--id", "Gyan.FFmpeg", "--exact",
                "--source", "winget", "--scope", "user", "--accept-package-agreements", "--accept-source-agreements", "--disable-interactivity"],
                TimeSpan.FromMinutes(10), cancellationToken);
            Log($"WinGet install completed: exit {result.ExitCode}; {result.Stdout}; {result.Stderr}",
                result.ExitCode == 0 ? DiagnosticLevel.Info : DiagnosticLevel.Error, "WinGet");
            var status = await CheckCoreAsync(cancellationToken, saveRecoveredPaths: false);
            if (status.IsReady)
            {
                await SavePathsAsync(new(
                    await WorkingSavedPathAsync("ffmpeg", priorPaths.FFmpeg, status.FFmpeg.Path!, cancellationToken),
                    await WorkingSavedPathAsync("ffprobe", priorPaths.FFprobe, status.FFprobe.Path!, cancellationToken)), cancellationToken);
                OfferDirectDownload = false;
                RequiredRepair = RepairSource.None;
                Log("FFmpeg and FFprobe verified and ready after WinGet installation.", operation: "WinGet",
                    userMessageKey: repairing ? "ToolsWingetRepairComplete" : "ToolsVerifiedReady");
                var successKey = repairing ? "ToolsWingetRepairComplete" : "ToolsVerifiedReady";
                return new(true, L.Get(successKey)) { LocalizedMessage = new(successKey) };
            }
            if (repairing) return WindowsRepairFailure("ToolsWingetRepairFailed");
            return WindowsFailure(result.ExitCode != 0
                ? L.Format("ToolsWingetExitFailure", result.ExitCode)
                : L.Get("ToolsWingetMissingAfterInstall"),
                result.ExitCode != 0 ? new("ToolsWingetExitFailure", result.ExitCode)
                    : new("ToolsWingetMissingAfterInstall"));
        }
        catch (OperationCanceledException)
        {
            Log("WinGet installation cancelled.", DiagnosticLevel.Warn, "WinGet", "InstallerInstallCancelled");
            throw;
        }
        catch (TimeoutException)
        {
            return RequiredRepair == RepairSource.WinGet ? WindowsRepairFailure("ToolsWingetRepairFailed")
                : WindowsFailure(L.Get("ToolsWingetTimeout"), new("ToolsWingetTimeout"));
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException or UnauthorizedAccessException)
        {
            Log($"WinGet operation failed: {ex.GetType().Name}: {ex.Message}", DiagnosticLevel.Error, "WinGet");
            return RequiredRepair == RepairSource.WinGet ? WindowsRepairFailure("ToolsWingetRepairFailed")
                : WindowsFailure(L.Get("ToolsWingetInstallFailed"), new("ToolsWingetInstallFailed"));
        }
    }

    private InstallResult WindowsRepairFailure(string key)
    {
        OfferDirectDownload = false;
        Log("Gyan.FFmpeg repair did not restore both executables.", DiagnosticLevel.Error, "WinGet", key);
        return new(false, L.Get(key)) { LocalizedMessage = new(key) };
    }

    private InstallResult WindowsFailure(string message, LocalizedMessage reason)
    {
        OfferDirectDownload = CanDirectDownload;
        Log("WinGet installation did not establish FFmpeg and FFprobe readiness.", DiagnosticLevel.Error, "WinGet",
            "ToolsWingetInstallFailed");
        return new(false, DirectDownloadAdvice(message))
        { LocalizedMessage = new("ToolsFailureWithAdvice", reason,
            new LocalizedMessage(CanDirectDownload ? "ToolsDirectDownloadAdvice" : "ToolsManualArchitectureAdvice")) };
    }

    private async Task<bool> ProbeAsync(Uri uri, CancellationToken cancellationToken)
    {
        if (!(NetworkAvailable?.Invoke() ?? NetworkInterface.GetIsNetworkAvailable()))
        {
            Log($"No network detected; skipped check of {uri.Host}.");
            return false;
        }
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(3));
        try
        {
            if (ProbeRunner is not null)
            {
                var result = await ProbeRunner(uri, timeout.Token);
                Log(result ? $"Source {uri.Host} is responding." : $"Source {uri.Host} is not responding.",
                    result ? DiagnosticLevel.Info : DiagnosticLevel.Warn, "HTTPS");
                return result;
            }
            using var http = _httpHandler is null
                ? new HttpClient(new HttpClientHandler { AllowAutoRedirect = false })
                : new HttpClient(_httpHandler, disposeHandler: false);
            http.Timeout = Timeout.InfiniteTimeSpan;
            using var request = new HttpRequestMessage(HttpMethod.Head, uri);
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            var available = (int)response.StatusCode < 500;
            Log(available ? $"Source {uri.Host} is responding." : $"Source {uri.Host} is not responding.",
                available ? DiagnosticLevel.Info : DiagnosticLevel.Warn, "HTTPS");
            return available;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            Log($"Source {uri.Host} did not respond within 3 seconds.", DiagnosticLevel.Warn, "HTTPS");
            return false;
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException)
        {
            Log($"Could not connect to {uri.Host}.", DiagnosticLevel.Warn, "HTTPS");
            return false;
        }
    }

    private string DirectDownloadAdvice(string message) => message + (CanDirectDownload
        ? L.Get("ToolsDirectDownloadAdvice") : L.Get("ToolsManualArchitectureAdvice"));

    private async Task SavePathsAsync(ToolPaths paths, CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(_settingsPath))!;
        Directory.CreateDirectory(directory);
        var temporary = Path.Combine(directory, ".settings-" + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(paths), cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporary, _settingsPath, overwrite: true);
            _savedPaths = paths;
            SettingsError = null;
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private async Task<string> WorkingSavedPathAsync(string name, string? saved, string fallback, CancellationToken cancellationToken) =>
        !string.IsNullOrWhiteSpace(saved) && (await CheckCandidateAsync(name, saved, cancellationToken)).IsReady
            ? saved : fallback;

    private IEnumerable<ToolPair> OwnInstallationPairs(string suffix)
    {
        var root = Path.Combine(_dataDirectory, "tools");
        if (!Directory.Exists(root)) yield break;
        string[] installations;
        try { installations = Directory.GetDirectories(root, "ffmpeg-*", SearchOption.TopDirectoryOnly); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { yield break; }
        foreach (var installation in installations.OrderBy(path => path, StringComparer.Ordinal))
        {
            if (IsReparsePoint(installation)) continue;
            string[] children;
            try { children = Directory.GetDirectories(installation, "*", SearchOption.TopDirectoryOnly); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { continue; }
            var bins = new[] { Path.Combine(installation, "bin") }.Concat(children
                .Where(path => !IsReparsePoint(path))
                .OrderBy(path => path, StringComparer.Ordinal)
                .Select(path => Path.Combine(path, "bin")));
            foreach (var bin in bins)
            {
                var ffmpeg = Path.Combine(bin, "ffmpeg" + suffix);
                var ffprobe = Path.Combine(bin, "ffprobe" + suffix);
                if (File.Exists(ffmpeg) && File.Exists(ffprobe)) yield return new(ffmpeg, ffprobe);
            }
        }
    }

    private sealed record ToolPair(string FFmpeg, string FFprobe);

    private static bool IsReparsePoint(string path)
    {
        try { return (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return true; }
    }

    private async Task<ToolStatus> CheckCandidateAsync(string name, string path, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!File.Exists(path))
            return new(name, false, L.Format("ToolsFileNotFoundPath", path), IsMissing: true)
            { LocalizedDetails = new("ToolsFileNotFoundPath", path) };
        try
        {
            var result = await RunAsync(path, ["-version"], TimeSpan.FromSeconds(5), cancellationToken);
            if (result.ExitCode != 0)
                return new(name, false, L.Format("ToolsExecutableExitFailure", name, result.ExitCode))
                { LocalizedDetails = new("ToolsExecutableExitFailure", name, result.ExitCode) };
            var firstLine = result.Stdout.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "";
            var prefix = name + " version ";
            if (!firstLine.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ||
                string.IsNullOrWhiteSpace(firstLine[prefix.Length..]))
                return new(name, false, L.Format("ToolsWrongExecutable", name))
                { LocalizedDetails = new("ToolsWrongExecutable", name) };
            return new(name, true, firstLine, path);
        }
        catch (TimeoutException)
        {
            return new(name, false, L.Format("ToolsVersionTimeout", name))
            { LocalizedDetails = new("ToolsVersionTimeout", name) };
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException or UnauthorizedAccessException)
        {
            return new(name, false, L.Format("ToolsLaunchFailed", name))
            { LocalizedDetails = new("ToolsLaunchFailed", name) };
        }
    }

    private async Task<bool> CheckCommandAsync(string name, CancellationToken cancellationToken) =>
        await FindWorkingCommandAsync(name, cancellationToken) is not null;

    private async Task<string?> FindWorkingCommandAsync(string name, CancellationToken cancellationToken)
    {
        var executable = OperatingSystem.IsWindows() ? name + ".exe" : name;
        var candidates = FindAllOnPath(executable).ToList();
        if (OperatingSystem.IsMacOS() && name == "brew")
            candidates.AddRange(new[] { "/opt/homebrew/bin/brew", "/usr/local/bin/brew" }.Where(File.Exists));
        foreach (var path in candidates.Distinct(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal))
        {
            try
            {
                var result = await RunAsync(path, ["--version"], TimeSpan.FromSeconds(5), cancellationToken);
                if (result.ExitCode == 0) return path;
            }
            catch (Exception ex) when (ex is TimeoutException or System.ComponentModel.Win32Exception or IOException or UnauthorizedAccessException) { }
        }
        return null;
    }

    private IEnumerable<string> FindAllOnPath(string executable)
    {
        foreach (var directory in PathDirectories())
        {
            var candidate = Path.Combine(directory, executable);
            if (File.Exists(candidate)) yield return candidate;
        }
        if (OperatingSystem.IsWindows() && _searchPathOverride is null)
            foreach (var candidate in KnownWindowsPaths(executable))
                if (File.Exists(candidate)) yield return candidate;
    }

    private IEnumerable<string> PathDirectories()
    {
        var paths = _searchPathOverride is null
            ? new[] { Environment.GetEnvironmentVariable("PATH"),
                OperatingSystem.IsWindows() ? Environment.GetEnvironmentVariable("PATH", EnvironmentVariableTarget.User) : null,
                OperatingSystem.IsWindows() ? Environment.GetEnvironmentVariable("PATH", EnvironmentVariableTarget.Machine) : null }
            : new[] { _searchPathOverride };
        foreach (var path in paths)
            foreach (var directory in (path ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                string candidate;
                try { candidate = OperatingSystem.IsWindows()
                    ? Environment.ExpandEnvironmentVariables(directory.Trim('"')) : directory; }
                catch (ArgumentException) { continue; }
                if (Directory.Exists(candidate)) yield return candidate;
            }
    }

    private static IEnumerable<string> KnownWindowsPaths(string executable)
    {
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var roots = new[] { Path.Combine(local, "Microsoft", "WinGet"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "WinGet") };
        var candidates = new List<string>();
        if (executable == "winget.exe") candidates.Add(Path.Combine(local, "Microsoft", "WindowsApps", executable));
        foreach (var root in roots)
        {
            candidates.Add(Path.Combine(root, "Links", executable));
            if (executable is not ("ffmpeg.exe" or "ffprobe.exe")) continue;
            try
            {
                var packages = Path.Combine(root, "Packages");
                if (!Directory.Exists(packages)) continue;
                foreach (var package in Directory.EnumerateDirectories(packages, "Gyan.FFmpeg_*"))
                {
                    candidates.Add(Path.Combine(package, "bin", executable));
                    foreach (var build in Directory.EnumerateDirectories(package, "ffmpeg*"))
                        candidates.Add(Path.Combine(build, "bin", executable));
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
        return candidates;
    }

    private async Task<ProcessResult> RunAsync(string executable, string[] arguments, TimeSpan timeout, CancellationToken cancellationToken)
    {
        if (ProcessRunner is not null) return await ProcessRunner(executable, arguments, timeout, cancellationToken);
        using var process = new Process { StartInfo = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        } };
        foreach (var argument in arguments) process.StartInfo.ArgumentList.Add(argument);
        cancellationToken.ThrowIfCancellationRequested();
        process.Start();
        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(timeout);
        try
        {
            var stdout = ReadOutputAsync(process.StandardOutput, timeoutSource.Token);
            var stderr = ReadOutputAsync(process.StandardError, timeoutSource.Token);
            await Task.WhenAll(process.WaitForExitAsync(timeoutSource.Token), stdout, stderr);
            return new(process.ExitCode, await stdout, await stderr);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            await KillAsync(process);
            throw new TimeoutException();
        }
        catch (OperationCanceledException)
        {
            await KillAsync(process);
            throw;
        }
        catch
        {
            await KillAsync(process);
            throw;
        }
    }

    private static async Task<string> ReadOutputAsync(StreamReader reader, CancellationToken cancellationToken)
    {
        var output = new StringBuilder();
        var buffer = new char[4096];
        int count;
        while ((count = await reader.ReadAsync(buffer, cancellationToken)) != 0)
            if (output.Length < 65536) output.Append(buffer, 0, Math.Min(count, 65536 - output.Length));
        return output.ToString();
    }

    private static async Task KillAsync(Process process)
    {
        try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
        catch (InvalidOperationException) { }
        catch (System.ComponentModel.Win32Exception)
        {
            if (!process.HasExited) process.Kill();
        }
        using var wait = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        try { await process.WaitForExitAsync(wait.Token); }
        catch (OperationCanceledException) { throw new IOException("Could not stop the child process. Close it manually before retrying."); }
        catch (InvalidOperationException) { }
    }

    private sealed record ToolPaths(string? FFmpeg = null, string? FFprobe = null);
    internal sealed record ProcessResult(int ExitCode, string Stdout, string Stderr);
}
