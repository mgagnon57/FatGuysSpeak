using FatGuysSpeak.Shared;

namespace FatGuysSpeak.Tests.Client;

public class GameLibraryTests
{
    // Trimmed copy of a real libraryfolders.vdf (Steam escapes backslashes).
    private const string Vdf = """
        "libraryfolders"
        {
        	"0"
        	{
        		"path"		"C:\\Program Files (x86)\\Steam"
        		"label"		""
        		"apps"
        		{
        			"228980"		"411709306"
        		}
        	}
        	"1"
        	{
        		"path"		"D:\\SteamLibrary"
        	}
        }
        """;

    [Fact]
    public void ParseLibraryPaths_RealFormat_ReturnsCommonFolders()
    {
        var paths = SteamLibraryParser.ParseLibraryPaths(Vdf);

        Assert.Equal(
            [@"C:\Program Files (x86)\Steam\steamapps\common", @"D:\SteamLibrary\steamapps\common"],
            paths);
    }

    [Fact]
    public void ParseLibraryPaths_Null_ReturnsEmpty()
        => Assert.Empty(SteamLibraryParser.ParseLibraryPaths(null));

    [Fact]
    public void ParseLibraryPaths_Malformed_ReturnsEmpty()
        => Assert.Empty(SteamLibraryParser.ParseLibraryPaths("{{{ not vdf"));

    [Fact]
    public void RelativeFolders_IncludesEpicAndXbox()
    {
        Assert.Contains(@"Program Files\Epic Games", GameLibraryRoots.RelativeFolders);
        Assert.Contains("XboxGames", GameLibraryRoots.RelativeFolders);
    }
}
