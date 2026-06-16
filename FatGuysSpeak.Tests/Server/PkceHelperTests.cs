using FatGuysSpeak.Shared;

namespace FatGuysSpeak.Tests.Server;

public class PkceHelperTests
{
    [Fact]
    public void GenerateVerifier_IsUrlSafeAndRightLength()
    {
        var v = PkceHelper.GenerateVerifier();
        Assert.Equal(43, v.Length);
        Assert.Matches("^[A-Za-z0-9_-]+$", v);
    }

    [Fact]
    public void Challenge_MatchesRfc7636TestVector()
    {
        const string verifier = "dBjftJeZ4CVP-mB92K27uhbUJU1p1r_wW1gFWFOEjXk";
        const string expected = "E9Melhoa2OwvFrEMTJguCHaoeK1t8URWbuGJSstw-cM";
        Assert.Equal(expected, PkceHelper.Challenge(verifier));
    }

    [Fact]
    public void GenerateState_IsNonEmptyAndDistinct()
    {
        var a = PkceHelper.GenerateState();
        var b = PkceHelper.GenerateState();
        Assert.False(string.IsNullOrWhiteSpace(a));
        Assert.NotEqual(a, b);
    }
}
