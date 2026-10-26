using Squelch.Shared;

namespace Squelch.Tests.Client;

public class GameDetectionTests
{
    private const int MonW = 2560, MonH = 1440;

    private static GameWindowFacts Facts(string process, string title = "win", int left = 0, int top = 0,
        int width = MonW, int height = MonH, string? exePath = null, bool minimized = false, bool? gfx = null,
        long handle = 1, int pid = 100) =>
        new(handle, pid, title, process, exePath, left, top, width, height, 0, 0, MonW, MonH, minimized, gfx);

    private static readonly HashSet<string> Library = new(StringComparer.OrdinalIgnoreCase)
    {
        "WardogsClient-Win64-Shipping", "WardogsLauncher-Shipping", "CrashReportClient", "EpicWebHelper",
        "FortniteClient-Win64-Shipping",
    };
    private static readonly string[] Roots = [@"C:\Program Files (x86)\Steam\steamapps\common"];

    // Wardogs as observed on the owner's PC: borderless fullscreen on the 2560×1440 primary, exe path unreadable.
    private static GameWindowFacts Wardogs(long handle = 1) =>
        Facts("WardogsClient-Win64-Shipping", "Wardogs  ", handle: handle);

    [Fact]
    public void Detect_Wardogs_IsConfidentPick()
    {
        var result = GameDetector.Detect(
            [Facts("explorer", "Program Manager", handle: 2), Wardogs(), Facts("chrome", "YouTube", 100, 100, 1200, 800, handle: 3)],
            Library, Roots);

        Assert.NotNull(result.ConfidentPick);
        Assert.Equal("WardogsClient-Win64-Shipping", result.ConfidentPick!.Window.ProcessName);
        Assert.Equal("Wardogs", result.ConfidentPick.GameName);
    }

    [Fact]
    public void Detect_MinimizedLibraryGame_IsConfident()
    {
        var minimized = Facts("WardogsClient-Win64-Shipping", "Wardogs", -32000, -32000, 160, 28, minimized: true);

        var result = GameDetector.Detect([minimized], Library, Roots);

        Assert.Equal(minimized, result.ConfidentPick?.Window);
    }

    [Fact]
    public void Detect_FullscreenBrowser_IsNeverCandidate()
    {
        var result = GameDetector.Detect([Facts("msedge", "Some video - YouTube")], Library, Roots);

        Assert.Empty(result.Ranked);
        Assert.Null(result.ConfidentPick);
    }

    [Fact]
    public void Detect_ExcludedHelpersInLibrary_AreIgnored()
    {
        var result = GameDetector.Detect(
            [Facts("CrashReportClient", "Crash Reporter", handle: 2), Facts("EpicWebHelper", "Epic", handle: 3)],
            Library, Roots);

        Assert.Empty(result.Ranked);
    }

    // Seen live on the owner's PC: NVIDIA's overlay is a full-monitor GPU window (score 60 without exclusion).
    [Theory]
    [InlineData("NVIDIA Overlay")]
    [InlineData("NVIDIA Share")]
    [InlineData("GameBar")]
    [InlineData("XboxGameBarWidgets")]
    [InlineData("GameOverlayUI")]
    public void Detect_FullscreenOverlays_AreNeverCandidates(string overlay)
    {
        var result = GameDetector.Detect([Facts(overlay, overlay, gfx: true)], Library, Roots);

        Assert.Empty(result.Ranked);
    }

    [Fact]
    public void Detect_GameAndItsLauncher_PicksGame()
    {
        var launcher = Facts("WardogsLauncher-Shipping", "Wardogs Launcher", 600, 300, 1200, 700, handle: 2);

        var result = GameDetector.Detect([launcher, Wardogs()], Library, Roots);

        Assert.Equal("WardogsClient-Win64-Shipping", result.ConfidentPick?.Window.ProcessName);
    }

    [Fact]
    public void Detect_TwoLibraryGamesBothFullscreen_NotConfident()
    {
        var result = GameDetector.Detect(
            [Wardogs(), Facts("FortniteClient-Win64-Shipping", "Fortnite", handle: 2)], Library, Roots);

        Assert.Null(result.ConfidentPick);
        Assert.Equal(2, result.Ranked.Count);
    }

    [Fact]
    public void Detect_UnknownFullscreenApp_NotConfidentButRanked()
    {
        var result = GameDetector.Detect([Facts("SomeIndieGame", "Indie")], Library, Roots);

        Assert.Single(result.Ranked);
        Assert.Null(result.ConfidentPick);
    }

    [Fact]
    public void Detect_NothingGameLike_EmptyAndNull()
    {
        var result = GameDetector.Detect([Facts("notepad", "Untitled", 100, 100, 800, 600)], Library, Roots);

        Assert.Empty(result.Ranked);
        Assert.Null(result.ConfidentPick);
    }

    [Fact]
    public void Detect_ExePathUnderLibraryRoot_CountsAsLibrary()
    {
        var game = Facts("Mystery", "Mystery Game",
            exePath: @"C:\Program Files (x86)\Steam\steamapps\common\Mystery\Mystery.exe");

        var result = GameDetector.Detect([game], Library, Roots);

        Assert.Equal(game, result.ConfidentPick?.Window);
    }

    [Theory]
    [InlineData("WardogsClient-Win64-Shipping", "Wardogs")]
    [InlineData("FortniteClient-Win64-Shipping", "Fortnite")]
    [InlineData("VALORANT-Win64-Shipping", "VALORANT")]
    [InlineData("cs2", "cs2")]
    [InlineData("RocketLeague.exe", "RocketLeague")]
    [InlineData("", "a game")]
    [InlineData("   ", "a game")]
    public void Clean_StripsEngineSuffixes(string process, string expected)
        => Assert.Equal(expected, GameNames.Clean(process));

    [Fact]
    public void Clean_TruncatesTo40Chars()
        => Assert.Equal(40, GameNames.Clean(new string('x', 60)).Length);
}
