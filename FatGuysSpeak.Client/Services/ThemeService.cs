namespace FatGuysSpeak.Client.Services;

public static class ThemeService
{
    public const string Dark = "Dark";
    public const string Midnight = "Midnight";
    public const string OLED = "OLED";

    private static readonly Dictionary<string, Dictionary<string, string>> Themes = new()
    {
        [Dark] = new()
        {
            ["ThemeBgPrimary"]     = "#1a1a1a",
            ["ThemeBgPanel"]       = "#2a2a2a",
            ["ThemeBgHeader"]      = "#252525",
            ["ThemeBgElevated"]    = "#3a3a3a",
            ["ThemeBgInput"]       = "#141414",
            ["ThemeDivider"]       = "#333333",
            ["ThemeAccent"]        = "#2d5f9e",
            ["ThemeAccentLight"]   = "#8ab4d4",
            ["ThemeTextPrimary"]   = "#d0d0d0",
            ["ThemeTextSecondary"] = "#888888",
            ["ThemeTextMuted"]     = "#666666",
        },
        [Midnight] = new()
        {
            ["ThemeBgPrimary"]     = "#0e1016",
            ["ThemeBgPanel"]       = "#161b2a",
            ["ThemeBgHeader"]      = "#111622",
            ["ThemeBgElevated"]    = "#1f2540",
            ["ThemeBgInput"]       = "#0b0d14",
            ["ThemeDivider"]       = "#1e2438",
            ["ThemeAccent"]        = "#5865f2",
            ["ThemeAccentLight"]   = "#a0a8f8",
            ["ThemeTextPrimary"]   = "#dce1f0",
            ["ThemeTextSecondary"] = "#7a88a8",
            ["ThemeTextMuted"]     = "#4d5a6e",
        },
        [OLED] = new()
        {
            ["ThemeBgPrimary"]     = "#000000",
            ["ThemeBgPanel"]       = "#0d0d0d",
            ["ThemeBgHeader"]      = "#111111",
            ["ThemeBgElevated"]    = "#181818",
            ["ThemeBgInput"]       = "#000000",
            ["ThemeDivider"]       = "#1a1a1a",
            ["ThemeAccent"]        = "#2d5f9e",
            ["ThemeAccentLight"]   = "#8ab4d4",
            ["ThemeTextPrimary"]   = "#e0e0e0",
            ["ThemeTextSecondary"] = "#888888",
            ["ThemeTextMuted"]     = "#555555",
        },
    };

    public static string CurrentTheme { get; private set; } = Dark;

    public static void Apply(string themeName)
    {
        if (!Themes.TryGetValue(themeName, out var colors)) return;
        CurrentTheme = themeName;
        var resources = Application.Current?.Resources;
        if (resources is null) return;
        foreach (var (key, hex) in colors)
            resources[key] = Color.FromArgb(hex);
        Preferences.Set("app_theme", themeName);
    }

    public static void ApplyStored() =>
        Apply(Preferences.Get("app_theme", Dark));
}
