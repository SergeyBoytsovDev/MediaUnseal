using System;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using MediaUnseal.Services;
using MediaUnseal.ViewModels;
using MediaUnseal.Views;

namespace MediaUnseal;

public partial class App : Application
{
    internal static readonly FontFamily InterfaceFont = new(
        OperatingSystem.IsWindows() ? "Inter" : "fonts:Inter#Inter");
    internal UiAppearance Appearance { get; } = new();
    internal LocalizationManager Localization { get; } = new();
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
            viewModel.SetOutputDirectory(Appearance.Preferences.OutputDirectory!);
            desktop.MainWindow = new MainWindow
            {
                DataContext = viewModel,
            };
        }

        base.OnFrameworkInitializationCompleted();
    }

    internal bool ChangeLanguage(string language)
    {
        if (!Appearance.SetLanguage(language)) return false;
        return Localization.SetLanguage(language);
    }
}
