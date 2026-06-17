using FatGuysSpeak.Server.Services;

namespace FatGuysSpeak.Tests.Helpers;

public sealed class FakeGoogleCodeExchanger : IGoogleCodeExchanger
{
    public string? IdTokenToReturn { get; set; }
    public Exception? ThrowOnExchange { get; set; }

    public Task<string> ExchangeAsync(string code, string codeVerifier, string redirectUri)
    {
        if (ThrowOnExchange is not null) throw ThrowOnExchange;
        return Task.FromResult(IdTokenToReturn
            ?? throw new InvalidOperationException("FakeGoogleCodeExchanger.IdTokenToReturn must be set."));
    }
}
