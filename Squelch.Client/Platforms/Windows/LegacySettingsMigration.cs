#if WINDOWS
using Squelch.Shared;

namespace Squelch.Client;

/// <summary>One-time import of the old FatGuysSpeak client's MAUI preferences and secure storage (the ApplicationId
/// changed, so they live in a different folder). Must run before anything reads Preferences/SecureStorage.</summary>
public static class LegacySettingsMigration
{
    public static void RunOnce()
    {
        try
        {
            var (newDir, oldDir) = LegacySettingsPaths.FromAppDataDirectory(FileSystem.AppDataDirectory);
            var newPrefs = Path.Combine(newDir, "preferences.dat");
            var oldPrefs = Path.Combine(oldDir, "preferences.dat");
            if (!LegacySettingsImport.ShouldImport(File.Exists(newPrefs), File.Exists(oldPrefs))) return;

            var rewritten = LegacySettingsImport.RewritePreferencesJson(File.ReadAllText(oldPrefs));
            Directory.CreateDirectory(newDir);

            // Secure storage first, preferences last: preferences.dat is the "already imported" marker, so a
            // crash midway leaves the import eligible to run again.
            var oldSecure = Path.Combine(oldDir, "securestorage.dat");
            var newSecure = Path.Combine(newDir, "securestorage.dat");
            if (File.Exists(oldSecure)) File.Copy(oldSecure, newSecure, overwrite: true);

            var tmp = newPrefs + ".import.tmp";
            File.WriteAllText(tmp, rewritten, new System.Text.UTF8Encoding(false));
            File.Move(tmp, newPrefs, overwrite: false);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Legacy settings import skipped: {ex.Message}");
        }
    }
}
#else
namespace Squelch.Client;

public static class LegacySettingsMigration
{
    public static void RunOnce() { }
}
#endif
