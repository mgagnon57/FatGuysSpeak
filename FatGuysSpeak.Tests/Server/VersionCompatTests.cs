using FatGuysSpeak.Shared;
using Xunit;

namespace FatGuysSpeak.Tests.Server;

public class VersionCompatTests
{
    [Theory]
    [InlineData("1.0.0", "1.7.3", true)]
    [InlineData("1.5.0", "1.2.0", true)]
    [InlineData("v2.1.0", "2.9.9", true)]
    [InlineData("1.0.0", "3.0.0", false)]
    [InlineData("3.0.0", "1.0.0", false)]
    public void SameMajor_ComparesMajorOnly(string a, string b, bool expected)
        => Assert.Equal(expected, VersionCompat.SameMajor(a, b));

    [Theory]
    [InlineData(null, "1.0.0")]
    [InlineData("1.0.0", null)]
    [InlineData("junk", "1.0.0")]
    public void SameMajor_Unparseable_ReturnsFalse(string? a, string? b)
        => Assert.False(VersionCompat.SameMajor(a, b));
}
