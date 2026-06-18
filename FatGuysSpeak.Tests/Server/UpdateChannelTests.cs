using FatGuysSpeak.Shared;

namespace FatGuysSpeak.Tests.Server;

public class UpdateChannelTests
{
    [Theory]
    [InlineData("1.2.0", "v1-2-0")]
    [InlineData("v1.2.0", "v1-2-0")]
    [InlineData("V2.0.3", "v2-0-3")]
    [InlineData("1.0.0", "v1-0-0")]
    public void ForVersion_MapsToPerVersionChannel(string version, string expected)
        => Assert.Equal(expected, UpdateChannel.ForVersion(version));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("garbage")]
    public void ForVersion_NullForUnparseable(string? version)
        => Assert.Null(UpdateChannel.ForVersion(version));
}
