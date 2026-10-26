using System.Text.Json;
using Squelch.Shared;

namespace Squelch.Tests.Client;

// First launch after the FatGuysSpeak -> Squelch rename imports the old app's MAUI preferences.dat so testers keep
// their login, PTT key and devices — but the old Azure server URL must be pointed at the new one.
public class LegacySettingsImportTests
{
    private const string Old = LegacySettingsImport.OldServerUrl, New = LegacySettingsImport.NewServerUrl;

    private static string Build(params (string Key, string Value)[] prefs)
        => JsonSerializer.Serialize(new Dictionary<string, Dictionary<string, string>>
        {
            [""] = prefs.ToDictionary(p => p.Key, p => p.Value),
        });

    private static string? Get(string json, string key)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.GetProperty("").TryGetProperty(key, out var v) ? v.GetString() : null;
    }

    [Theory]
    [InlineData(false, true, true)]
    [InlineData(true, true, false)]
    [InlineData(false, false, false)]
    public void ShouldImport_OnlyWhenNewMissingAndOldPresent(bool newExists, bool oldExists, bool expected)
        => Assert.Equal(expected, LegacySettingsImport.ShouldImport(newExists, oldExists));

    [Fact]
    public void RewritesServerUrl()
        => Assert.Equal(New, Get(LegacySettingsImport.RewritePreferencesJson(Build(("server_url", Old))), "server_url"));

    [Fact]
    public void RewritesServerUrl_TrailingSlashAndCase()
        => Assert.Equal(New, Get(LegacySettingsImport.RewritePreferencesJson(
            Build(("server_url", "https://FatGuysSpeak-server.azurewebsites.net/"))), "server_url"));

    [Fact]
    public void KeepsLocalhostServerUrl()
        => Assert.Equal("http://localhost:5238", Get(LegacySettingsImport.RewritePreferencesJson(
            Build(("server_url", "http://localhost:5238"))), "server_url"));

    [Fact]
    public void SavedServers_RewrittenAndDeduped()
    {
        var input = $"{Old}|http://localhost:5238|http://localhost:5239|http://localhost:5238|{New}";
        Assert.Equal($"{New}|http://localhost:5238|http://localhost:5239",
            Get(LegacySettingsImport.RewritePreferencesJson(Build(("saved_servers", input))), "saved_servers"));
    }

    [Fact]
    public void SavedServers_EmptyEntriesDropped()
        => Assert.Equal("a|b", Get(LegacySettingsImport.RewritePreferencesJson(Build(("saved_servers", "a||b"))), "saved_servers"));

    [Fact]
    public void AbsentServerKeys_StayAbsent()
    {
        var output = LegacySettingsImport.RewritePreferencesJson(Build(("ui_theme", "Ember")));
        Assert.Null(Get(output, "server_url"));
        Assert.Null(Get(output, "saved_servers"));
    }

    [Fact]
    public void OtherKeysPreserved()
    {
        var prefs = new[]
        {
            ("saved_username", "BillyBong"), ("remember_me", "True"), ("PttVirtualKey_1", "49"),
            ("audio_input_device", "1"), ("ui_theme", "Ember"), ("note", "a+b é"),
        };
        var output = LegacySettingsImport.RewritePreferencesJson(Build(prefs));
        foreach (var (key, value) in prefs) Assert.Equal(value, Get(output, key));
        Assert.Contains("a+b é", output); // relaxed encoder: no \u escapes for '+' or non-ASCII
    }
}
