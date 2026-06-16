using System.Security.Cryptography;
using System.Text;

namespace FatGuysSpeak.Shared;

/// <summary>PKCE (RFC 7636) helpers for the Google OAuth loopback flow.</summary>
public static class PkceHelper
{
    public static string GenerateVerifier() => Base64Url(RandomNumberGenerator.GetBytes(32));

    public static string Challenge(string verifier) =>
        Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));

    public static string GenerateState() => Base64Url(RandomNumberGenerator.GetBytes(16));

    private static string Base64Url(byte[] data) =>
        Convert.ToBase64String(data).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
