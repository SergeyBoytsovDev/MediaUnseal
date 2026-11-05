using System;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia;
using Avalonia.Interactivity;
using Avalonia.Threading;
using TrackExtractor.Services;
using TrackExtractor.ViewModels;

namespace TrackExtractor.Views;

public partial class SettingsWindow : Window
{
    private readonly MainViewModel _viewModel;
    private readonly UiAppearance _appearance;
    private readonly DependencyPanel _installer;
    private readonly LocalizationManager _localization;
    private int _demoUpdateState;
    private string? _licensePaths;
    private string? _detectedLicense;
    private CancellationTokenSource? _licenseCancellation;
    private bool _scaleSelectionReady;

    public SettingsWindow() : this(new MainViewModel(), new UiAppearance(), CancellationToken.None) { }

    internal SettingsWindow(MainViewModel viewModel, UiAppearance appearance, CancellationToken cancellationToken)
    {
        InitializeComponent();
        UiScale.Attach(this);
        _viewModel = viewModel;
        _appearance = appearance;
        InterfaceScaleBox.SelectedIndex = appearance.Preferences.InterfaceScale == 0 ? 0
            : Math.Clamp((appearance.Preferences.InterfaceScale - 75 + 2) / 5 + 1, 1, 11);
        _scaleSelectionReady = true;
        _localization = LocalizationManager.Active;
        DataContext = viewModel;
        _installer = new DependencyPanel(viewModel, cancellationToken);
        _installer.ContinueRequested += (_, _) => Close();
        InstallerHost.Content = _installer;
        UpdateVersionText();
        _localization.PropertyChanged += Localization_PropertyChanged;
        _viewModel.PropertyChanged += ViewModel_PropertyChanged;
        Closed += (_, _) =>
        {
            _localization.PropertyChanged -= Localization_PropertyChanged;
            _viewModel.PropertyChanged -= ViewModel_PropertyChanged;
            _licenseCancellation?.Cancel();
        };
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

    private void InterfaceScale_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (!_scaleSelectionReady || InterfaceScaleBox.SelectedItem is not ComboBoxItem item
            || item.Tag is not string text || !int.TryParse(text, out var percent)) return;
        _appearance.SetInterfaceScale(percent);
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
        if (index == 4) RefreshFfmpegLicense();
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

    private void UpdateVersionText()
    {
        var version = typeof(App).Assembly.GetName().Version?.ToString(3)
            ?? _localization.Get("VersionUnspecified");
        VersionText.Text = _localization.Format("VersionFormat", version);
        TechnologyText.Text = _localization.Format("SettingsTechnology",
            Environment.Version.Major, typeof(Application).Assembly.GetName().Version?.ToString(3) ?? "—");
        AboutFooterText.Text = _localization.Format("SettingsAboutFooter",
            _localization.Get("SettingsDeveloperName"), _localization.Get("SettingsProductName"), version);
        UpdateButton.Content = _localization.Get(_demoUpdateState == 2
            ? "SettingsInstallUpdate" : "SettingsCheckUpdates");
        UpdateCurrentStatus.IsVisible = _demoUpdateState == 1;
        UpdateAvailableStatus.IsVisible = _demoUpdateState == 2;
        if (AboutActionError.IsVisible)
            AboutActionError.Text = _localization.Get("SettingsOpenLinkFailed");
        FfmpegLicenseText.Text = _detectedLicense ?? _localization.Get("SettingsFfmpegLicense");
    }

    private void ViewModel_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(MainViewModel.FFmpegExecutablePath)
            or nameof(MainViewModel.FFprobeExecutablePath))
            Dispatcher.UIThread.Post(() => { if (AboutSection.IsVisible) RefreshFfmpegLicense(); });
    }

    private async void RefreshFfmpegLicense()
    {
        var ffmpegPath = _viewModel.FFmpegExecutablePath;
        var ffprobePath = _viewModel.FFprobeExecutablePath;
        var paths = $"{ffmpegPath}\n{ffprobePath}";
        if (paths == _licensePaths) return;
        _licensePaths = paths;
        _licenseCancellation?.Cancel();
        _detectedLicense = null;
        FfmpegLicenseText.Text = _localization.Get("SettingsFfmpegLicense");
        if (ffmpegPath is null || ffprobePath is null) return;
        var cancellation = new CancellationTokenSource();
        _licenseCancellation = cancellation;
        try
        {
            var labels = await Task.WhenAll(
                FfmpegLicenseDetector.DetectAsync(ffmpegPath, cancellation.Token),
                FfmpegLicenseDetector.DetectAsync(ffprobePath, cancellation.Token));
            if (cancellation.IsCancellationRequested || labels[0] is null || labels[1] is null) return;
            _detectedLicense = labels[0] == labels[1] ? labels[0]
                : $"FFmpeg: {labels[0]} · FFprobe: {labels[1]}";
            FfmpegLicenseText.Text = _detectedLicense;
        }
        finally
        {
            if (ReferenceEquals(_licenseCancellation, cancellation)) _licenseCancellation = null;
            cancellation.Dispose();
        }
    }

    private void UpdateButton_Click(object? sender, RoutedEventArgs e)
    {
        _demoUpdateState = _demoUpdateState == 2 ? 1 : _demoUpdateState + 1;
        UpdateVersionText();
    }

    private async void ExternalLink_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string target }
            || !Uri.TryCreate(target, UriKind.Absolute, out var uri)) return;
        AboutActionError.IsVisible = false;
        if (await ExternalLinkLauncher.TryOpenAsync(uri)) return;
        AboutActionError.Text = _localization.Get("SettingsOpenLinkFailed");
        AboutActionError.IsVisible = true;
    }

    private void Support_Click(object? sender, RoutedEventArgs e) => new SupportWindow().Show(this);

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
