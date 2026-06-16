using Google.Apis.Auth;

namespace FatGuysSpeak.Server.Services;

public record GoogleIdentity(string Sub, string Email, bool EmailVerified, string? Name);

public interface IGoogleTokenValidator
{
    /// <summary>
    /// Validates a Google ID token's signature, expiry, and audience.
    /// Throws <see cref="InvalidJwtException"/> if the token is invalid, and
    /// <see cref="InvalidOperationException"/> if Google sign-in is not configured
    /// (no Google:ClientId) — never validate a token without a configured audience.
    /// </summary>
    Task<GoogleIdentity> ValidateAsync(string idToken);
}

public class GoogleTokenValidator(IConfiguration config) : IGoogleTokenValidator
{
    public async Task<GoogleIdentity> ValidateAsync(string idToken)
    {
        var clientId = config["Google:ClientId"];
        // Refuse to validate without a configured audience. A null Audience makes
        // GoogleJsonWebSignature skip audience checks, which would accept ID tokens
        // minted for ANY Google OAuth client — i.e. anyone could sign in.
        if (string.IsNullOrWhiteSpace(clientId))
            throw new InvalidOperationException("Google sign-in is not configured (Google:ClientId is missing).");

        var settings = new GoogleJsonWebSignature.ValidationSettings
        {
            Audience = new[] { clientId }
        };
        var payload = await GoogleJsonWebSignature.ValidateAsync(idToken, settings);
        return new GoogleIdentity(payload.Subject, payload.Email, payload.EmailVerified, payload.Name);
    }
}
