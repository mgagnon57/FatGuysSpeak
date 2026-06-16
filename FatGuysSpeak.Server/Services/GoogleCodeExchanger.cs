using System.Text.Json;

namespace FatGuysSpeak.Server.Services;

/// <summary>Thrown when Google's token exchange fails (bad/expired code, no id_token, network).</summary>
public class GoogleExchangeException(string message) : Exception(message);

public interface IGoogleCodeExchanger
{
    /// <summary>
    /// Exchanges an authorization code for a Google ID token.
    /// Throws <see cref="InvalidOperationException"/> if Google sign-in is not configured,
    /// and <see cref="GoogleExchangeException"/> if the exchange fails.
    /// </summary>
    Task<string> ExchangeAsync(string code, string codeVerifier, string redirectUri);
}

public class GoogleCodeExchanger(IHttpClientFactory httpFactory, IConfiguration config) : IGoogleCodeExchanger
{
    private const string TokenEndpoint = "https://oauth2.googleapis.com/token";

    public async Task<string> ExchangeAsync(string code, string codeVerifier, string redirectUri)
    {
        var clientId = config["Google:ClientId"];
        var clientSecret = config["Google:ClientSecret"];
        if (string.IsNullOrWhiteSpace(clientId) || string.IsNullOrWhiteSpace(clientSecret))
            throw new InvalidOperationException("Google sign-in is not configured (Google:ClientId/ClientSecret missing).");

        var client = httpFactory.CreateClient("google");
        using var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["client_id"] = clientId,
            ["client_secret"] = clientSecret,
            ["code"] = code,
            ["code_verifier"] = codeVerifier,
            ["grant_type"] = "authorization_code",
            ["redirect_uri"] = redirectUri,
        });

        using var resp = await client.PostAsync(TokenEndpoint, form);
        if (!resp.IsSuccessStatusCode)
            throw new GoogleExchangeException($"Google token exchange failed: {(int)resp.StatusCode}");

        var body = await resp.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(body);
        if (!doc.RootElement.TryGetProperty("id_token", out var idTokenEl)
            || idTokenEl.GetString() is not { Length: > 0 } idToken)
            throw new GoogleExchangeException("Google token response did not contain an id_token.");

        return idToken;
    }
}
