using System;
using System.Diagnostics;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace TrackExtractor.Services;

internal static class FfmpegLicenseDetector
{
    internal static async Task<string?> DetectAsync(string executablePath, CancellationToken cancellationToken)
    {
        if (!File.Exists(executablePath)) return null;
        using var process = new Process();
        process.StartInfo = new ProcessStartInfo(executablePath)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        process.StartInfo.ArgumentList.Add("-L");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        try
        {
            if (!process.Start()) return null;
            var output = process.StandardOutput.ReadToEndAsync(timeout.Token);
            var errors = process.StandardError.ReadToEndAsync(timeout.Token);
            await process.WaitForExitAsync(timeout.Token);
            var text = await output + await errors;
            return process.ExitCode == 0 ? Parse(text) : null;
        }
        catch (OperationCanceledException)
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
            catch (InvalidOperationException) { }
            catch (System.ComponentModel.Win32Exception) { }
            return null;
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException
            or System.ComponentModel.Win32Exception or UnauthorizedAccessException
            or NotSupportedException)
        { return null; }
    }

    internal static string? Parse(string text)
    {
        var license = text.Contains("GNU Lesser General Public License", StringComparison.OrdinalIgnoreCase)
            ? "GNU LGPL"
            : text.Contains("GNU General Public License", StringComparison.OrdinalIgnoreCase)
                ? "GNU GPL" : null;
        if (license is null) return null;
        var version = Regex.Match(text, @"\bversion\s+(?<number>\d+(?:\.\d+)?)\b",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        return version.Success ? $"{license} v{version.Groups["number"].Value}" : license;
    }
}
