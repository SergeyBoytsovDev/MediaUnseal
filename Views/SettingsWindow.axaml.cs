using System;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia;
using Avalonia.Interactivity;
using Avalonia.Threading;
using MediaUnseal.Services;
using MediaUnseal.ViewModels;

namespace MediaUnseal.Views;

public partial class SettingsWindow : Window
{
    private readonly MainViewModel _viewModel;
    private readonly UiAppearance _appearance;
    private readonly DependencyPanel _installer;
    private readonly LocalizationManager _localization;
    private readonly UpdateService _updates;
    private CancellationTokenSource? _downloadCancellation;
    private string? _licensePaths;
    private string? _detectedLicense;
    private CancellationTokenSource? _licenseCancellation;
    private bool _scaleSelectionReady;
    private bool _manualUpdateCheckRequested;
    private string? _aboutErrorKey;

    public SettingsWindow() : this(new MainViewModel(), new UiAppearance(), CancellationToken.None) { }

    internal SettingsWindow(MainViewModel viewModel, UiAppearance appearance, CancellationToken cancellationToken,
        UpdateService? updates = null)
    {
        InitializeComponent();
        UiScale.Attach(this);
        _viewModel = viewModel;
        _appearance = appearance;
        InterfaceScaleBox.SelectedIndex = appearance.Preferences.InterfaceScale == 0 ? 0
            : Math.Clamp((appearance.Preferences.InterfaceScale - 75 + 2) / 5 + 1, 1, 11);
        _scaleSelectionReady = true;
        _localization = LocalizationManager.Active;
        _updates = updates ?? (Application.Current as App)?.Updates ?? new UpdateService();
        _updates.Changed += UpdateResultChanged;
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
            _downloadCancellation?.Cancel();
            _updates.Changed -= UpdateResultChanged;
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
        AboutFooterText.Text = _localization.Get("SettingsAboutFooter");
        SizeUpdateButton();
        RefreshUpdateResult();
        if (AboutActionError.IsVisible && _aboutErrorKey is { } key)
            AboutActionError.Text = _localization.Get(key);
        FfmpegLicenseText.Text = _detectedLicense ?? _localization.Get("SettingsFfmpegLicense");
    }

