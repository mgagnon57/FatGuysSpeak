using Google.Apis.Auth;

namespace FatGuysSpeak.Server.Services;

public record GoogleIdentity(string Sub, string Email, bool EmailVerified, string? Name);

public interface IGoogleTokenValidator
{
    /// <summary>
    /// Validates a Google ID token's signature, expiry, and audience.
    /// Throws <see cref="InvalidJwtException"/> if the token is invalid.
    /// </summary>
    Task<GoogleIdentity> ValidateAsync(string idToken);
}

public class GoogleTokenValidator(IConfiguration config) : IGoogleTokenValidator
{
    public async Task<GoogleIdentity> ValidateAsync(string idToken)
    {
        var clientId = config["Google:ClientId"];
        var settings = new GoogleJsonWebSignature.ValidationSettings
        {
            // When configured, require the token's audience to match our client id.
            Audience = string.IsNullOrEmpty(clientId) ? null : new[] { clientId }
        };
        var payload = await GoogleJsonWebSignature.ValidateAsync(idToken, settings);
        return new GoogleIdentity(payload.Subject, payload.Email, payload.EmailVerified, payload.Name);
    }
}
