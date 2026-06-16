using FatGuysSpeak.Shared;

namespace FatGuysSpeak.Tests.Server;

public class GoogleAuthUrlBuilderTests
{
    [Fact]
    public void Build_IncludesAllRequiredParams()
    {
        var url = GoogleAuthUrlBuilder.Build(
            "client123", "http://127.0.0.1:5001/", "challengeXYZ", "stateABC");

        Assert.StartsWith("https://accounts.google.com/o/oauth2/v2/auth?", url);
        Assert.Contains("client_id=client123", url);
        Assert.Contains("redirect_uri=http%3A%2F%2F127.0.0.1%3A5001%2F", url);
        Assert.Contains("response_type=code", url);
        Assert.Contains("scope=openid%20email%20profile", url);
        Assert.Contains("code_challenge=challengeXYZ", url);
        Assert.Contains("code_challenge_method=S256", url);
        Assert.Contains("state=stateABC", url);
    }
}
