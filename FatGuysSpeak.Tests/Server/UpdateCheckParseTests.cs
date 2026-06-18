using FatGuysSpeak.Server.Services;

namespace FatGuysSpeak.Tests.Server;

public class UpdateCheckParseTests
{
    [Fact]
    public void ParsesTagAndUrl()
    {
        var json = """{"tag_name":"v1.2.0","html_url":"https://github.com/x/y/releases/tag/v1.2.0"}""";
        var (version, url) = UpdateCheckService.ParseLatestRelease(json);
        Assert.Equal("1.2.0", version);
        Assert.Equal("https://github.com/x/y/releases/tag/v1.2.0", url);
    }

    [Fact]
    public void MissingFields_YieldNulls()
    {
        var (version, url) = UpdateCheckService.ParseLatestRelease("""{"message":"Not Found"}""");
        Assert.Null(version);
        Assert.Null(url);
    }

    [Fact]
    public void NonSemverTag_NormalizesToNull()
    {
        var (version, _) = UpdateCheckService.ParseLatestRelease("""{"tag_name":"nightly"}""");
        Assert.Null(version);
    }
}
