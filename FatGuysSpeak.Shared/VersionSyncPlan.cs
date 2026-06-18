namespace FatGuysSpeak.Shared;

/// <summary>What the client should do about its version when it connects to a server.</summary>
public enum VersionSyncAction
{
    /// <summary>Can't decide (no server version, or client isn't a Velopack install / dev build). Connect as-is.</summary>
    CannotEvaluate,
    /// <summary>Compatible (same major) — connect as-is, no update.</summary>
    ConnectAsIs,
    /// <summary>Cross-major and the server is newer — sync up to the server's version.</summary>
    Upgrade,
    /// <summary>Cross-major and the server is older — sync down to the server's version.</summary>
    Downgrade,
}

/// <summary>Pure decision for the connect-time version sync: given the client's installed
/// version and the server's version, decide whether to connect as-is or sync up/down.
/// Sync fires only across a SemVer-major gap; within a major the client is wire-compatible.
/// Never throws.</summary>
public static class VersionSyncPlan
{
    public static VersionSyncAction Decide(string? installedVersion, string? serverVersion)
    {
        // No server version reported, or not a Velopack install (dev/unpackaged) -> can't self-sync.
        if (string.IsNullOrWhiteSpace(serverVersion)) return VersionSyncAction.CannotEvaluate;
        if (string.IsNullOrWhiteSpace(installedVersion)) return VersionSyncAction.CannotEvaluate;

        // Unparseable on either side -> can't reason about majors -> leave the user connected.
        if (SemVer.Major(installedVersion) is null || SemVer.Major(serverVersion) is null)
            return VersionSyncAction.CannotEvaluate;

        if (VersionCompat.SameMajor(installedVersion, serverVersion))
            return VersionSyncAction.ConnectAsIs;   // wire-compatible

        // Cross-major: sync to the server's exact version, up or down.
        return SemVer.Compare(installedVersion, serverVersion) > 0
            ? VersionSyncAction.Downgrade
            : VersionSyncAction.Upgrade;
    }
}
