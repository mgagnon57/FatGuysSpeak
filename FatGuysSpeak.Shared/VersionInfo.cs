namespace FatGuysSpeak.Shared;

/// <summary>Parsed build version. Produced from an assembly's InformationalVersion
/// (format "MAJOR.MINOR.PATCH+g{sha}.{yyyy-MM-dd}"). Pure + tolerant: never throws.</summary>
public record VersionInfo(string Version, string Commit, string BuildDate)
{
    public static VersionInfo Parse(string? informational)
    {
        if (string.IsNullOrWhiteSpace(informational))
            return new VersionInfo("0.0.0", "", "");

        var plus = informational.IndexOf('+');
        if (plus < 0)
            return new VersionInfo(informational, "", "");

        var version = informational[..plus];
        var meta = informational[(plus + 1)..];

        // Expected meta: "g{sha}.{date}"
        if (meta.StartsWith('g'))
        {
            var dot = meta.IndexOf('.');
            if (dot > 1)
            {
                var sha = meta[1..dot];
                var date = meta[(dot + 1)..];
                if (sha == "nogit") sha = "";
                return new VersionInfo(version, sha, date);
            }
        }

        // Unrecognized metadata: keep it as a best-effort commit, no date.
        return new VersionInfo(version, meta, "");
    }
}
