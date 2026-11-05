using System;
using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using MediaUnseal.Services;

namespace MediaUnseal.Views;

public partial class SupportWindow : Window
{
    private const string CryptoAddress = "TXZYXQbwq2pp2VSRAtvfDiMZqpkcCKNFJ8";
    private string? _statusKey;

    public SupportWindow()
    {
        InitializeComponent();
        UiScale.Attach(this);
        CryptoAddressText.Text = CryptoAddress;
        RefreshLocalizedText();
        LocalizationManager.Active.PropertyChanged += LocalizationChanged;
        Closed += (_, _) => LocalizationManager.Active.PropertyChanged -= LocalizationChanged;
    }

    private void LocalizationChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == "Item[]") RefreshLocalizedText();
    }

    private void RefreshLocalizedText()
    {
        var localization = LocalizationManager.Active;
        SupportDescription.Text = localization.Format("SupportDescription",
            localization.Get("SettingsProductName"));
        if (_statusKey is not null) SupportStatus.Text = localization.Get(_statusKey);
        var showSbp = localization.Language.Split('-')[0].Equals("ru", StringComparison.OrdinalIgnoreCase);
        SbpCard.IsVisible = showSbp;
        SupportMethodsGrid.ColumnDefinitions[1].Width = showSbp
            ? new GridLength(1, GridUnitType.Star) : new GridLength(0);
        SupportMethodsGrid.ColumnSpacing = showSbp ? 12 : 0;
    }

    private void SetStatus(string key)
    {
        _statusKey = key;
        SupportStatus.Text = LocalizationManager.Active.Get(key);
    }

    private async void CopyCrypto_Click(object? sender, RoutedEventArgs e)
    {
        try
        {
            if (Clipboard is null) throw new InvalidOperationException();
            await Clipboard.SetTextAsync(CryptoAddress);
            SetStatus("SupportAddressCopied");
        }
        catch (Exception)
        { SetStatus("SupportCopyFailed"); }
    }

    private void Close_Click(object? sender, RoutedEventArgs e) => Close();
}
