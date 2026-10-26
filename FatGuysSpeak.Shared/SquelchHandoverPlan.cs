namespace FatGuysSpeak.Shared;

/// <summary>Where the handover starts: a fresh download + install, or just open the Squelch
/// that is already there (never reinstall 0.14.0 over a possibly newer, possibly running Squelch).</summary>
public enum HandoverStart { DownloadAndInstall, OpenExisting }

/// <summary>Pure path/outcome rules for the 0.13.2 bridge that hands an installed
/// FatGuysSpeak client over to Squelch. Kept here so it is unit-testable without MAUI.</summary>
public static class SquelchHandoverPlan
{
    public const string SetupFileName = "Squelch.Client-win-Setup.exe";
    public const string SquelchPackageId = "Squelch.Client";
    public const string SquelchExeName = "Squelch.Client.exe";
    public const string OldPackageId = "FatGuysSpeak.Client";

    /// <summary>Where Velopack's default (per-user) install puts the Squelch launcher.</summary>
    public static string SquelchExePath(string localAppData)
        => Path.Combine(localAppData, SquelchPackageId, SquelchExeName);

    /// <summary>The real app inside Velopack's &lt;root&gt;\current\ folder.</summary>
    public static string SquelchCurrentExePath(string localAppData)
        => Path.Combine(localAppData, SquelchPackageId, "current", SquelchExeName);

    /// <summary>Velopack writes current\sq.version as part of a completed install.</summary>
    public static string SquelchVersionFilePath(string localAppData)
        => Path.Combine(localAppData, SquelchPackageId, "current", "sq.version");

    /// <summary>A completed Velopack install has both current\Squelch.Client.exe and current\sq.version;
    /// the root stub alone (or a half-extracted current\) is a partial install and doesn't count.</summary>
    public static bool IsCompletedInstall(bool currentExeExists, bool versionFileExists)
        => currentExeExists && versionFileExists;

    /// <summary>Squelch already installed (earlier handover whose uninstall didn't happen, or a
    /// hand install) → skip download/install and only open it + retry the uninstall.
    /// Pass <see cref="IsCompletedInstall"/>, not just "the stub exists".</summary>
    public static HandoverStart Decide(bool squelchInstalled)
        => squelchInstalled ? HandoverStart.OpenExisting : HandoverStart.DownloadAndInstall;

    /// <summary>Velopack runs the app from &lt;root&gt;\current\ with Update.exe in &lt;root&gt;.
    /// Derive the root from the running app's base directory; if the app isn't running from a
    /// "current" folder, fall back to the default install root under LocalAppData.</summary>
    public static string UpdateExePath(string appBaseDirectory, string localAppData)
    {
        var dir = appBaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (string.Equals(Path.GetFileName(dir), "current", StringComparison.OrdinalIgnoreCase))
        {
            var root = Path.GetDirectoryName(dir);
            if (!string.IsNullOrEmpty(root))
                return Path.Combine(root, "Update.exe");
        }
        return Path.Combine(localAppData, OldPackageId, "Update.exe");
    }

    /// <summary>Classifies the silent setup run. Returns null when Squelch installed correctly,
    /// otherwise a user-facing error.</summary>
    public static string? SetupFailure(int exitCode, bool squelchInstalled)
    {
        if (exitCode != 0)
            return $"The Squelch installer failed (exit code {exitCode}).";
        if (!squelchInstalled)
            return "The Squelch installer finished, but Squelch wasn't found where it should be.";
        return null;
    }
}
