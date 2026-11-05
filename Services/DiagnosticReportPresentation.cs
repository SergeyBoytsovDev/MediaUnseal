using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace MediaUnseal.Services;

internal sealed record DiagnosticReportRow(string Kind, string Time, string Level, string Category,
    string Message, string Timestamp)
{
    public bool IsDate => Kind == "date";
    public bool IsStartup => Kind == "startup";
    public bool IsEntry => Kind == "entry";
    public bool IsInfo => Level == "INFO";
    public bool IsWarn => Level == "WARN";
    public bool IsError => Level == "ERROR";
}

internal static class DiagnosticReportPresentation
{
    private static readonly Regex Entry = new(
        @"^(?<stamp>\d{4}-\d\d-\d\d \d\d:\d\d:\d\d [+-]\d\d:\d\d) \[(?<level>INFO|WARN|ERROR)\] \[(?<category>[^\]]+)\] (?<message>.*)$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));

    internal static IReadOnlyList<DiagnosticReportRow> Parse(string report)
        => Parse(report.Split('\n'));

    internal static IReadOnlyList<DiagnosticReportRow> Parse(IEnumerable<string> lines)
    {
        var rows = new List<DiagnosticReportRow>();
        DateOnly? day = null;
        foreach (var line in lines)
        {
            if (line.Length == 0 || line == "\r") continue;
            var source = line.TrimEnd('\r');
            var match = Entry.Match(source);
            if (!match.Success || !DateTimeOffset.TryParseExact(match.Groups["stamp"].Value,
                "yyyy-MM-dd HH:mm:ss zzz", CultureInfo.InvariantCulture, DateTimeStyles.None, out var timestamp))
            {
                rows.Add(new("entry", "", "", "", source, ""));
                continue;
            }
            var currentDay = DateOnly.FromDateTime(timestamp.Date);
            if (currentDay != day)
            {
                rows.Add(new("date", "", "", "", currentDay.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), ""));
                day = currentDay;
            }
            var time = timestamp.ToString("HH:mm:ss", CultureInfo.InvariantCulture);
            var category = match.Groups["category"].Value;
            var message = match.Groups["message"].Value;
            if (category == "Startup" && message == "Application dependency check started.")
                rows.Add(new("startup", time, "", "", "▶ Media Unseal started · " + time,
                    match.Groups["stamp"].Value));
            rows.Add(new("entry", time, match.Groups["level"].Value, category, message,
                match.Groups["stamp"].Value));
        }
        return rows;
    }

    internal static string ToText(IReadOnlyList<DiagnosticReportRow> rows)
    {
        var result = new StringBuilder();
        foreach (var line in TextLines(rows)) result.AppendLine(line);
        return result.ToString();
    }

    internal static IEnumerable<string> TextLines(IReadOnlyList<DiagnosticReportRow> rows)
    {
        var categoryWidth = Math.Max(18, rows.Where(row => row.IsEntry).Select(row => row.Category.Length)
            .DefaultIfEmpty(0).Max());
        foreach (var row in rows)
        {
            if (row.IsDate) yield return "──────────── " + row.Message + " ────────────";
            else if (row.IsStartup) yield return row.Message + " ──────────────";
            else
            {
                var prefix = $"{row.Time,-8}  {row.Level,-5}  {row.Category.PadRight(categoryWidth)}  ";
                yield return prefix + row.Message;
            }
        }
    }
}
