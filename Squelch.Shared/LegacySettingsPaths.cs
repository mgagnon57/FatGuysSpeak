namespace Squelch.Shared;

/// <summary>Path math for the legacy settings import. MAUI unpackaged puts AppDataDirectory at
/// %LOCALAPPDATA%\&lt;Publisher&gt;\&lt;AppId&gt;\Data and Preferences/SecureStorage in the sibling \Settings folder,
/// so the old app's folder is a sibling of the new app's folder under the publisher directory.</summary>
public static class LegacySettingsPaths
{
    public static (string NewSettingsDir, string OldSettingsDir) FromAppDataDirectory(string appDataDir)
    {
        var appFolder = Path.GetDirectoryName(appDataDir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
            ?? throw new ArgumentException("No parent folder", nameof(appDataDir));
        var publisherDir = Path.GetDirectoryName(appFolder)
            ?? throw new ArgumentException("No publisher folder", nameof(appDataDir));
        return (Path.Combine(appFolder, "Settings"),
                Path.Combine(publisherDir, LegacySettingsImport.OldAppId, "Settings"));
    }
}
