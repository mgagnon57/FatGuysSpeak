using FatGuysSpeak.Shared;

namespace FatGuysSpeak.Tests.Server;

public class SemVerTests
{
    [Theory]
    [InlineData("v1.1.0", "1.1.0")]
    [InlineData("1.1.0", "1.1.0")]
    [InlineData("V2.0.3", "2.0.3")]
    [InlineData(null, null)]
    [InlineData("garbage", null)]
    [InlineData("", null)]
    public void NormalizeTag_StripsLeadingV_OrNullsGarbage(string? input, string? expected)
        => Assert.Equal(expected, SemVer.NormalizeTag(input));

    [Theory]
    [InlineData("1.0.0", "1.0.0", 0)]
    [InlineData("1.0.0", "1.0.1", -1)]
    [InlineData("1.2.0", "1.10.0", -1)]
    [InlineData("2.0.0", "1.9.9", 1)]
    [InlineData("garbage", "1.0.0", -1)]
    public void Compare_IsNumericPerComponent(string a, string b, int sign)
        => Assert.Equal(sign, Math.Sign(SemVer.Compare(a, b)));

    [Theory]
    [InlineData("1.0.0", "1.1.0", true)]
    [InlineData("1.1.0", "1.1.0", false)]
    [InlineData("1.2.0", "1.1.0", false)]
    [InlineData("1.0.0", "v1.1.0", true)]
    [InlineData("1.0.0", null, false)]
    [InlineData("1.0.0", "", false)]
    [InlineData("1.0.0", "garbage", false)]
    public void IsOutdated_TrueOnlyWhenStrictlyBehindAValidLatest(string current, string? latest, bool expected)
        => Assert.Equal(expected, SemVer.IsOutdated(current, latest));
}
