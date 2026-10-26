using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
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
    public bool OfferDirectDownload { get; private set; }
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
            SettingsError = $"Не удалось прочитать сохранённые пути: {ex.Message}";
        }
    }

    public string? SettingsError { get; private set; }

    public async Task<ToolsStatus> CheckAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var ffmpeg = CheckToolAsync("ffmpeg", _savedPaths.FFmpeg, cancellationToken);
        var ffprobe = CheckToolAsync("ffprobe", _savedPaths.FFprobe, cancellationToken);
        await Task.WhenAll(ffmpeg, ffprobe);
        return new(await ffmpeg, await ffprobe);
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
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            SettingsError = $"Путь проверен, но не сохранён: {ex.Message}";
            return SettingsError;
        }
    }

    public async Task<string> GetInstallOptionAsync(CancellationToken cancellationToken)
    {
        if (OperatingSystem.IsWindows())
        {
            var winget = await CheckCommandAsync("winget", cancellationToken);
            OfferDirectDownload = !winget && CanDirectDownload;
            return winget
                ? "Будет запущена установка пакета Gyan.FFmpeg через WinGet. Подтвердите действие кнопкой «Установить»."
                : DirectDownloadAdvice("WinGet недоступен.");
        }

        if (OperatingSystem.IsMacOS())
            return await CheckCommandAsync("brew", cancellationToken)
                ? "Установите FFmpeg в терминале командой: brew install ffmpeg. Затем нажмите «Повторить проверку»."
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
        var wingetPath = FindOnPath("winget");
        if (wingetPath is null || !await CheckCommandAsync("winget", cancellationToken))
            return WindowsFailure("WinGet недоступен.");

        try
        {
            var result = await RunAsync(wingetPath, ["install", "--id", "Gyan.FFmpeg", "--exact",
                "--accept-package-agreements", "--accept-source-agreements", "--disable-interactivity"],
                TimeSpan.FromMinutes(10), cancellationToken);
            var status = await CheckAsync(cancellationToken);
            if (status.IsReady)
            {
                await SavePathsAsync(new(status.FFmpeg.Path, status.FFprobe.Path), cancellationToken);
                OfferDirectDownload = false;
                return new(true, "FFmpeg и FFprobe проверены и готовы.");
            }
            return WindowsFailure(result.ExitCode != 0
                ? $"WinGet завершился с кодом {result.ExitCode}: {GetOutput(result)}"
                : "WinGet завершился, но обе работоспособные утилиты не найдены. Можно повторить проверку после перезапуска приложения.");
        }
        catch (TimeoutException)
        {
            return WindowsFailure("Время ожидания WinGet истекло. Повторите проверку.");
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException or UnauthorizedAccessException)
        {
            return WindowsFailure($"Не удалось завершить установку через WinGet: {ex.Message}");
        }
    }

    private InstallResult WindowsFailure(string message)
    {
        OfferDirectDownload = CanDirectDownload;
        return new(false, DirectDownloadAdvice(message));
    }

    private string DirectDownloadAdvice(string message) => message + (CanDirectDownload
        ? " Нажмите «Установить», чтобы скачать Windows x64 ZIP с https://www.gyan.dev/ffmpeg/builds/ и проверить SHA-256. Установка будет выполнена в пользовательский каталог Track Extractor без изменения PATH."
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

    private async Task<ToolStatus> CheckToolAsync(string name, string? savedPath, CancellationToken cancellationToken)
    {
        var executable = OperatingSystem.IsWindows() ? name + ".exe" : name;
        var candidates = new List<string>();
        if (!string.IsNullOrWhiteSpace(savedPath)) candidates.Add(savedPath);
        candidates.Add(Path.Combine(_appDirectory, executable));
        candidates.AddRange(FindAllOnPath(executable));
        var failure = string.IsNullOrWhiteSpace(savedPath) ? "Файл не найден." : $"Файл не найден: {savedPath}";
        foreach (var candidate in candidates.Distinct(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal))
        {
            var result = await CheckCandidateAsync(name, candidate, cancellationToken);
            if (result.IsReady) return result;
            if (File.Exists(candidate)) failure = result.Details;
        }
        return new(name, false, failure);
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
                return new(name, false, $"{name}: код завершения {result.ExitCode}. {GetOutput(result)}");
            var firstLine = result.Stdout.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "";
            var prefix = name + " version ";
            if (!firstLine.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ||
                string.IsNullOrWhiteSpace(firstLine[prefix.Length..]))
                return new(name, false, $"{name}: неожиданный ответ на -version.");
            return new(name, true, firstLine, path);
        }
        catch (TimeoutException)
        {
            return new(name, false, $"{name}: превышено время ожидания ответа (5 секунд).");
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException or UnauthorizedAccessException)
        {
            return new(name, false, $"{name}: ошибка запуска: {ex.Message}");
        }
    }

    private async Task<bool> CheckCommandAsync(string name, CancellationToken cancellationToken)
    {
        var path = FindOnPath(name);
        if (path is null) return false;
        try
        {
            var result = await RunAsync(path, ["--version"], TimeSpan.FromSeconds(5), cancellationToken);
            return result.ExitCode == 0;
        }
        catch (Exception ex) when (ex is TimeoutException or System.ComponentModel.Win32Exception or IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private string? FindOnPath(string name)
    {
        var executable = OperatingSystem.IsWindows() ? name + ".exe" : name;
        var path = FindAllOnPath(executable).FirstOrDefault();
        if (path is not null) return path;
        if (OperatingSystem.IsMacOS() && name == "brew")
            return new[] { "/opt/homebrew/bin/brew", "/usr/local/bin/brew" }.FirstOrDefault(File.Exists);
        return null;
    }

    private IEnumerable<string> FindAllOnPath(string executable)
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
                try { candidate = Path.Combine(directory.Trim('"'), executable); }
                catch (ArgumentException) { continue; }
                if (File.Exists(candidate)) yield return candidate;
            }
        if (OperatingSystem.IsWindows() && _searchPathOverride is null)
        {
            var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            var link = Path.Combine(localAppData, "Microsoft", "WinGet", "Links", executable);
            if (File.Exists(link)) yield return link;
        }
    }

    private static string GetOutput(ProcessResult result)
    {
        var output = string.IsNullOrWhiteSpace(result.Stderr) ? result.Stdout : result.Stderr;
        if (string.IsNullOrWhiteSpace(output)) return "Подробности не предоставлены.";
        output = output.Trim();
        return output.Length <= 1000 ? output : output[..1000] + "…";
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
