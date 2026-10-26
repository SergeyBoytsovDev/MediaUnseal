using System;
using System.Threading;
using System.Threading.Tasks;
using TrackExtractor.Services;

namespace TrackExtractor.ViewModels;

public class MainViewModel : ViewModelBase
{
    private readonly FfmpegTools _tools = new();
    private bool _isBusy;
    private bool _isReady;
    private string _greeting = "Проверка FFmpeg и FFprobe…";
    private string _ffmpegStatus = "Проверка…";
    private string _ffprobeStatus = "Проверка…";
    private string _message = "";
    private string _installOption = "";
    private CancellationTokenSource? _operation;
    private TaskCompletionSource? _idle;
    private double _progress;
    private bool _progressIndeterminate = true;

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (SetProperty(ref _isBusy, value))
            {
                if (value) _idle = new(TaskCreationOptions.RunContinuationsAsynchronously);
                else _idle?.TrySetResult();
                OnPropertyChanged(nameof(CanAct));
            }
        }
    }

    public bool CanAct => !IsBusy;
    public bool NeedsTools => !IsReady;
    public bool IsReady
    {
        get => _isReady;
        private set
        {
            if (SetProperty(ref _isReady, value)) OnPropertyChanged(nameof(NeedsTools));
        }
    }
    public string Greeting { get => _greeting; private set => SetProperty(ref _greeting, value); }
    public string FFmpegStatus { get => _ffmpegStatus; private set => SetProperty(ref _ffmpegStatus, value); }
    public string FFprobeStatus { get => _ffprobeStatus; private set => SetProperty(ref _ffprobeStatus, value); }
    public string Message { get => _message; private set => SetProperty(ref _message, value); }
    public string InstallOption { get => _installOption; private set => SetProperty(ref _installOption, value); }
    public double Progress { get => _progress; private set => SetProperty(ref _progress, value); }
    public bool ProgressIndeterminate { get => _progressIndeterminate; private set => SetProperty(ref _progressIndeterminate, value); }
    public void CancelOperation() => _operation?.Cancel();
    public Task WaitForIdleAsync() => _idle?.Task ?? Task.CompletedTask;

    public void ReportError(string message) => Message = message;

    public async Task CheckAsync(CancellationToken cancellationToken)
    {
        if (IsBusy) return;
        using var operation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _operation = operation;
        cancellationToken = operation.Token;
        ProgressIndeterminate = true;
        IsBusy = true;
        try
        {
            await RefreshAsync(cancellationToken);
            if (!IsReady)
                InstallOption = await Task.Run(() => _tools.GetInstallOptionAsync(cancellationToken), cancellationToken);
        }
        finally { _operation = null; IsBusy = false; }
    }

    public async Task InstallAsync(CancellationToken cancellationToken)
    {
        if (IsBusy || IsReady) return;
        if (!OperatingSystem.IsWindows())
        {
            Message = InstallOption;
            return;
        }

        using var operation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _operation = operation;
        cancellationToken = operation.Token;
        ProgressIndeterminate = true;
        Progress = 0;
        IsBusy = true;
        try
        {
            var progress = new Progress<InstallProgress>(value =>
            {
                if (_operation != operation || operation.IsCancellationRequested) return;
                Message = value.Message;
                ProgressIndeterminate = !value.Percent.HasValue;
                Progress = value.Percent ?? 0;
            });
            Message = _tools.OfferDirectDownload ? "Подготовка загрузки с gyan.dev…" : "Выполняется установка через WinGet…";
            var result = await Task.Run(() => _tools.OfferDirectDownload
                ? _tools.DownloadWindowsAsync(progress, cancellationToken)
                : _tools.InstallOnWindowsAsync(cancellationToken), cancellationToken);
            await RefreshAsync(cancellationToken);
            Message = result.Success && !IsReady
                ? "Повторная проверка не подтвердила готовность обеих утилит. Повторите проверку или укажите пути вручную."
                : result.Message;
            if (!IsReady && _tools.OfferDirectDownload) InstallOption = result.Message;
        }
        finally { _operation = null; IsBusy = false; }
    }

    public async Task SetManualPathAsync(string name, string path, CancellationToken cancellationToken)
    {
        if (IsBusy) return;
        using var operation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _operation = operation;
        cancellationToken = operation.Token;
        ProgressIndeterminate = true;
        IsBusy = true;
        try
        {
            var error = await Task.Run(() => _tools.SetManualPathAsync(name, path, cancellationToken), cancellationToken);
            if (error is not null)
            {
                Message = error;
                return;
            }
            await RefreshAsync(cancellationToken);
        }
        finally { _operation = null; IsBusy = false; }
    }

    private async Task RefreshAsync(CancellationToken cancellationToken)
    {
        Message = "Проверка утилит…";
        IsReady = false;
        var status = await Task.Run(() => _tools.CheckAsync(cancellationToken), cancellationToken);
        FFmpegStatus = Format(status.FFmpeg);
        FFprobeStatus = Format(status.FFprobe);
        IsReady = status.IsReady;
        if (IsReady) InstallOption = "";
        Greeting = IsReady
            ? $"Готово. FFmpeg {Version(status.FFmpeg)}, FFprobe {Version(status.FFprobe)}."
            : "FFmpeg и FFprobe требуются для работы с медиафайлами.";
        Message = _tools.SettingsError ?? (IsReady ? "Обе утилиты готовы." : "Установите недостающие утилиты или укажите их пути.");
    }

    private static string Format(ToolStatus status) =>
        status.IsReady ? $"{status.Name}: {status.Details}\n{status.Path}" : $"{status.Name}: {status.Details}";

    private static string Version(ToolStatus status) => status.Details.Split(' ', StringSplitOptions.RemoveEmptyEntries)[2];
}
