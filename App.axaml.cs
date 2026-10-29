using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using TrackExtractor.Services;
using TrackExtractor.ViewModels;
using TrackExtractor.Views;

namespace TrackExtractor;

public partial class App : Application
{
    internal UiAppearance Appearance { get; } = new();
    internal LocalizationManager Localization { get; } = new();
    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
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
