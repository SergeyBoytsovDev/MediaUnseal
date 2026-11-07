using System;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace MediaUnseal.Services;

internal sealed class SingleInstanceCoordinator : IDisposable
{
    private readonly Mutex _mutex;
    private readonly string _pipeName;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly object _activationLock = new();
    private Task? _server;
    private Action? _activate;
    private int _pending;
    private bool _ownsMutex;

    internal SingleInstanceCoordinator(string? testScope = null)
    {
        var user = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (string.IsNullOrEmpty(user)) user = Environment.UserName;
        if (OperatingSystem.IsWindows()) user = user.ToUpperInvariant();
        var identity = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(user))).Substring(0, 24);
        var suffix = testScope is null ? identity : identity + "." + testScope;
        _pipeName = "MediaUnseal." + suffix;
        _mutex = new Mutex(false, OperatingSystem.IsWindows() ? "Global\\" + _pipeName : _pipeName);
    }

    internal bool TryAcquire()
    {
        if (_ownsMutex) return true;
        try { _ownsMutex = _mutex.WaitOne(0); }
        catch (AbandonedMutexException) { _ownsMutex = true; }
        return _ownsMutex;
    }

    internal void StartServer()
    {
        if (!_ownsMutex || _server is not null) throw new InvalidOperationException("The instance server requires the primary mutex.");
        _server = Task.Run(() => ServeAsync(_shutdown.Token));
    }

    internal void SetActivationHandler(Action activate)
    {
        bool pending;
        lock (_activationLock)
        {
            _activate = activate;
            pending = _pending != 0;
            _pending = 0;
        }
        if (pending) activate();
    }

    internal async Task<bool> NotifyPrimaryAsync(TimeSpan timeout)
    {
        using var limit = new CancellationTokenSource(timeout);
        try
        {
            using var client = new NamedPipeClientStream(".", _pipeName, PipeDirection.InOut,
                PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            await client.ConnectAsync(limit.Token).ConfigureAwait(false);
            var response = new byte[4];
            await client.ReadExactlyAsync(response, limit.Token).ConfigureAwait(false);
            if (OperatingSystem.IsWindows()) AllowSetForegroundWindow(BitConverter.ToInt32(response));
            await client.WriteAsync(new byte[] { 1 }, limit.Token).ConfigureAwait(false);
            await client.FlushAsync(limit.Token).ConfigureAwait(false);
            var acknowledgment = new byte[1];
            await client.ReadExactlyAsync(acknowledgment, limit.Token).ConfigureAwait(false);
            return acknowledgment[0] == 1;
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or TimeoutException)
        { return false; }
    }

    private async Task ServeAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                using var server = new NamedPipeServerStream(_pipeName, PipeDirection.InOut, 1,
                    PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await server.WaitForConnectionAsync(cancellationToken).ConfigureAwait(false);
                await server.WriteAsync(BitConverter.GetBytes(Environment.ProcessId), cancellationToken).ConfigureAwait(false);
                await server.FlushAsync(cancellationToken).ConfigureAwait(false);
                var command = new byte[1];
                await server.ReadExactlyAsync(command, cancellationToken).ConfigureAwait(false);
                if (command[0] != 1) continue;
                Action? activate;
                lock (_activationLock)
                {
                    activate = _activate;
                    if (activate is null) _pending = 1;
                }
                activate?.Invoke();
                await server.WriteAsync(new byte[] { 1 }, cancellationToken).ConfigureAwait(false);
                await server.FlushAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { break; }
            catch (IOException) when (!cancellationToken.IsCancellationRequested) { }
        }
    }

    public void Dispose()
    {
        _shutdown.Cancel();
        try
        {
            try { _server?.GetAwaiter().GetResult(); }
            catch (OperationCanceledException) { }
        }
        finally
        {
            if (_ownsMutex) _mutex.ReleaseMutex();
            _mutex.Dispose();
            _shutdown.Dispose();
        }
    }

    [DllImport("user32.dll")]
    private static extern bool AllowSetForegroundWindow(int processId);
}
