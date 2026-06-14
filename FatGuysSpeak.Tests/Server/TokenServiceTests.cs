using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using FatGuysSpeak.Server.Models;
using FatGuysSpeak.Tests.Helpers;

namespace FatGuysSpeak.Tests.Server;

public class TokenServiceTests
{
    [Fact]
    public void CreateToken_ReturnsNonEmptyString()
    {
        var svc = TestHelpers.CreateTokenService();
        var user = new User { Id = 1, Username = "alice" };

        var token = svc.CreateToken(user);

        Assert.False(string.IsNullOrWhiteSpace(token));
    }

    [Fact]
    public void CreateToken_ContainsCorrectClaims()
    {
        var svc = TestHelpers.CreateTokenService();
        var user = new User { Id = 42, Username = "bob" };

        var token = svc.CreateToken(user);

        var handler = new JwtSecurityTokenHandler();
        var jwt = handler.ReadJwtToken(token);

        Assert.Equal("42", jwt.Claims.First(c => c.Type == ClaimTypes.NameIdentifier).Value);
        Assert.Equal("bob", jwt.Claims.First(c => c.Type == ClaimTypes.Name).Value);
    }

    [Fact]
    public void CreateToken_ExpiresInSevenDays()
    {
        var svc = TestHelpers.CreateTokenService();
        var user = new User { Id = 1, Username = "alice" };

        var token = svc.CreateToken(user);

        var jwt = new JwtSecurityTokenHandler().ReadJwtToken(token);
        Assert.True(jwt.ValidTo > DateTime.UtcNow.AddDays(6));
    }
}
