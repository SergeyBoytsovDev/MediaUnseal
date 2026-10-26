using System;
using System.Collections.Generic;
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

namespace TrackExtractor.Services;

public sealed record ToolStatus(string Name, bool IsReady, string Details, string? Path = null);

public sealed record ToolsStatus(ToolStatus FFmpeg, ToolStatus FFprobe)
{
    public bool IsReady => FFmpeg.IsReady && FFprobe.IsReady;
}

public sealed record InstallResult(bool Success, string Message);
public sealed record InstallProgress(string Message, double? Percent = null);

public sealed partial class FfmpegTools
{
    private readonly string _settingsPath;
    private readonly string _appDirectory;
    private readonly string? _searchPathOverride;
    private readonly string _dataDirectory;
    private readonly HttpMessageHandler? _httpHandler;
    internal Func<string, string[], TimeSpan, CancellationToken, Task<ProcessResult>>? ProcessRunner { get; init; }
    internal Func<Uri, CancellationToken, Task<bool>>? ProbeRunner { get; init; }
    internal Func<bool>? NetworkAvailable { get; init; }
    public bool OfferDirectDownload { get; private set; }
    public bool NetworkCheckFailed { get; private set; }
    public event Action<string>? EventLogged;
    public DiagnosticLog Diagnostics { get; }
    private bool _started;
    private void Log(string message, DiagnosticLevel level = DiagnosticLevel.Info, string operation = "Зависимости")
    {
        Diagnostics.Record(level, operation, message);
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
        _dataDirectory = Path.GetFullPath(dataDirectory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TrackExtractor"));
        Diagnostics = new DiagnosticLog(Path.Combine(_dataDirectory, "logs"));
        _httpHandler = httpHandler;
        var settingsDirectory = OperatingSystem.IsWindows()
            ? Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData)
            : OperatingSystem.IsMacOS()
                ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Library", "Application Support")
                : Environment.GetEnvironmentVariable("XDG_CONFIG_HOME") is { Length: > 0 } xdg && Path.IsPathRooted(xdg)
                    ? xdg
                    : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config");
        _settingsPath = settingsPath ?? Path.Combine(settingsDirectory, "TrackExtractor", "settings.json");
        try
        {
            if (File.Exists(_settingsPath))
                _savedPaths = JsonSerializer.Deserialize<ToolPaths>(File.ReadAllText(_settingsPath)) ?? new();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            SettingsError = "Не удалось прочитать сохранённые пути. Проверьте файл пользовательских настроек.";
        }
    }

    public string? SettingsError { get; private set; }

    public async Task<ToolsStatus> CheckAsync(CancellationToken cancellationToken)
        => await CheckCoreAsync(cancellationToken, saveRecoveredPaths: true);

