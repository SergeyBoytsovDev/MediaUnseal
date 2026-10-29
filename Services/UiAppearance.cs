using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using Avalonia;
using Avalonia.Media;

namespace TrackExtractor.Services;

internal sealed class UiPreferences
{
    public string Theme { get; set; } = UiAppearance.DefaultTheme;
    public string Language { get; set; } = LocalizationManager.DefaultLanguage;
    public double Width { get; set; } = 1280;
    public double Height { get; set; } = 900;
    public int? X { get; set; }
    public int? Y { get; set; }
    public double[] ColumnWidths { get; set; } = [36, 86, 54, 150, 115, 104, 163, 129];
    public string? OutputDirectory { get; set; }
}

internal sealed class UiAppearance
{
    internal const string DefaultTheme = "Studio Dark";
    internal static readonly string[] ThemeNames =
    [
        DefaultTheme, "Resolve-inspired Workspace", "Adobe Clean Pro",
        "Industrial Utility", "Hybrid Modern Panel"
    ];

    private readonly string _path;
    internal UiPreferences Preferences { get; private set; }

    internal UiAppearance(string? path = null)
    {
        _path = path ?? Path.Combine(UserConfigDirectory(), "ui.json");
        try
        {
            Preferences = File.Exists(_path)
                ? JsonSerializer.Deserialize<UiPreferences>(File.ReadAllText(_path)) ?? new UiPreferences()
                : new UiPreferences();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            Preferences = new UiPreferences();
        }
        if (Array.IndexOf(ThemeNames, Preferences.Theme) < 0) Preferences.Theme = DefaultTheme;
        if (Array.IndexOf(LocalizationManager.AvailableLanguages, Preferences.Language) < 0)
            Preferences.Language = LocalizationManager.DefaultLanguage;
        if (!double.IsFinite(Preferences.Width) || Preferences.Width is < 960 or > 6000) Preferences.Width = 1280;
        if (!double.IsFinite(Preferences.Height) || Preferences.Height is < 680 or > 4000) Preferences.Height = 900;
        if (Preferences.ColumnWidths is not { Length: 8 }
            || Array.Exists(Preferences.ColumnWidths, width => !double.IsFinite(width) || width is < 34 or > 1000))
            Preferences.ColumnWidths = new UiPreferences().ColumnWidths;
        if (!IsExistingAbsoluteDirectory(Preferences.OutputDirectory))
            Preferences.OutputDirectory = DefaultOutputDirectory();
    }

    internal void SetTheme(string theme)
    {
        if (Array.IndexOf(ThemeNames, theme) < 0) theme = DefaultTheme;
        Preferences.Theme = theme;
        Apply(theme);
        Save();
    }

    internal bool SetLanguage(string language)
    {
        if (Array.IndexOf(LocalizationManager.AvailableLanguages, language) < 0) return false;
        Preferences.Language = language;
        Save();
        return true;
    }

    internal void Apply() => Apply(Preferences.Theme);

