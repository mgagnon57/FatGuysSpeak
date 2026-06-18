namespace FatGuysSpeak.Client.Services;

public static class ThemeService
{
    public const string Ember = "Ember";
    public const string Dark = "Dark";
    public const string Midnight = "Midnight";
    public const string OLED = "OLED";

    private static readonly Dictionary<string, Dictionary<string, string>> Themes = new()
    {
        // Ember — red on warm-black, matches the landing page. Default.
        [Ember] = new()
        {
            ["ThemeBgPrimary"]     = "#101010",
            ["ThemeBgPanel"]       = "#191717",
            ["ThemeBgHeader"]      = "#0a0909",
            ["ThemeBgElevated"]    = "#222020",
            ["ThemeBgInput"]       = "#0a0909",
            ["ThemeBgActive"]      = "#241310",
            ["ThemeDivider"]       = "#292525",
            ["ThemeAccent"]        = "#d42d00",
            ["ThemeAccentLight"]   = "#f04010",
            ["ThemeAccentMuted"]   = "#2a1208",
            ["ThemeTextPrimary"]   = "#e9e4d9",
            ["ThemeTextSecondary"] = "#756e62",
            ["ThemeTextMuted"]     = "#4a443d",
            ["ThemeDanger"]        = "#d42d00",
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
    };

    public static string CurrentTheme { get; private set; } = Ember;

    public static void Apply(string themeName)
    {
        if (!Themes.TryGetValue(themeName, out var colors)) return;
        CurrentTheme = themeName;
        var resources = Application.Current?.Resources;
        if (resources is null) return;
        foreach (var (key, hex) in colors)
            resources[key] = Color.FromArgb(hex);
        Preferences.Set("ui_theme", themeName);
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
