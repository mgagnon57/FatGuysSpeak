using FatGuysSpeak.Shared;

namespace FatGuysSpeak.Tests.Server;

public class VersionInfoTests
{
    [Fact]
    public void NullOrEmpty_DefaultsToZero()
    {
        var v = VersionInfo.Parse(null);
        Assert.Equal("0.0.0", v.Version);
        Assert.Equal("", v.Commit);
        Assert.Equal("", v.BuildDate);
    }

    [Fact]
    public void NoSuffix_PassesVersionThrough()
    {
        var v = VersionInfo.Parse("1.2.3");
        Assert.Equal("1.2.3", v.Version);
        Assert.Equal("", v.Commit);
        Assert.Equal("", v.BuildDate);
    }

    [Fact]
    public void FullStamp_ParsesAllParts()
    {
        var v = VersionInfo.Parse("1.2.3+gabc1234.2026-06-17");
        Assert.Equal("1.2.3", v.Version);
        Assert.Equal("abc1234", v.Commit);
        Assert.Equal("2026-06-17", v.BuildDate);
    }

    [Fact]
    public void NogitSha_YieldsEmptyCommit()
    {
        var v = VersionInfo.Parse("1.2.3+gnogit.2026-06-17");
        Assert.Equal("1.2.3", v.Version);
        Assert.Equal("", v.Commit);
        Assert.Equal("2026-06-17", v.BuildDate);
    }

    [Fact]
    public void MalformedMetadata_DoesNotThrow_KeepsVersion()
    {
        var v = VersionInfo.Parse("1.2.3+weird-metadata");
        Assert.Equal("1.2.3", v.Version);
        Assert.Equal("weird-metadata", v.Commit);
        Assert.Equal("", v.BuildDate);
    }
}
