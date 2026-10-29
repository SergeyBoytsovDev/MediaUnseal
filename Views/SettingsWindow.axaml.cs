using System;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia;
using Avalonia.Interactivity;
using TrackExtractor.Services;
using TrackExtractor.ViewModels;

namespace TrackExtractor.Views;

public partial class SettingsWindow : Window
{
    private readonly MainViewModel _viewModel;
    private readonly UiAppearance _appearance;
    private readonly DependencyPanel _installer;
    private readonly LocalizationManager _localization;

    public SettingsWindow() : this(new MainViewModel(), new UiAppearance(), CancellationToken.None) { }

    internal SettingsWindow(MainViewModel viewModel, UiAppearance appearance, CancellationToken cancellationToken)
    {
        InitializeComponent();
        _viewModel = viewModel;
        _appearance = appearance;
        _localization = LocalizationManager.Active;
        DataContext = viewModel;
        _installer = new DependencyPanel(viewModel, cancellationToken);
        _installer.ContinueRequested += (_, _) => Close();
        InstallerHost.Content = _installer;
        UpdateVersionText();
        _localization.PropertyChanged += Localization_PropertyChanged;
        Closed += (_, _) => _localization.PropertyChanged -= Localization_PropertyChanged;
        AddAdditionalLanguageCards();
        foreach (var card in LanguageOptions.Children.OfType<Border>())
        {
            if (card.Tag is not string language || card.Child is not Grid grid ||
                grid.Children.OfType<RadioButton>().FirstOrDefault() is not { } option) continue;
            var available = Array.IndexOf(LocalizationManager.AvailableLanguages, language) >= 0;
            option.GroupName = "Languages";
            option.IsEnabled = available;
            option.IsChecked = language == appearance.Preferences.Language;
            card.Opacity = available ? 1 : 0.55;
            card.Classes.Set("selected", option.IsChecked == true);
            card.PointerPressed += (_, _) => { if (available) option.IsChecked = true; };
            option.PropertyChanged += (_, args) =>
            {
                if (args.Property != RadioButton.IsCheckedProperty) return;
                card.Classes.Set("selected", option.IsChecked == true);
                if (option.IsChecked != true || _appearance.Preferences.Language == language) return;
                if (Application.Current is App app) app.ChangeLanguage(language);
                else _appearance.SetLanguage(language);
            };
        }
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

    private void AddAdditionalLanguageCards()
    {
        var shown = LanguageOptions.Children.OfType<Border>()
            .Select(card => card.Tag as string).OfType<string>().ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var code in LocalizationManager.AvailableLanguages.Where(code => !shown.Contains(code)))
        {
            var index = LanguageOptions.Children.Count;
            var row = index / 2;
            while (LanguageOptions.RowDefinitions.Count <= row)
                LanguageOptions.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            var culture = CultureInfo.GetCultureInfo(code);
            var card = new Border { Tag = code, Height = 60, BorderThickness = new Thickness(1),
                Padding = new Thickness(10, 6) };
            card.Classes.Add("language-card");
            var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
            var names = new StackPanel { Spacing = 2 };
            names.Children.Add(new TextBlock { Text = culture.NativeName, FontWeight = Avalonia.Media.FontWeight.SemiBold });
            var codeLabel = new TextBlock { Text = culture.Name, FontSize = 12 };
            codeLabel.Classes.Add("secondary");
            names.Children.Add(codeLabel);
            grid.Children.Add(names);
            var option = new RadioButton { VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center };
            Grid.SetColumn(option, 1);
            grid.Children.Add(option);
            card.Child = grid;
            Grid.SetRow(card, row);
            Grid.SetColumn(card, index % 2);
            LanguageOptions.Children.Add(card);
        }
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

    private void Localization_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == "Item[]")
        {
            UpdateVersionText();
            foreach (var card in LanguageOptions.Children.OfType<Border>())
                if (card.Tag is string language && card.Child is Grid grid &&
                    grid.Children.OfType<RadioButton>().FirstOrDefault() is { } option)
                    option.IsChecked = language == _appearance.Preferences.Language;
            _viewModel.RefreshInstallerLocalization();
        }
    }

    private void UpdateVersionText() => VersionText.Text = _localization.Format("VersionFormat",
        typeof(App).Assembly.GetName().Version?.ToString() ?? _localization.Get("VersionUnspecified"));

    private void OpenGuide_Click(object? sender, RoutedEventArgs e) => new MediaGuideWindow().Show(this);

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