    internal void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            var temporary = _path + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(Preferences));
            File.Move(temporary, _path, true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    private static string UserConfigDirectory()
    {
        if (OperatingSystem.IsWindows())
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "TrackExtractor");
        if (OperatingSystem.IsMacOS())
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                "Library", "Application Support", "TrackExtractor");
        var xdg = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
        return Path.Combine(!string.IsNullOrWhiteSpace(xdg) && Path.IsPathRooted(xdg)
            ? xdg : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config"),
            "TrackExtractor");
    }

    internal static string DefaultOutputDirectory()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        string? downloads = null;
        if (OperatingSystem.IsWindows())
        {
            var id = new Guid("374DE290-123F-4565-9164-39C4925E467B");
            if (SHGetKnownFolderPath(in id, 0, IntPtr.Zero, out var pointer) == 0)
            {
                try { downloads = Marshal.PtrToStringUni(pointer); }
                finally { Marshal.FreeCoTaskMem(pointer); }
            }
        }
        else if (OperatingSystem.IsLinux())
        {
            var configHome = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
            var config = Path.Combine(!string.IsNullOrWhiteSpace(configHome) && Path.IsPathRooted(configHome)
                ? configHome : Path.Combine(home, ".config"), "user-dirs.dirs");
            try
            {
                if (File.Exists(config))
                    foreach (var line in File.ReadLines(config))
                    {
                        if (!line.TrimStart().StartsWith("XDG_DOWNLOAD_DIR=", StringComparison.Ordinal)) continue;
                        var value = line[(line.IndexOf('=') + 1)..].Trim().Trim('"');
                        downloads = value.StartsWith("$HOME/", StringComparison.Ordinal)
                            ? Path.Combine(home, value[6..]) : value == "$HOME" ? home : value;
                        break;
                    }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
        if (IsExistingAbsoluteDirectory(downloads))
            return Path.GetFullPath(downloads!);
        if (!string.IsNullOrWhiteSpace(home))
        {
            var conventional = Path.Combine(home, "Downloads");
            if (IsExistingAbsoluteDirectory(conventional)) return Path.GetFullPath(conventional);
        }
        return !string.IsNullOrWhiteSpace(home) && Directory.Exists(home) ? home : Environment.CurrentDirectory;
    }

    private static bool IsExistingAbsoluteDirectory(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        try { return Path.IsPathRooted(path) && Directory.Exists(path); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { return false; }
    }

    [DllImport("shell32.dll")]
    private static extern int SHGetKnownFolderPath(in Guid folderId, uint flags, IntPtr token, out IntPtr path);

    private static void Apply(string name)
    {
        if (Application.Current is null) return;
        var palette = name switch
        {
            "Resolve-inspired Workspace" => new Palette("#111419", "#1B1F26", "#171B21", "#272D36", "#F0F1F3", "#A5ADB9", "#DD3338", "#AD272E", "#0C1118", 1, 2),
            "Adobe Clean Pro" => new Palette("#171821", "#232431", "#20212C", "#414352", "#F2F0F8", "#B9B4C8", "#A45ADB", "#7442B4", "#1B1D29", 5, 4),
            "Industrial Utility" => new Palette("#111514", "#1B211F", "#171D1B", "#4E6057", "#EFF7F0", "#AFC3B5", "#39B85B", "#247B42", "#111A16", 0, 1),
            "Hybrid Modern Panel" => new Palette("#101A23", "#1D2B36", "#172631", "#3E5C6E", "#E7F6F8", "#A8C4CB", "#27B9C1", "#18818D", "#14232D", 6, 5),
            _ => new Palette("#14171B", "#22262D", "#1B2026", "#414850", "#F0F2F3", "#ABB4BD", "#F5A623", "#AB6E16", "#171C22", 1, 2)
        };
        var resources = Application.Current.Resources;
        resources["AppBackground"] = Brush(palette.Background);
        resources["PanelBackground"] = Brush(palette.Panel);
        resources["PanelAltBackground"] = Brush(palette.PanelAlt);
        resources["HeaderBackground"] = Brush(palette.Header);
        resources["LineBrush"] = Brush(palette.Line);
        resources["MainTextBrush"] = Brush(palette.Text);
        resources["MutedTextBrush"] = Brush(palette.Muted);
        resources["AccentBrush"] = Brush(palette.Accent);
        resources["AccentHoverBrush"] = Brush(palette.AccentHover);
        resources["SystemAccentColor"] = Color.Parse(palette.Accent);
        resources["RowBackground"] = Brush(palette.Row);
        resources["RowSelectedBackground"] = Brush(palette.AccentHover);
        resources["SuccessBrush"] = Brush(name == "Industrial Utility" ? "#59D879" : "#43C98A");
        resources["WarningBrush"] = Brush("#F2AE3D");
        resources["ErrorBrush"] = Brush("#EF6874");
        resources["InfoBrush"] = Brush(name == "Adobe Clean Pro" ? "#BD9BE6" : "#86B6EC");
        resources["PanelCorner"] = new CornerRadius(palette.Corner);
        resources["ControlCorner"] = new CornerRadius(palette.ControlCorner);
        resources["PanelGap"] = new Thickness(palette.Corner >= 5 ? 10 : 6);
    }

    private static SolidColorBrush Brush(string value) => new(Color.Parse(value));

    private sealed record Palette(string Background, string Panel, string PanelAlt, string Line,
        string Text, string Muted, string Accent, string AccentHover, string Row,
        double Corner, double ControlCorner)
    {
        internal string Header => PanelAlt;
    }
}
