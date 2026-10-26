#if WINDOWS
using Squelch.Shared;

namespace Squelch.Client;

/// <summary>One-time import of the old FatGuysSpeak client's MAUI preferences and secure storage (the ApplicationId
/// changed, so they live in a different folder). Must run before anything reads Preferences/SecureStorage.</summary>
public static class LegacySettingsMigration
{
    private const string MutexName = @"Local\Squelch.LegacyImport";

    public static void RunOnce()
    {
        string? tmp = null;
        Mutex? mutex = null;
        var owned = false;
        try
        {
            // Two instances launched together must not both import (or one read prefs mid-write).
            mutex = new Mutex(false, MutexName);
            try { owned = mutex.WaitOne(TimeSpan.FromSeconds(10)); }
            catch (AbandonedMutexException) { owned = true; }
            if (!owned) return;

            // Evaluate the rule only after acquiring: the other instance may have just finished the import.
            var (newDir, oldDir) = LegacySettingsPaths.FromAppDataDirectory(FileSystem.AppDataDirectory);
            var newPrefs = Path.Combine(newDir, "preferences.dat");
            var oldPrefs = Path.Combine(oldDir, "preferences.dat");
            var newPrefsJson = File.Exists(newPrefs) ? File.ReadAllText(newPrefs) : null;
            if (!LegacySettingsImport.ShouldImport(File.Exists(oldPrefs), newPrefsJson)) return;

            var rewritten = LegacySettingsImport.RewritePreferencesJson(File.ReadAllText(oldPrefs));
            Directory.CreateDirectory(newDir);

            // An unmarked Squelch preferences.dat (e.g. from a dev run) is replaced; keep a copy just in case.
            if (newPrefsJson != null) File.Copy(newPrefs, newPrefs + ".pre-import", overwrite: true);

            // Secure storage first, preferences last: the marker lives in preferences.dat, so a crash midway
            // leaves the import eligible to run again.
            var oldSecure = Path.Combine(oldDir, "securestorage.dat");
            var newSecure = Path.Combine(newDir, "securestorage.dat");
            if (File.Exists(oldSecure)) File.Copy(oldSecure, newSecure, overwrite: true);

            tmp = newPrefs + ".import.tmp";
            File.WriteAllText(tmp, rewritten, new System.Text.UTF8Encoding(false));
            File.Move(tmp, newPrefs, overwrite: true);
            tmp = null;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Legacy settings import skipped: {ex.Message}");
        }
        finally
        {
            if (tmp != null)
            {
                try { File.Delete(tmp); } catch { /* best effort */ }
            }
            if (owned) mutex!.ReleaseMutex();
            mutex?.Dispose();
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
