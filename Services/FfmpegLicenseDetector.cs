using System;
using System.Diagnostics;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace MediaUnseal.Services;

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
            timeout.Token.ThrowIfCancellationRequested();
            if (!process.Start()) return null;
            try
            {
                var output = FfmpegTools.ReadOutputAsync(process.StandardOutput, timeout.Token);
                var errors = FfmpegTools.ReadOutputAsync(process.StandardError, timeout.Token);
                await Task.WhenAll(output, errors, process.WaitForExitAsync(timeout.Token)).ConfigureAwait(false);
                var text = await output.ConfigureAwait(false) + await errors.ConfigureAwait(false);
                return process.ExitCode == 0 ? Parse(text) : null;
            }
            catch
            {
                await FfmpegTools.KillAsync(process).ConfigureAwait(false);
                throw;
            }
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException
            or System.ComponentModel.Win32Exception or UnauthorizedAccessException
            or NotSupportedException or OperationCanceledException)
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
