using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace TrackExtractor.Services;

public enum DiagnosticLevel { Info, Warn, Error }

public sealed record DiagnosticEvent(DateTimeOffset Time, DiagnosticLevel Level,
    string Operation, string Message, string? Details = null,
    string? UserMessageKey = null, object?[]? UserMessageArgs = null, bool UserSuccess = false);

public sealed class DiagnosticLog
{
    internal const long MaxFileBytes = 10L * 1024 * 1024;
    internal const long MaxTotalBytes = 100L * 1024 * 1024;
    internal static readonly TimeSpan Retention = TimeSpan.FromDays(14);
    private const string CurrentFile = "diagnostic.txt";
    private readonly string _directory;
    private readonly object _gate = new();
    public event Action<DiagnosticEvent>? EventRecorded;

    public DiagnosticLog(string directory) => _directory = directory;

    public void Record(DiagnosticLevel level, string operation, string message, string? details = null,
        string? userMessageKey = null, object?[]? userMessageArgs = null, bool userSuccess = false)
    {
        var entry = new DiagnosticEvent(DateTimeOffset.Now, level, Clean(operation),
            Clean(message), details is null ? null : Clean(details), userMessageKey, userMessageArgs, userSuccess);
        EventRecorded?.Invoke(entry);
        var line = Format(entry) + Environment.NewLine;
        try
        {
            lock (_gate)
            {
                Directory.CreateDirectory(_directory);
                Prune();
                var current = Path.Combine(_directory, CurrentFile);
                if (File.Exists(current) && new FileInfo(current).Length + Encoding.UTF8.GetByteCount(line) > MaxFileBytes)
                {
                    var previous = Path.Combine(_directory,
                        $"diagnostic-{DateTime.UtcNow:yyyyMMddTHHmmssfff}-{Guid.NewGuid():N}.txt");
                    File.Move(current, previous);
                }
                File.AppendAllText(current, line, Encoding.UTF8);
                Prune();
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    public string ExportReport()
    {
        lock (_gate)
        {
            Prune();
            var text = new StringBuilder();
            foreach (var file in ManagedFiles().OrderBy(file => file.Name == CurrentFile ? DateTime.MaxValue
                : file.LastWriteTimeUtc).ThenBy(file => file.Name, StringComparer.Ordinal))
            {
                text.Append(File.ReadAllText(file.FullName));
            }
            return RedactPersonalPaths(text.ToString());
        }
    }

    public void Clear()
    {
        lock (_gate)
            foreach (var file in ManagedFiles()) file.Delete();
    }

    private FileInfo[] ManagedFiles()
    {
        if (!Directory.Exists(_directory)) return [];
        return new DirectoryInfo(_directory).EnumerateFiles("diagnostic*", SearchOption.TopDirectoryOnly)
            .Where(file => file.Name is CurrentFile or "diagnostic.log" or "diagnostic.1.log"
                || Regex.IsMatch(file.Name, @"\Adiagnostic-\d{8}T\d{9}-[0-9a-f]{32}\.txt\z"))
            .ToArray();
    }

    private void Prune()
    {
        var files = ManagedFiles();
        var cutoff = DateTime.UtcNow - Retention;
        foreach (var file in files.Where(file => file.LastWriteTimeUtc < cutoff)) file.Delete();
        files = ManagedFiles();
        var total = files.Sum(file => file.Length);
        foreach (var file in files.OrderBy(file => file.LastWriteTimeUtc).ThenBy(file => file.Name, StringComparer.Ordinal))
        {
            if (total <= MaxTotalBytes) break;
            total -= file.Length;
            file.Delete();
        }
    }

    private static string Format(DiagnosticEvent entry) =>
        $"{entry.Time:yyyy-MM-dd HH:mm:ss zzz} [{entry.Level.ToString().ToUpperInvariant()}] [{entry.Operation}] {entry.Message}" +
        (entry.Details is null ? "" : $" | {entry.Details}");

    private static string Clean(string value)
    {
        value = value.Replace('\r', ' ').Replace('\n', ' ');
        value = Regex.Replace(value, @"(?i)\bBearer\s+\S+", "Bearer [redacted]");
        value = Regex.Replace(value, @"(?i)(authorization|bearer|token|password|secret|api[_-]?key)\s*[:=]\s*\S+", "$1=[redacted]");
        return value.Length > 1000 ? value[..1000] + "…" : value;
    }

    internal static string RedactPersonalPaths(string text)
    {
        text = Regex.Replace(text, @"(?im)((?:Путь результата|Output path):\s*)[^\r\n]+", "$1[redacted]");
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!string.IsNullOrWhiteSpace(home))
            text = text.Replace(home, "[home directory]", StringComparison.OrdinalIgnoreCase)
                .Replace(home.Replace('\\', '/'), "[home directory]", StringComparison.OrdinalIgnoreCase);
        text = Regex.Replace(text, @"(?i)(?:[A-Z]:[/\\]Users[/\\]|/home/|/Users/)[^/\\\s]+", "[home directory]");
        return text;
    }
}
