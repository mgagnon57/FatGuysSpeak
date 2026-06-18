namespace FatGuysSpeak.Shared;

/// <summary>Client/server version compatibility. Same SemVer major == wire-compatible
/// (connect as-is); different major == breaking gap (client must sync). Pure, never throws.</summary>
public static class VersionCompat
{
    public static bool SameMajor(string? clientVersion, string? serverVersion)
    {
        var a = SemVer.Major(clientVersion);
        var b = SemVer.Major(serverVersion);
        return a is not null && b is not null && a == b;
    }
}
