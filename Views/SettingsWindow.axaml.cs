using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using TrackExtractor.Services;
using TrackExtractor.ViewModels;

namespace TrackExtractor.Views;

public partial class SettingsWindow : Window
{
    private readonly MainViewModel _viewModel;
    private readonly UiAppearance _appearance;
    private readonly DependencyPanel _installer;

    public SettingsWindow() : this(new MainViewModel(), new UiAppearance(), CancellationToken.None) { }

    internal SettingsWindow(MainViewModel viewModel, UiAppearance appearance, CancellationToken cancellationToken)
    {
        InitializeComponent();
        _viewModel = viewModel;
        _appearance = appearance;
        DataContext = viewModel;
        _installer = new DependencyPanel(viewModel, cancellationToken);
        _installer.ContinueRequested += (_, _) => Close();
        InstallerHost.Content = _installer;
        VersionText.Text = $"Версия: {typeof(App).Assembly.GetName().Version?.ToString() ?? "не указана"}";
        foreach (var card in ThemeOptions.Children.OfType<Border>())
        {
            if (card.Child is not RadioButton option) continue;
            option.IsChecked = Equals(option.Tag, appearance.Preferences.Theme);
            card.Classes.Set("selected", option.IsChecked == true);
            card.PointerPressed += (_, _) => option.IsChecked = true;
            option.PropertyChanged += (_, args) =>
            {
                if (args.Property != RadioButton.IsCheckedProperty) return;
                card.Classes.Set("selected", option.IsChecked == true);
                if (option.IsChecked == true && option.Tag is string theme && _appearance.Preferences.Theme != theme)
                    _appearance.SetTheme(theme);
            };
        }
        SelectSection(0);
        Closing += SettingsWindow_Closing;
    }

    internal void SelectSection(int index)
    {
        SectionsList.SelectedIndex = index;
        InstallerSection.IsVisible = index == 0;
        AppearanceSection.IsVisible = index == 1;
        LanguageSection.IsVisible = index == 2;
        FormatsSection.IsVisible = index == 3;
        AboutSection.IsVisible = index == 4;
        SettingsContentScroll.Offset = default;
    }

    internal void SetShowContinue(bool value) => _installer.SetShowContinue(value);

    private void Sections_SelectionChanged(object? sender, SelectionChangedEventArgs e) =>
        SelectSection(SectionsList.SelectedIndex);

    private async void SettingsWindow_Closing(object? sender, WindowClosingEventArgs e)
    {
        if (!_viewModel.IsBusy) return;
        e.Cancel = true;
        _viewModel.CancelOperation();
        await _viewModel.WaitForIdleAsync();
        Close();
    }
}
