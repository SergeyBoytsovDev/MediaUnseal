using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace TrackExtractor.Services;

internal static class ExternalLinkLauncher
{
    internal static async Task<bool> TryOpenAsync(Uri target)
    {
        if (target.Scheme is not ("https" or "mailto")) return false;
        try
        {
            if (OperatingSystem.IsWindows())
                return Process.Start(new ProcessStartInfo(target.AbsoluteUri) { UseShellExecute = true }) is not null;
            using var process = new Process();
            process.StartInfo = new ProcessStartInfo(OperatingSystem.IsMacOS() ? "open" : "xdg-open")
            { UseShellExecute = false, CreateNoWindow = true };
            process.StartInfo.ArgumentList.Add(target.AbsoluteUri);
            if (!process.Start()) return false;
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            try { await process.WaitForExitAsync(timeout.Token); }
            catch (OperationCanceledException) { return true; }
            return process.ExitCode == 0;
        }
        catch (Exception ex) when (ex is Win32Exception or IOException or UnauthorizedAccessException
            or InvalidOperationException or NotSupportedException)
        { return false; }
    }
}
