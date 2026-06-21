namespace FatGuysSpeak.Shared;

/// <summary>What the client should do about its version when it connects to a server.</summary>
public enum VersionSyncAction
{
    /// <summary>Can't decide (no server version, or client isn't a Velopack install / dev build). Connect as-is.</summary>
    CannotEvaluate,
    /// <summary>Already current, or a newer-but-compatible client (same major) — connect as-is, no update.</summary>
    ConnectAsIs,
    /// <summary>The server is newer — sync up to the server's version (any major/minor/patch gap).</summary>
    Upgrade,
    /// <summary>The server is older and across a major gap — sync down to the server's version.</summary>
    Downgrade,
}

/// <summary>Pure decision for the connect-time version sync: given the client's installed
/// version and the server's version, decide whether to connect as-is or sync up/down.
/// A newer server always pulls the client up (so a beta minor/patch bump rolls out on the next
/// connect); a newer client only forces a downgrade across a SemVer-major gap (within a major the
/// client stays wire-compatible, so there's no point downgrading it). Never throws.</summary>
public static class VersionSyncPlan
{
    public static VersionSyncAction Decide(string? installedVersion, string? serverVersion)
    {
        // No server version reported, or not a Velopack install (dev/unpackaged) -> can't self-sync.
        if (string.IsNullOrWhiteSpace(serverVersion)) return VersionSyncAction.CannotEvaluate;
        if (string.IsNullOrWhiteSpace(installedVersion)) return VersionSyncAction.CannotEvaluate;

        // Unparseable on either side -> can't reason about versions -> leave the user connected.
        if (SemVer.Major(installedVersion) is null || SemVer.Major(serverVersion) is null)
            return VersionSyncAction.CannotEvaluate;

        var cmp = SemVer.Compare(installedVersion, serverVersion);
        if (cmp == 0) return VersionSyncAction.ConnectAsIs;   // already on the server's version
        if (cmp < 0) return VersionSyncAction.Upgrade;        // server is newer -> always sync up

        // Client is newer than the server: only force a downgrade when they're a major apart;
        // within the same major the client is wire-compatible, so keep it.
        return VersionCompat.SameMajor(installedVersion, serverVersion)
            ? VersionSyncAction.ConnectAsIs
            : VersionSyncAction.Downgrade;
    }
}
