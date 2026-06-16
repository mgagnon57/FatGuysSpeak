namespace FatGuysSpeak.Shared;

public record LoopbackResult(bool Success, string? Code, string? Error);

/// <summary>Parses and validates the query captured on the OAuth loopback redirect.</summary>
public static class LoopbackRedirectParser
{
    public static LoopbackResult Parse(string rawQuery, string expectedState)
    {
        string? code = null, state = null, error = null;
        foreach (var pair in rawQuery.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var i = pair.IndexOf('=');
            var key = i < 0 ? pair : pair[..i];
            var val = i < 0 ? "" : Uri.UnescapeDataString(pair[(i + 1)..]);
            switch (key)
            {
                case "code": code = val; break;
                case "state": state = val; break;
                case "error": error = val; break;
            }
        }

        if (error is not null) return new LoopbackResult(false, null, error);
        if (state != expectedState) return new LoopbackResult(false, null, "state_mismatch");
        if (string.IsNullOrEmpty(code)) return new LoopbackResult(false, null, "missing_code");
        return new LoopbackResult(true, code, null);
    }
}
