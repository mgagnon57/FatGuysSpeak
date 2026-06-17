namespace FatGuysSpeak.Server.Services;

public static class UsernameGenerator
{
    /// <summary>
    /// Produces a valid base username (matches AuthController's username rules:
    /// 1-32 chars of [a-zA-Z0-9._-]) from a display name, falling back to the
    /// email local-part, then to "user". Does NOT guarantee uniqueness.
    /// </summary>
    public static string Sanitize(string? name, string email)
    {
        var source = !string.IsNullOrWhiteSpace(name) ? name! : email.Split('@')[0];
        var cleaned = new string(source.ToLowerInvariant()
            .Where(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-')
            .ToArray());
        cleaned = cleaned.Trim('.', '_', '-');
        if (cleaned.Length > 32) cleaned = cleaned[..32];
        return cleaned.Length == 0 ? "user" : cleaned;
    }
}
