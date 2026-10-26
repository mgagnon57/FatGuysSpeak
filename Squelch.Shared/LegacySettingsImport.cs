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

    /// <summary>Written into the imported preferences; its presence means the import already happened.</summary>
    public const string ImportDoneKey = "legacy_import_done";

    /// <summary>Import when the old prefs exist and Squelch's prefs are missing or don't carry the import marker.
    /// A Squelch preferences.dat without the marker (e.g. a dev run, which shares the ApplicationId) doesn't count
    /// as imported. <paramref name="newPrefsJson"/> is null when the file is missing; unparsable JSON lacks the marker.</summary>
    public static bool ShouldImport(bool oldPrefsFileExists, string? newPrefsJson)
        => oldPrefsFileExists && !HasImportMarker(newPrefsJson);

    public static bool HasImportMarker(string? prefsJson)
    {
        if (string.IsNullOrWhiteSpace(prefsJson)) return false;
        try
        {
            return JsonNode.Parse(prefsJson) is JsonObject root
                && root[""] is JsonObject prefs
                && prefs[ImportDoneKey] is JsonValue v
                && v.TryGetValue<string>(out var s)
                && string.Equals(s, "True", StringComparison.OrdinalIgnoreCase);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>Input/output is the MAUI unpackaged preferences.dat JSON, shape {"": {"key": "value", ...}}.
    /// The output always carries <see cref="ImportDoneKey"/> = "True".</summary>
    public static string RewritePreferencesJson(string json)
    {
        var root = JsonNode.Parse(json)!.AsObject();
        if (root[""] is not JsonObject prefs)
        {
            prefs = new JsonObject();
            root[""] = prefs;
        }
        prefs[ImportDoneKey] = "True";

        if (prefs["server_url"]?.GetValue<string>() is { } url)
            prefs["server_url"] = Rewrite(url);

        if (prefs["saved_servers"]?.GetValue<string>() is { } saved)
        {
            var entries = saved.Split('|', StringSplitOptions.RemoveEmptyEntries)
                .Select(Rewrite).Distinct(StringComparer.OrdinalIgnoreCase);
            prefs["saved_servers"] = string.Join('|', entries);
        }
        return root.ToJsonString(Options);
    }

    private static string Rewrite(string url)
        => string.Equals(url.TrimEnd('/'), OldServerUrl, StringComparison.OrdinalIgnoreCase) ? NewServerUrl : url;
}
