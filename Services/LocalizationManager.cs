using System;
using System.Collections;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Resources;
using System.Collections.Generic;
using Avalonia;

namespace MediaUnseal.Services;

internal sealed class LocalizationManager : INotifyPropertyChanged
{
    internal const string DefaultLanguage = "ru";
    internal static string[] AvailableLanguages => LanguagePackCatalog.AvailableLanguages;
    private static readonly ResourceManager Strings = new("MediaUnseal.Resources.Strings", typeof(LocalizationManager).Assembly);
    private static readonly LocalizationManager Fallback = new();
    internal static LocalizationManager Active => (Application.Current as App)?.Localization ?? Fallback;
    private CultureInfo _culture = CultureInfo.GetCultureInfo(DefaultLanguage);
    private readonly Dictionary<string, IReadOnlyDictionary<string, string>> _testPacks = new(StringComparer.OrdinalIgnoreCase);

    internal string Language => _culture.Name;
    public string this[string key] => Get(key);
    public event PropertyChangedEventHandler? PropertyChanged;

    internal bool SetLanguage(string? language)
    {
        if (language is null || !AvailableLanguages.Contains(language, StringComparer.OrdinalIgnoreCase)
            && !_testPacks.ContainsKey(language)) return false;
        var next = CultureInfo.GetCultureInfo(language!);
        _culture = next;
        ApplyResources();
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("Item[]"));
        return true;
    }

    internal string Get(string key)
    {
        if (_testPacks.TryGetValue(_culture.Name, out var testPack))
            return testPack.TryGetValue(key, out var testValue) ? testValue
                : throw new MissingManifestResourceException($"Отсутствует строка локализации: {key}.");
        var value = _culture.Name == DefaultLanguage ? Strings.GetString(key, _culture)
            : Strings.GetResourceSet(_culture, true, false)?.GetString(key);
        return value ?? throw new MissingManifestResourceException($"Отсутствует строка локализации: {key}.");
    }

    internal string Format(string key, params object?[] arguments) =>
        string.Format(_culture, Get(key), arguments);

    internal void ApplyResources()
    {
        if (Application.Current is null) return;
        if (_testPacks.TryGetValue(_culture.Name, out var testPack))
        {
            foreach (var (key, value) in testPack)
                Application.Current.Resources["Loc_" + key] = value;
            return;
        }
        var set = Strings.GetResourceSet(_culture, true, _culture.Name == DefaultLanguage)
            ?? throw new MissingManifestResourceException("Отсутствуют ресурсы языка интерфейса.");
        foreach (DictionaryEntry entry in set)
            if (entry.Key is string key && entry.Value is string value)
                Application.Current.Resources["Loc_" + key] = value;
    }

    // Test-only packs are validated but never exposed through AvailableLanguages or settings.
    internal bool RegisterTestPack(string code, IReadOnlyDictionary<string, string> strings)
    {
        if (LanguagePackCatalog.ValidateInterface(code, strings).Count != 0) return false;
        _testPacks[CultureInfo.GetCultureInfo(code).Name] = strings;
        return true;
    }

    internal void RemoveTestPack(string code)
    {
        if (Language == code) SetLanguage(DefaultLanguage);
        _testPacks.Remove(code);
    }
}
