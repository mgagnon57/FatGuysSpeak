namespace Squelch.Shared;

/// <summary>What the client could learn about one visible window. ExePath / UsesGraphicsRuntime are null
/// when Windows refused to tell us (anti-cheat-protected games like Wardogs do this).</summary>
public sealed record GameWindowFacts(
    long Handle, int ProcessId, string Title, string ProcessName, string? ExePath,
    int Left, int Top, int Width, int Height,
    int MonitorLeft, int MonitorTop, int MonitorWidth, int MonitorHeight,
    bool IsMinimized, bool? UsesGraphicsRuntime);

public sealed record GameCandidate(GameWindowFacts Window, int Score, string GameName);

public sealed record GameDetectionResult(IReadOnlyList<GameCandidate> Ranked, GameCandidate? ConfidentPick);

/// <summary>Picks the window that is most likely the game being played, for one-click "Stream Game".</summary>
public static class GameDetector
{
    private const int CoversMonitorScore = 40;
    private const int LibraryScore       = 50;
    private const int GraphicsScore      = 20;
    private const int MinCandidateScore  = 40;
    private const int MinConfidentScore  = 50;
    private const int ConfidentLead      = 30;
    private const int EdgeTolerancePx    = 2;

    // Never games, even when they fill a monitor or live inside a game's install folder.
    private static readonly HashSet<string> Excluded = new(StringComparer.OrdinalIgnoreCase)
    {
        "Squelch.Client", "explorer", "ShellExperienceHost", "StartMenuExperienceHost", "SearchHost",
        "TextInputHost", "ApplicationFrameHost", "LockApp",
        "chrome", "msedge", "firefox", "opera", "brave", "vivaldi",
        "steam", "steamwebhelper", "EpicGamesLauncher", "EpicWebHelper", "Battle.net", "RiotClientServices",
        "RiotClientUx", "EADesktop", "GalaxyClient", "upc",
        "Discord", "obs64", "obs32", "devenv", "Code", "WindowsTerminal", "powershell", "cmd", "Teams", "ms-teams",
        "Spotify", "CrashReportClient", "crashpad_handler", "UnityCrashHandler64",
        // Overlays: transparent full-monitor GPU windows that sit on top of games.
        "NVIDIA Overlay", "NVIDIA Share", "GameBar", "GameBarFTServer", "XboxGameBarWidgets", "GameOverlayUI",
        "DiscordOverlay", "RTSS", "MSIAfterburner",
    };

    public static GameDetectionResult Detect(
        IReadOnlyList<GameWindowFacts> windows, IReadOnlySet<string> libraryExeNames, IReadOnlyList<string> libraryRoots)
    {
        var ranked = windows
            .Where(w => !Excluded.Contains(w.ProcessName))
            .Select(w => new GameCandidate(w, Score(w, libraryExeNames, libraryRoots), GameNames.Clean(w.ProcessName)))
            .Where(c => c.Score >= MinCandidateScore)
            .OrderByDescending(c => c.Score)
            .ThenByDescending(c => CoversMonitor(c.Window))
            .ToList();

        GameCandidate? pick = null;
        if (ranked.Count > 0 && ranked[0].Score >= MinConfidentScore
            && (ranked.Count == 1 || ranked[0].Score - ranked[1].Score >= ConfidentLead))
            pick = ranked[0];

        return new GameDetectionResult(ranked, pick);
    }

    private static int Score(GameWindowFacts w, IReadOnlySet<string> libraryExeNames, IReadOnlyList<string> libraryRoots)
    {
        var score = 0;
        if (CoversMonitor(w)) score += CoversMonitorScore;
        if (libraryExeNames.Contains(w.ProcessName)
            || (w.ExePath is not null && libraryRoots.Any(r => w.ExePath.StartsWith(r, StringComparison.OrdinalIgnoreCase))))
            score += LibraryScore;
        if (w.UsesGraphicsRuntime == true) score += GraphicsScore;
        return score;
    }

    private static bool CoversMonitor(GameWindowFacts w) =>
        !w.IsMinimized
        && Math.Abs(w.Left - w.MonitorLeft) <= EdgeTolerancePx
        && Math.Abs(w.Top - w.MonitorTop) <= EdgeTolerancePx
        && Math.Abs(w.Width - w.MonitorWidth) <= EdgeTolerancePx
        && Math.Abs(w.Height - w.MonitorHeight) <= EdgeTolerancePx;
}

/// <summary>Turns a process name into the game name viewers see. Only process names are used — never window
/// titles — so a browser tab or DM title can't be broadcast by mistake.</summary>
public static class GameNames
{
    public const int MaxLength = 40;
    private const string Fallback = "a game";
    private static readonly string[] EngineSuffixes = ["-Win64-Shipping", "-WinGDK-Shipping", "-Win32-Shipping", "-Shipping"];

    public static string Clean(string processName)
    {
        var name = processName.Trim();
        if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) name = name[..^4];
        foreach (var suffix in EngineSuffixes)
        {
            if (name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)) { name = name[..^suffix.Length]; break; }
        }
        if (name.Length > "Client".Length && name.EndsWith("Client", StringComparison.OrdinalIgnoreCase))
            name = name[..^"Client".Length];
        name = name.Trim();
        if (name.Length == 0) return Fallback;
        return name.Length > MaxLength ? name[..MaxLength] : name;
    }
}
