namespace FatGuysSpeak.Shared;

/// <summary>Maps a product version to its Velopack per-version channel name, used identically
/// by the release publisher and the client so the client can pin to the server's exact version.
/// "1.2.0" -> "v1-2-0". Returns null for anything that isn't a MAJOR.MINOR.PATCH version.</summary>
public static class UpdateChannel
{
    public static string? ForVersion(string? version)
    {
        var norm = SemVer.NormalizeTag(version);
        return norm is null ? null : "v" + norm.Replace('.', '-');
    }
}
