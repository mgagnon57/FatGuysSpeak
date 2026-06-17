using FatGuysSpeak.Shared;

namespace FatGuysSpeak.Tests.Server;

public class LoopbackRedirectParserTests
{
    [Fact]
    public void Parse_ValidCodeAndState_Succeeds()
    {
        var r = LoopbackRedirectParser.Parse("?code=abc123&state=xyz", "xyz");
        Assert.True(r.Success);
        Assert.Equal("abc123", r.Code);
        Assert.Null(r.Error);
    }

    [Fact]
    public void Parse_ErrorParam_Fails()
    {
        var r = LoopbackRedirectParser.Parse("?error=access_denied", "xyz");
        Assert.False(r.Success);
        Assert.Equal("access_denied", r.Error);
    }

    [Fact]
    public void Parse_StateMismatch_Fails()
    {
        var r = LoopbackRedirectParser.Parse("?code=abc&state=wrong", "xyz");
        Assert.False(r.Success);
        Assert.Equal("state_mismatch", r.Error);
    }

    [Fact]
    public void Parse_MissingCode_Fails()
    {
        var r = LoopbackRedirectParser.Parse("?state=xyz", "xyz");
        Assert.False(r.Success);
        Assert.Equal("missing_code", r.Error);
    }

    [Fact]
    public void Parse_UrlEncodedCode_IsDecoded()
    {
        var r = LoopbackRedirectParser.Parse("?code=a%2Fb%2Bc&state=xyz", "xyz");
        Assert.True(r.Success);
        Assert.Equal("a/b+c", r.Code);
    }
}
