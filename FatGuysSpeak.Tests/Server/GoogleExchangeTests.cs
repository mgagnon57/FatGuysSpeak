using FatGuysSpeak.Server.Controllers;
using FatGuysSpeak.Server.Services;
using FatGuysSpeak.Shared;
using FatGuysSpeak.Tests.Helpers;
using Microsoft.AspNetCore.Mvc;

namespace FatGuysSpeak.Tests.Server;

public class GoogleExchangeTests : IDisposable
{
    private readonly TestDb _testDb;
    private readonly AuthController _controller;
    private readonly FakeGoogleCodeExchanger _exchanger = new();
    private readonly FakeGoogleTokenValidator _validator = new();

    public GoogleExchangeTests()
    {
        _testDb = new TestDb();
        _controller = new AuthController(_testDb.Db, TestHelpers.CreateTokenService(),
            new SessionBlacklistService());
    }

    public void Dispose() => _testDb.Dispose();

    private Task<ActionResult<AuthResponse>> Exchange() =>
        _controller.GoogleExchange(
            new GoogleCodeExchangeRequest("auth-code", "verifier", "http://127.0.0.1:5000/"),
            _exchanger, _validator);

    [Fact]
    public async Task ValidCode_ExchangesValidatesAndCreatesAccount()
    {
        _exchanger.IdTokenToReturn = "fake-id-token";
        _validator.Identity = new GoogleIdentity("sub-1", "jane@gmail.com", true, "Jane Doe");

        var result = await Exchange();

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var auth = Assert.IsType<AuthResponse>(ok.Value);
        Assert.False(string.IsNullOrWhiteSpace(auth.Token));
        Assert.Equal("janedoe", auth.Username);
        Assert.True(_testDb.Db.ExternalLogins.Any(e => e.ProviderUserId == "sub-1"));
    }

    [Fact]
    public async Task ExchangeFailure_ReturnsUnauthorizedAndCreatesNothing()
    {
        _exchanger.ThrowOnExchange = new GoogleExchangeException("bad code");

        var result = await Exchange();

        Assert.IsType<UnauthorizedObjectResult>(result.Result);
        Assert.Empty(_testDb.Db.Users);
        Assert.Empty(_testDb.Db.ExternalLogins);
    }

    [Fact]
    public async Task NotConfigured_ReturnsServiceUnavailable()
    {
        _exchanger.ThrowOnExchange = new InvalidOperationException("not configured");

        var result = await Exchange();

        var status = Assert.IsType<ObjectResult>(result.Result);
        Assert.Equal(503, status.StatusCode);
    }

    [Fact]
    public async Task MissingFields_ReturnsUnauthorized()
    {
        var result = await _controller.GoogleExchange(
            new GoogleCodeExchangeRequest("", "", ""), _exchanger, _validator);

        Assert.IsType<UnauthorizedObjectResult>(result.Result);
    }
}
