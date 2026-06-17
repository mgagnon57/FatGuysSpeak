using FatGuysSpeak.Server.Services;

namespace FatGuysSpeak.Tests.Server;

public class UsernameGeneratorTests
{
    [Fact]
    public void Sanitize_UsesNameLowercasedAndStripped()
    {
        Assert.Equal("johnsmith", UsernameGenerator.Sanitize("John Smith", "j@x.com"));
    }

    [Fact]
    public void Sanitize_KeepsAllowedPunctuation()
    {
        Assert.Equal("john.s_mith-1", UsernameGenerator.Sanitize("John.S_mith-1", "j@x.com"));
    }

    [Fact]
    public void Sanitize_FallsBackToEmailLocalPartWhenNameBlank()
    {
        Assert.Equal("jsmith", UsernameGenerator.Sanitize("   ", "jsmith@x.com"));
    }

    [Fact]
    public void Sanitize_TrimsLeadingTrailingSeparators()
    {
        Assert.Equal("bob", UsernameGenerator.Sanitize("...bob...", "b@x.com"));
    }

    [Fact]
    public void Sanitize_ClampsTo32Chars()
    {
        var result = UsernameGenerator.Sanitize(new string('a', 50), "x@x.com");
        Assert.Equal(32, result.Length);
    }

    [Fact]
    public void Sanitize_FallsBackToUserWhenEverythingStripped()
    {
        Assert.Equal("user", UsernameGenerator.Sanitize("@@@", "@@@@"));
    }
}