    private async Task<ToolsStatus> CheckCoreAsync(CancellationToken cancellationToken, bool saveRecoveredPaths)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!_started)
        {
            _started = true;
            Log("Запуск проверки зависимостей приложения.", operation: "Запуск");
        }
        Log("Начата проверка FFmpeg и FFprobe.", operation: "Проверка");
        var executableSuffix = OperatingSystem.IsWindows() ? ".exe" : "";
        var ownPairs = OwnInstallationPairs(executableSuffix).ToArray();
        var pathDirectories = PathDirectories().ToArray();
        var knownFFmpeg = OperatingSystem.IsWindows() && _searchPathOverride is null
            ? KnownWindowsPaths("ffmpeg.exe").Where(File.Exists).ToArray() : [];
        var knownFFprobe = OperatingSystem.IsWindows() && _searchPathOverride is null
            ? KnownWindowsPaths("ffprobe.exe").Where(File.Exists).ToArray() : [];
        var comparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        var checkedPaths = new Dictionary<string, ToolStatus>(comparer);

        async Task<ToolStatus> Check(string name, string path)
        {
            var key = name + "\0" + path;
            if (checkedPaths.TryGetValue(key, out var cached)) return cached;
            Log($"Проверяется {name}: {path}", operation: "Проверка");
            var result = await CheckCandidateAsync(name, path, cancellationToken);
            checkedPaths[key] = result;
            Log(result.IsReady ? $"{name} {result.Details}: {path}" : $"{name} не прошёл проверку: {path}",
                result.IsReady ? DiagnosticLevel.Info : DiagnosticLevel.Warn, "Проверка");
            return result;
        }

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
            var failure = string.IsNullOrWhiteSpace(saved) ? "Файл не найден." : $"Файл не найден: {saved}";
            foreach (var candidate in candidates.Distinct(comparer))
            {
                var result = await Check(name, candidate);
                if (result.IsReady) return result;
                if (File.Exists(candidate)) failure = result.Details;
            }
            return new(name, false, failure);
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
                    Log("Рабочие пути FFmpeg/FFprobe сохранены.");
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    SettingsError = "Рабочие утилиты найдены, но пути не удалось сохранить. Проверьте доступ к пользовательским настройкам.";
                    Log("Не удалось сохранить найденные пути.");
                }
            }
            return status;
        }
    }

    public async Task<string?> SetManualPathAsync(string name, string path, CancellationToken cancellationToken)
    {
        if (name is not ("ffmpeg" or "ffprobe"))
            throw new ArgumentException("Неизвестная утилита.", nameof(name));

        var result = await CheckCandidateAsync(name, path, cancellationToken);
        if (!result.IsReady)
            return result.Details;

        var updated = name == "ffmpeg"
            ? _savedPaths with { FFmpeg = path }
            : _savedPaths with { FFprobe = path };
        try
        {
            await SavePathsAsync(updated, cancellationToken);
            Log($"Путь {name} сохранён: {path}");
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            SettingsError = "Утилита работает, но путь не удалось сохранить. Проверьте доступ к пользовательским настройкам.";
            Log($"Не удалось сохранить путь {name}.");
            return SettingsError;
        }
    }

    public async Task<string> GetInstallOptionAsync(CancellationToken cancellationToken)
    {
        if (OperatingSystem.IsWindows())
        {
            var winget = await CheckCommandAsync("winget", cancellationToken);
            NetworkCheckFailed = false;
            if (winget && await ProbeAsync(new Uri("https://cdn.winget.microsoft.com/cache"), cancellationToken))
            {
                OfferDirectDownload = false;
                return "Доступен WinGet. Нажмите «Установить», чтобы установить Gyan.FFmpeg.";
            }
            if (CanDirectDownload && await ProbeAsync(new Uri("https://www.gyan.dev/ffmpeg/builds/"), cancellationToken))
            {
                OfferDirectDownload = true;
                return winget
                    ? "Источник WinGet не ответил. Нажмите «Установить» для резервной загрузки с gyan.dev."
                    : "WinGet недоступен. Нажмите «Установить» для резервной загрузки с gyan.dev.";
            }
            OfferDirectDownload = !winget && CanDirectDownload;
            NetworkCheckFailed = true;
            Log("Оба источника установки не ответили на предварительную проверку.", DiagnosticLevel.Warn, "HTTPS");
            return "Не удалось связаться с источниками установки. Проверьте подключение к интернету и повторите попытку";
        }

        if (OperatingSystem.IsMacOS())
            return await CheckCommandAsync("brew", cancellationToken)
                ? "Выполните в терминале: brew install ffmpeg. Затем нажмите «Повторить проверку». Homebrew из приложения не запускается; прямая загрузка сборок macOS не поддерживается. Альтернатива: https://ffmpeg.org/download.html и ручное указание путей."
                : "Homebrew не найден. Установите Homebrew с https://brew.sh и выполните brew install ffmpeg либо укажите пути вручную. Автоматическая загрузка сборок macOS не поддерживается.";

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
                    return $"Выполните в терминале: {command}. Наличие пакета зависит от репозиториев дистрибутива. Затем нажмите «Повторить проверку». Если пакет недоступен, см. https://ffmpeg.org/download.html или укажите пути вручную. Универсальная прямая загрузка для Linux не поддерживается.";
            return "Поддерживаемый пакетный менеджер не найден. Выберите пакет для своего дистрибутива и архитектуры на https://ffmpeg.org/download.html и укажите пути вручную. Универсальная прямая загрузка для Linux не поддерживается.";
        }

        return "Установите FFmpeg вручную и нажмите «Повторить проверку».";
    }

    public async Task<InstallResult> InstallOnWindowsAsync(CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsWindows())
            return new(false, await GetInstallOptionAsync(cancellationToken));
        if ((await CheckAsync(cancellationToken)).IsReady)
            return new(true, "FFmpeg и FFprobe уже найдены и готовы. Повторная установка не требуется.");
        var priorPaths = _savedPaths;
        var wingetPath = await FindWorkingCommandAsync("winget", cancellationToken);
        if (wingetPath is null)
            return WindowsFailure("WinGet недоступен.");

        try
        {
            Log("Запущена установка Gyan.FFmpeg через WinGet.", operation: "WinGet");
            var result = await RunAsync(wingetPath, ["install", "--id", "Gyan.FFmpeg", "--exact",
                "--source", "winget", "--scope", "user", "--accept-package-agreements", "--accept-source-agreements", "--disable-interactivity"],
                TimeSpan.FromMinutes(10), cancellationToken);
            var status = await CheckCoreAsync(cancellationToken, saveRecoveredPaths: false);
            if (status.IsReady)
            {
                await SavePathsAsync(new(
                    await WorkingSavedPathAsync("ffmpeg", priorPaths.FFmpeg, status.FFmpeg.Path!, cancellationToken),
                    await WorkingSavedPathAsync("ffprobe", priorPaths.FFprobe, status.FFprobe.Path!, cancellationToken)), cancellationToken);
                OfferDirectDownload = false;
                Log("После WinGet FFmpeg и FFprobe проверены и готовы.", operation: "WinGet");
                return new(true, "FFmpeg и FFprobe проверены и готовы.");
            }
            return WindowsFailure(result.ExitCode != 0
                ? $"WinGet не установил FFmpeg (код завершения {result.ExitCode})."
                : "WinGet завершился, но обе работоспособные утилиты не найдены. Можно повторить проверку после перезапуска приложения.");
        }
        catch (OperationCanceledException)
        {
            Log("Установка WinGet отменена.", DiagnosticLevel.Warn, "WinGet");
            throw;
        }
        catch (TimeoutException)
        {
            return WindowsFailure("Время ожидания WinGet истекло. Повторите проверку.");
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException or UnauthorizedAccessException)
        {
            return WindowsFailure("Не удалось завершить установку через WinGet. Проверьте права доступа или попробуйте резервный способ.");
        }
    }

    private InstallResult WindowsFailure(string message)
    {
        OfferDirectDownload = CanDirectDownload;
        Log("Установка WinGet не подтвердила готовность FFmpeg и FFprobe.", DiagnosticLevel.Error, "WinGet");
        return new(false, DirectDownloadAdvice(message));
    }

    private async Task<bool> ProbeAsync(Uri uri, CancellationToken cancellationToken)
    {
        if (!(NetworkAvailable?.Invoke() ?? NetworkInterface.GetIsNetworkAvailable()))
        {
            Log($"Сеть не обнаружена; пропущена проверка {uri.Host}.");
            return false;
        }
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(3));
        try
        {
            if (ProbeRunner is not null)
            {
                var result = await ProbeRunner(uri, timeout.Token);
                Log(result ? $"Источник {uri.Host} отвечает." : $"Источник {uri.Host} сейчас не отвечает.",
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
            Log(available ? $"Источник {uri.Host} отвечает." : $"Источник {uri.Host} сейчас не отвечает.",
                available ? DiagnosticLevel.Info : DiagnosticLevel.Warn, "HTTPS");
            return available;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            Log($"Источник {uri.Host} не ответил за 3 секунды.", DiagnosticLevel.Warn, "HTTPS");
            return false;
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException)
        {
            Log($"Не удалось связаться с {uri.Host}.", DiagnosticLevel.Warn, "HTTPS");
            return false;
        }
    }

    private string DirectDownloadAdvice(string message) => message + (CanDirectDownload
        ? " Нажмите «Установить» для резервной загрузки с gyan.dev в пользовательский каталог. PATH не изменится."
        : " Прямая загрузка поддерживается только для Windows 10/11 x64. Для этой архитектуры укажите совместимые утилиты вручную: https://ffmpeg.org/download.html.");

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
            return new(name, false, $"Файл не найден: {path}");
        try
        {
            var result = await RunAsync(path, ["-version"], TimeSpan.FromSeconds(5), cancellationToken);
            if (result.ExitCode != 0)
                return new(name, false, $"{name}: ошибка выполнения (код завершения {result.ExitCode}).");
            var firstLine = result.Stdout.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "";
            var prefix = name + " version ";
            if (!firstLine.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ||
                string.IsNullOrWhiteSpace(firstLine[prefix.Length..]))
                return new(name, false, $"{name}: неверный исполняемый файл — ответ на -version не соответствует утилите.");
            return new(name, true, firstLine, path);
        }
        catch (TimeoutException)
        {
            return new(name, false, $"{name}: превышено время ожидания ответа (5 секунд).");
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException or UnauthorizedAccessException)
        {
            return new(name, false, $"{name}: ошибка запуска. Проверьте права доступа и совместимость файла.");
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
        catch (OperationCanceledException) { throw new IOException("Не удалось завершить дочерний процесс. Закройте его вручную перед повторной попыткой."); }
        catch (InvalidOperationException) { }
    }

    private sealed record ToolPaths(string? FFmpeg = null, string? FFprobe = null);
    internal sealed record ProcessResult(int ExitCode, string Stdout, string Stderr);
}
