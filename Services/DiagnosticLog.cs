using System;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace TrackExtractor.Services;

public enum DiagnosticLevel { Info, Warn, Error }

public sealed record DiagnosticEvent(DateTimeOffset Time, DiagnosticLevel Level,
    string Operation, string Message, string? Details = null);

public sealed class DiagnosticLog
{
    private const long MaxFileBytes = 512 * 1024;
    private readonly string _directory;
    private readonly object _gate = new();
    public event Action<DiagnosticEvent>? EventRecorded;

    public DiagnosticLog(string directory) => _directory = directory;

    public void Record(DiagnosticLevel level, string operation, string message, string? details = null)
    {
        var entry = new DiagnosticEvent(DateTimeOffset.Now, level, Clean(operation),
            Clean(message), details is null ? null : Clean(details));
        EventRecorded?.Invoke(entry);
        var line = Format(entry) + Environment.NewLine;
        try
        {
            lock (_gate)
            {
                Directory.CreateDirectory(_directory);
                var current = Path.Combine(_directory, "diagnostic.log");
                if (File.Exists(current) && new FileInfo(current).Length + Encoding.UTF8.GetByteCount(line) > MaxFileBytes)
                {
                    var previous = Path.Combine(_directory, "diagnostic.1.log");
                    File.Move(current, previous, overwrite: true);
                }
                File.AppendAllText(current, line, Encoding.UTF8);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    public string ExportReport()
    {
        lock (_gate)
        {
            var text = new StringBuilder();
            foreach (var name in new[] { "diagnostic.1.log", "diagnostic.log" })
            {
                var path = Path.Combine(_directory, name);
                if (File.Exists(path)) text.Append(File.ReadAllText(path));
            }
            return RedactPersonalPaths(text.ToString());
        }
    }

    private static string Format(DiagnosticEvent entry) =>
        $"{entry.Time:yyyy-MM-dd HH:mm:ss zzz} [{entry.Level.ToString().ToUpperInvariant()}] [{entry.Operation}] {entry.Message}" +
        (entry.Details is null ? "" : $" | {entry.Details}");

    private static string Clean(string value)
    {
        value = value.Replace('\r', ' ').Replace('\n', ' ');
        value = Regex.Replace(value, @"(?i)\bBearer\s+\S+", "Bearer [скрыто]");
        value = Regex.Replace(value, @"(?i)(authorization|bearer|token|password|secret|api[_-]?key)\s*[:=]\s*\S+", "$1=[скрыто]");
        return value.Length > 1000 ? value[..1000] + "…" : value;
    }

    internal static string RedactPersonalPaths(string text)
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!string.IsNullOrWhiteSpace(home))
            text = text.Replace(home, "[домашний каталог]", StringComparison.OrdinalIgnoreCase)
                .Replace(home.Replace('\\', '/'), "[домашний каталог]", StringComparison.OrdinalIgnoreCase);
        text = Regex.Replace(text, @"(?i)(?:[A-Z]:[/\\]Users[/\\]|/home/|/Users/)[^/\\\s]+", "[домашний каталог]");
        return text;
    }
}
