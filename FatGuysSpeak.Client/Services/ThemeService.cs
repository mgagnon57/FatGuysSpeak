namespace FatGuysSpeak.Client.Services;

public static class ThemeService
{
    public const string Ember = "Ember";
    public const string Dark = "Dark";
    public const string Midnight = "Midnight";
    public const string OLED = "OLED";
    public const string Nord = "Nord";
    public const string Forest = "Forest";
    public const string Crimson = "Crimson";
    public const string Plum = "Plum";

    // Order shown in the Settings picker (Ember first — the default).
    public static readonly string[] Names = { Ember, Dark, Midnight, OLED, Nord, Forest, Crimson, Plum };

    private static readonly Dictionary<string, Dictionary<string, string>> Themes = new()
    {
        // Ember — clean red on near-black. Default.
        [Ember] = new()
        {
            ["ThemeBgPrimary"]     = "#0d0c0c",
            ["ThemeBgPanel"]       = "#161313",
            ["ThemeBgHeader"]      = "#080707",
            ["ThemeBgElevated"]    = "#211c1c",
            ["ThemeBgInput"]       = "#080707",
            ["ThemeBgActive"]      = "#2a0f12",
            ["ThemeDivider"]       = "#2b2323",
            ["ThemeAccent"]        = "#cc1124",
            ["ThemeAccentLight"]   = "#ff2a3d",
            ["ThemeAccentMuted"]   = "#2c0c11",
            ["ThemeTextPrimary"]   = "#ece7e3",
            ["ThemeTextSecondary"] = "#867d78",
            ["ThemeTextMuted"]     = "#544c49",
            ["ThemeDanger"]        = "#ff2a3d",
            ["ThemeWarning"]       = "#e89000",
            ["ThemeSuccess"]       = "#36b864",
        },
        [Dark] = new()
        {
            ["ThemeBgPrimary"]     = "#1a1a1a",
            ["ThemeBgPanel"]       = "#2a2a2a",
            ["ThemeBgHeader"]      = "#252525",
            ["ThemeBgElevated"]    = "#3a3a3a",
            ["ThemeBgInput"]       = "#141414",
            ["ThemeBgActive"]      = "#1e2a3a",
            ["ThemeDivider"]       = "#333333",
            ["ThemeAccent"]        = "#2d5f9e",
            ["ThemeAccentLight"]   = "#8ab4d4",
            ["ThemeAccentMuted"]   = "#1a3050",
            ["ThemeTextPrimary"]   = "#d0d0d0",
            ["ThemeTextSecondary"] = "#888888",
            ["ThemeTextMuted"]     = "#666666",
            ["ThemeDanger"]        = "#c0392b",
            ["ThemeWarning"]       = "#e0a030",
            ["ThemeSuccess"]       = "#43b581",
        },
        [Midnight] = new()
        {
            ["ThemeBgPrimary"]     = "#0e1016",
            ["ThemeBgPanel"]       = "#161b2a",
            ["ThemeBgHeader"]      = "#111622",
            ["ThemeBgElevated"]    = "#1f2540",
            ["ThemeBgInput"]       = "#0b0d14",
            ["ThemeBgActive"]      = "#1a2240",
            ["ThemeDivider"]       = "#1e2438",
            ["ThemeAccent"]        = "#5865f2",
            ["ThemeAccentLight"]   = "#a0a8f8",
            ["ThemeAccentMuted"]   = "#232a55",
            ["ThemeTextPrimary"]   = "#dce1f0",
            ["ThemeTextSecondary"] = "#7a88a8",
            ["ThemeTextMuted"]     = "#4d5a6e",
            ["ThemeDanger"]        = "#ed4245",
            ["ThemeWarning"]       = "#faa61a",
            ["ThemeSuccess"]       = "#3ba55d",
        },
        [OLED] = new()
        {
            ["ThemeBgPrimary"]     = "#000000",
            ["ThemeBgPanel"]       = "#0d0d0d",
            ["ThemeBgHeader"]      = "#111111",
            ["ThemeBgElevated"]    = "#181818",
            ["ThemeBgInput"]       = "#000000",
            ["ThemeBgActive"]      = "#141414",
            ["ThemeDivider"]       = "#1a1a1a",
            ["ThemeAccent"]        = "#2d5f9e",
            ["ThemeAccentLight"]   = "#8ab4d4",
            ["ThemeAccentMuted"]   = "#14233a",
            ["ThemeTextPrimary"]   = "#e0e0e0",
            ["ThemeTextSecondary"] = "#888888",
            ["ThemeTextMuted"]     = "#555555",
            ["ThemeDanger"]        = "#c0392b",
            ["ThemeWarning"]       = "#e0a030",
            ["ThemeSuccess"]       = "#43b581",
        },
        // Nord — cool arctic blue-grey.
        [Nord] = new()
        {
            ["ThemeBgPrimary"]     = "#2e3440",
            ["ThemeBgPanel"]       = "#3b4252",
            ["ThemeBgHeader"]      = "#2b303b",
            ["ThemeBgElevated"]    = "#434c5e",
            ["ThemeBgInput"]       = "#272c36",
            ["ThemeBgActive"]      = "#3b4860",
            ["ThemeDivider"]       = "#434c5e",
            ["ThemeAccent"]        = "#5e81ac",
            ["ThemeAccentLight"]   = "#88c0d0",
            ["ThemeAccentMuted"]   = "#3b4a5e",
            ["ThemeTextPrimary"]   = "#eceff4",
            ["ThemeTextSecondary"] = "#a9b3c4",
            ["ThemeTextMuted"]     = "#6b7488",
            ["ThemeDanger"]        = "#bf616a",
            ["ThemeWarning"]       = "#ebcb8b",
            ["ThemeSuccess"]       = "#a3be8c",
        },
        // Forest — green on deep green-black.
        [Forest] = new()
        {
            ["ThemeBgPrimary"]     = "#0f1410",
            ["ThemeBgPanel"]       = "#18211a",
            ["ThemeBgHeader"]      = "#0c110d",
            ["ThemeBgElevated"]    = "#243026",
            ["ThemeBgInput"]       = "#0c110d",
            ["ThemeBgActive"]      = "#16271b",
            ["ThemeDivider"]       = "#233027",
            ["ThemeAccent"]        = "#2e8b57",
            ["ThemeAccentLight"]   = "#43c97a",
            ["ThemeAccentMuted"]   = "#16301f",
            ["ThemeTextPrimary"]   = "#dde8df",
            ["ThemeTextSecondary"] = "#7d927f",
            ["ThemeTextMuted"]     = "#4a5a4d",
            ["ThemeDanger"]        = "#c0392b",
            ["ThemeWarning"]       = "#e0a030",
            ["ThemeSuccess"]       = "#43c97a",
        },
        // Crimson — deep cool red (cooler/darker than Ember's orange-red).
        [Crimson] = new()
        {
            ["ThemeBgPrimary"]     = "#140d0f",
            ["ThemeBgPanel"]       = "#1f1417",
            ["ThemeBgHeader"]      = "#100a0c",
            ["ThemeBgElevated"]    = "#2c1a1f",
            ["ThemeBgInput"]       = "#100a0c",
            ["ThemeBgActive"]      = "#2a1016",
            ["ThemeDivider"]       = "#2e1d22",
            ["ThemeAccent"]        = "#b01030",
            ["ThemeAccentLight"]   = "#e83a5c",
            ["ThemeAccentMuted"]   = "#2e0f16",
            ["ThemeTextPrimary"]   = "#f0dde2",
            ["ThemeTextSecondary"] = "#98828a",
            ["ThemeTextMuted"]     = "#5e4a50",
            ["ThemeDanger"]        = "#e83a5c",
            ["ThemeWarning"]       = "#e0a030",
            ["ThemeSuccess"]       = "#43b581",
        },
        // Plum — purple on dark.
        [Plum] = new()
        {
            ["ThemeBgPrimary"]     = "#140f1a",
            ["ThemeBgPanel"]       = "#1e1729",
            ["ThemeBgHeader"]      = "#110c18",
            ["ThemeBgElevated"]    = "#2a2040",
            ["ThemeBgInput"]       = "#110c18",
            ["ThemeBgActive"]      = "#241a3a",
            ["ThemeDivider"]       = "#2a2138",
            ["ThemeAccent"]        = "#8a4fff",
            ["ThemeAccentLight"]   = "#b08cff",
            ["ThemeAccentMuted"]   = "#251a3e",
            ["ThemeTextPrimary"]   = "#e6e0f0",
            ["ThemeTextSecondary"] = "#8b82a0",
            ["ThemeTextMuted"]     = "#564d6e",
            ["ThemeDanger"]        = "#e0506e",
            ["ThemeWarning"]       = "#e0a030",
            ["ThemeSuccess"]       = "#43b581",
        },
    };

    public static string CurrentTheme { get; private set; } = Ember;

    // Raised after a theme is applied, so platform-specific touch-ups (e.g. the WinUI Entry focus
    // underline, which DynamicResource can't reach) can refresh to the new accent.
    public static event Action? ThemeChanged;

    public static void Apply(string themeName)
    {
        if (!Themes.TryGetValue(themeName, out var colors)) return;
        CurrentTheme = themeName;
        var resources = Application.Current?.Resources;
        if (resources is null) return;
        foreach (var (key, hex) in colors)
            resources[key] = Color.FromArgb(hex);
        Preferences.Set("ui_theme", themeName);
        ThemeChanged?.Invoke();
    }

    // New key "ui_theme" (was "app_theme") so the Ember rebrand becomes the default for
    // everyone on update; a deliberate re-pick in Settings persists under the new key.
    public static void ApplyStored() =>
        Apply(Preferences.Get("ui_theme", Ember));

    /// <summary>Read a live theme color token from app resources (for value converters that
    /// can't use DynamicResource). Falls back to transparent if missing.</summary>
    public static Color Get(string key) =>
        Application.Current?.Resources is { } r && r.TryGetValue(key, out var v) && v is Color c
            ? c : Colors.Transparent;
}
