using System.Text.RegularExpressions;

namespace FatGuysSpeak.Shared;

/// <summary>Reads Steam's libraryfolders.vdf to find every Steam library's game folder.</summary>
public static class SteamLibraryParser
{
    private static readonly Regex PathRegex = new("\"path\"\\s+\"([^\"]+)\"", RegexOptions.Compiled);

    /// <summary>Returns each library's "steamapps\common" folder. Never throws; bad input → empty.</summary>
    public static IReadOnlyList<string> ParseLibraryPaths(string? vdfText)
    {
        if (string.IsNullOrWhiteSpace(vdfText)) return [];
        return PathRegex.Matches(vdfText)
            .Select(m => m.Groups[1].Value.Replace(@"\\", @"\").TrimEnd('\\'))
            .Where(p => p.Length > 0)
            .Select(p => p + @"\steamapps\common")
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }
}

/// <summary>Well-known game install folders, relative to each fixed drive root.</summary>
public static class GameLibraryRoots
{
    public static IReadOnlyList<string> RelativeFolders { get; } =
    [
        "Epic Games",
        @"Program Files\Epic Games",
        "XboxGames",
        "Riot Games",
        @"Program Files (x86)\Battle.net",
        @"Program Files (x86)\GOG Galaxy\Games",
        @"Program Files\EA Games",
        @"Program Files (x86)\Ubisoft\Ubisoft Game Launcher\games",
    ];
}
