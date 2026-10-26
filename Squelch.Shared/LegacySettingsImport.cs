using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Squelch.Shared;

/// <summary>Carries the old FatGuysSpeak client's MAUI preferences over to Squelch on first launch, repointing the
/// old Azure server URL at the new one. Pure: the caller does the file I/O.</summary>
public static class LegacySettingsImport
{
    public const string OldServerUrl = "https://fatguysspeak-server.azurewebsites.net";
    public const string NewServerUrl = "https://squelch-server.azurewebsites.net";
    public const string OldAppId = "com.companyname.fatguysspeak.client";

    private static readonly JsonSerializerOptions Options = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    /// <summary>Import only once: never overwrite a preferences file Squelch has already written.</summary>
    public static bool ShouldImport(bool newPrefsFileExists, bool oldPrefsFileExists)
        => !newPrefsFileExists && oldPrefsFileExists;

    /// <summary>Input/output is the MAUI unpackaged preferences.dat JSON, shape {"": {"key": "value", ...}}.</summary>
    public static string RewritePreferencesJson(string json)
    {
        var root = JsonNode.Parse(json)!.AsObject();
        if (root[""] is JsonObject prefs)
        {
            if (prefs["server_url"]?.GetValue<string>() is { } url)
                prefs["server_url"] = Rewrite(url);

            if (prefs["saved_servers"]?.GetValue<string>() is { } saved)
            {
                var entries = saved.Split('|', StringSplitOptions.RemoveEmptyEntries)
                    .Select(Rewrite).Distinct(StringComparer.OrdinalIgnoreCase);
                prefs["saved_servers"] = string.Join('|', entries);
            }
        }
        return root.ToJsonString(Options);
    }

    private static string Rewrite(string url)
        => string.Equals(url.TrimEnd('/'), OldServerUrl, StringComparison.OrdinalIgnoreCase) ? NewServerUrl : url;
}
