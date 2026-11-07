using System;
using System.IO;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.Controls;
using Avalonia.Threading;
using MediaUnseal.Services;
using MediaUnseal.ViewModels;
using MediaUnseal.Views;

namespace MediaUnseal;

public partial class App : Application
{
    internal static readonly FontFamily InterfaceFont = new(
        OperatingSystem.IsWindows() ? "Inter" : "fonts:Inter#Inter");
    private bool _startupUpdateCheckStarted;
    internal UiAppearance Appearance { get; } = new();
    internal LocalizationManager Localization { get; } = new();
    internal UpdateService Updates { get; } = new();
    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
        Resources["InterfaceFont"] = InterfaceFont;
    }

    public override void OnFrameworkInitializationCompleted()
    {
        Localization.SetLanguage(Appearance.Preferences.Language);
        Appearance.Apply();
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var viewModel = new MainViewModel();
            if (UpdateInstaller.ConsumePreviousFailure() is { } updateFailure)
                viewModel.Diagnostics.Record(DiagnosticLevel.Error, "Updates",
                    "Previous update failed.", updateFailure, userMessageKey: "UpdateInstallFailed");
            _ = Task.Run(async () =>
            {
                var root = Path.GetTempPath();
                void Report(string message) => viewModel.Diagnostics.Record(DiagnosticLevel.Warn, "Temporary files", message);
                await TemporaryDirectory.RecoverAsync(root, "MediaUnseal-preview-", Report);
                await TemporaryDirectory.RecoverAsync(root, "mediaunseal-copy-", Report);
                await TemporaryDirectory.RecoverAsync(root, "mediaunseal-probe-", Report);
                await TemporaryDirectory.RecoverAsync(root, "MediaUnseal-update-", Report);
                await Appearance.RecoverTemporaryFilesAsync(Report);
            });
            Appearance.SaveFailed += viewModel.ReportSettingsSaveFailure;
            Appearance.SaveRecovered += viewModel.ReportSettingsSaveRecovery;
            Appearance.TemporaryCleanupFailed += message =>
                viewModel.Diagnostics.Record(DiagnosticLevel.Warn, "Temporary files", message);
            viewModel.SetOutputDirectory(Appearance.Preferences.OutputDirectory!);
            var window = new MainWindow
            {
                DataContext = viewModel,
            };
            desktop.MainWindow = window;
            window.Opened += (_, _) =>
            {
                Program.Instance?.SetActivationHandler(() =>
                    Dispatcher.UIThread.Post(() => RestoreMainWindow(window)));
                if (!_startupUpdateCheckStarted)
                {
                    _startupUpdateCheckStarted = true;
                    _ = CheckUpdatesAtStartupAsync(viewModel);
                }
            };
        }

        base.OnFrameworkInitializationCompleted();
    }

    private async Task CheckUpdatesAtStartupAsync(MainViewModel viewModel)
    {
        var result = await Updates.CheckAsync();
        if (result.State == UpdateCheckState.Available && result.Manifest is { } manifest)
            viewModel.Diagnostics.Record(DiagnosticLevel.Info, "Updates",
                "A newer official release is available: " + manifest.Version.ToString(3),
                userMessageKey: "UpdateAvailableJournal",
                userMessageArgs: [manifest.Version.ToString(3)]);
        else if (result.State == UpdateCheckState.Failed)
            viewModel.Diagnostics.Record(DiagnosticLevel.Warn, "Updates",
                "Automatic update check failed.", result.Error);
    }
    internal static void RestoreMainWindow(Window window)
    {
        if (window.WindowState == WindowState.Minimized) window.WindowState = WindowState.Normal;
        window.Activate();
    }

    internal bool ChangeLanguage(string language)
    {
        if (!Appearance.SetLanguage(language)) return false;
        return Localization.SetLanguage(language);
    }
}





