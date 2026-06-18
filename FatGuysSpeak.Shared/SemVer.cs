namespace FatGuysSpeak.Shared;

/// <summary>Pure, tolerant SemVer helpers for update-version comparison. Never throws.</summary>
public static class SemVer
{
    // "v1.1.0"/"V1.1.0" -> "1.1.0"; "1.1.0" -> "1.1.0"; null / non MAJOR.MINOR.PATCH -> null.
    public static string? NormalizeTag(string? tagName)
    {
        if (string.IsNullOrWhiteSpace(tagName)) return null;
        var s = tagName.Trim();
        if (s.Length > 0 && (s[0] == 'v' || s[0] == 'V')) s = s[1..];
        var parts = s.Split('.');
        if (parts.Length != 3) return null;
        foreach (var p in parts)
            if (!int.TryParse(p, out _)) return null;
        return s;
    }

    // Numeric per-component compare of MAJOR.MINOR.PATCH. Unparseable components -> 0.
    public static int Compare(string? a, string? b)
    {
        var (am, ai, ap) = Parts(a);
        var (bm, bi, bp) = Parts(b);
        if (am != bm) return am.CompareTo(bm);
        if (ai != bi) return ai.CompareTo(bi);
        return ap.CompareTo(bp);
    }

    // True only when `current` is strictly older than a valid `latest`.
    public static bool IsOutdated(string? current, string? latest)
    {
        var l = NormalizeTag(latest);
        return l is not null && Compare(current, l) < 0;
    }

    private static (int, int, int) Parts(string? v)
    {
        if (string.IsNullOrWhiteSpace(v)) return (0, 0, 0);
        var s = v.Trim();
        if (s.Length > 0 && (s[0] == 'v' || s[0] == 'V')) s = s[1..];
        var p = s.Split('.');
        int N(int i) => p.Length > i && int.TryParse(p[i], out var n) ? n : 0;
        return (N(0), N(1), N(2));
    }
}