    private void SizeUpdateButton()
    {
        var label = new TextBlock
        {
            FontFamily = UpdateButton.FontFamily,
            FontSize = UpdateButton.FontSize,
            FontWeight = UpdateButton.FontWeight
        };
        double WidthFor(string key)
        {
            label.Text = _localization.Get(key);
            label.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            return label.DesiredSize.Width;
        }
        UpdateButton.Width = Math.Ceiling(Math.Max(WidthFor("SettingsCheckUpdates"),
            WidthFor("UpdateDownload")) + UpdateButton.Padding.Left + UpdateButton.Padding.Right
            + UpdateButton.BorderThickness.Left + UpdateButton.BorderThickness.Right + 8);
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

    private void UpdateResultChanged(UpdateCheckResult result) =>
        Dispatcher.UIThread.Post(RefreshUpdateResult);

    private void RefreshUpdateResult()
    {
        var result = _updates.Result;
        UpdateButton.Content = _localization.Get(_manualUpdateCheckRequested && result.State == UpdateCheckState.Available
            ? "UpdateDownload" : "SettingsCheckUpdates");
        UpdateButton.IsEnabled = result.State != UpdateCheckState.Checking && _downloadCancellation is null;
        var showResult = _manualUpdateCheckRequested && _downloadCancellation is null;
        UpdateCurrentStatus.IsVisible = showResult && result.State == UpdateCheckState.Current;
        UpdateAvailableStatus.IsVisible = showResult && result.State == UpdateCheckState.Available;
        UpdateCheckingStatus.IsVisible = showResult && result.State == UpdateCheckState.Checking;
        UpdateErrorStatus.IsVisible = showResult && result.State == UpdateCheckState.Failed;
        if (result.Manifest is { } manifest && result.State == UpdateCheckState.Available)
            UpdateAvailableStatus.Text = _localization.Format("UpdateAvailableVersion", manifest.Version.ToString(3));
    }

    private async void UpdateButton_Click(object? sender, RoutedEventArgs e)
    {
        if (_manualUpdateCheckRequested && _updates.Result.State == UpdateCheckState.Available)
            DownloadUpdate_Click(sender, e);
        else
            await CheckForUpdatesAsync();
    }

    internal async Task CheckForUpdatesAsync(Uri? manifestUri = null)
    {
        _manualUpdateCheckRequested = true;
        AboutActionError.IsVisible = false;
        _aboutErrorKey = null;
        var result = await _updates.CheckAsync(manifestUri: manifestUri);
        if (result.State == UpdateCheckState.Failed)
            _viewModel.Diagnostics.Record(DiagnosticLevel.Warn, "Updates", "Manual update check failed.", result.Error);
    }

    private async void DownloadUpdate_Click(object? sender, RoutedEventArgs e)
    {
        if (_downloadCancellation is not null || _updates.Result.Manifest is not { } manifest ||
            _updates.Result.State != UpdateCheckState.Available) return;
        if (_viewModel.IsBusy || _viewModel.IsAnalyzing || _viewModel.IsExporting)
        {
            _aboutErrorKey = "UpdateBusy";
            AboutActionError.Text = _localization.Get(_aboutErrorKey);
            AboutActionError.IsVisible = true;
            return;
        }
        var cancellation = new CancellationTokenSource();
        _downloadCancellation = cancellation;
        UpdateButton.IsEnabled = false;
        UpdateProgress.Text = _localization.Format("UpdateDownloadProgress", 0);
        UpdateProgress.IsVisible = true;
        RefreshUpdateResult();
        AboutActionError.IsVisible = false;
        try
        {
            var kind = UpdateInstaller.DetectCurrentPackage();
            if (kind != UpdatePackageKind.WindowsInstaller && !UpdateInstaller.CanReplaceCurrentInstallation())
            {
                _aboutErrorKey = "UpdateManualInstall";
                AboutActionError.Text = _localization.Get(_aboutErrorKey);
                await ExternalLinkLauncher.TryOpenAsync(new Uri(
                    "https://github.com/SergeyBoytsovDev/MediaUnseal/releases/tag/" + manifest.Tag));
                AboutActionError.IsVisible = true;
                return;
            }
            var progress = new Progress<double>(value =>
                UpdateProgress.Text = _localization.Format("UpdateDownloadProgress", value * 100));
            var path = await _updates.DownloadAsync(manifest, kind, progress, cancellation.Token);
            _viewModel.Diagnostics.Record(DiagnosticLevel.Info, "Updates",
                "Update package downloaded and verified.", $"Release={manifest.Tag}; package={kind}.");
            await UpdateInstaller.LaunchAsync(path, manifest, kind, cancellation.Token);
            (Application.Current?.ApplicationLifetime as Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime)?.Shutdown();
        }
        catch (OperationCanceledException)
        {
            _viewModel.Diagnostics.Record(DiagnosticLevel.Info, "Updates", "Update download cancelled.");
        }
        catch (Exception ex)
        {
            var details = ex switch
            {
                HttpRequestException http => "HTTP request failed; status=" + http.StatusCode,
                InvalidDataException invalid => invalid.Message,
                _ => ex.GetType().Name + "; HResult 0x" + ex.HResult.ToString("X8")
            };
            _viewModel.Diagnostics.Record(DiagnosticLevel.Error, "Updates", "Update failed.", details);
            _aboutErrorKey = "UpdateInstallFailed";
            AboutActionError.Text = _localization.Get(_aboutErrorKey);
            AboutActionError.IsVisible = true;
        }
        finally
        {
            cancellation.Dispose();
            _downloadCancellation = null;
            UpdateProgress.IsVisible = false;
            RefreshUpdateResult();
        }
    }

    private async void ExternalLink_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string target }
            || !Uri.TryCreate(target, UriKind.Absolute, out var uri)) return;
        AboutActionError.IsVisible = false;
        if (await ExternalLinkLauncher.TryOpenAsync(uri)) return;
        _aboutErrorKey = "SettingsOpenLinkFailed";
        AboutActionError.Text = _localization.Get(_aboutErrorKey);
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









