using FatGuysSpeak.Shared;

namespace FatGuysSpeak.Tests.Client;

public class StreamTextTests
{
    [Theory]
    [InlineData(null, "a game")]
    [InlineData("", "a game")]
    [InlineData("  Wardogs \n", "Wardogs")]
    public void SanitizeGameName_TrimsAndFallsBack(string? input, string expected)
        => Assert.Equal(expected, StreamText.SanitizeGameName(input));

    [Fact]
    public void SanitizeGameName_StripsControlCharsAndCapsLength()
    {
        var result = StreamText.SanitizeGameName("x\u0007" + new string('y', 80));

        Assert.DoesNotContain(result, char.IsControl);
        Assert.Equal(40, result.Length);
    }

    [Fact]
    public void ViewerLabel_Game()
        => Assert.Equal("🎮 BillyBong is streaming Wardogs", StreamText.ViewerLabel(StreamKind.Game, "BillyBong", "Wardogs"));

    [Fact]
    public void ViewerLabel_Screen()
        => Assert.Equal("🖥 BillyBong is sharing their screen", StreamText.ViewerLabel(StreamKind.Screen, "BillyBong", null));

    [Theory]
    [InlineData(19045, false)]   // Windows 10 22H2
    [InlineData(22000, true)]    // Windows 11 21H2
    [InlineData(26200, true)]    // owner's PC
    public void SupportsGameStream_RequiresWindows11(int build, bool expected)
        => Assert.Equal(expected, StreamText.SupportsGameStream(build));
}
