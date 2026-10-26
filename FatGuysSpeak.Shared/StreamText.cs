namespace FatGuysSpeak.Shared;

/// <summary>Stream wording and rules shared by the server (notifications) and the client (labels, gating).</summary>
public static class StreamText
{
    private const int Windows11Build = 22000;

    public static bool SupportsGameStream(int osBuild) => osBuild >= Windows11Build;

    /// <summary>Server-side guard on the game name a client sends: no control chars, trimmed, capped.</summary>
    public static string SanitizeGameName(string? name)
    {
        var clean = new string((name ?? "").Where(c => !char.IsControl(c)).ToArray()).Trim();
        if (clean.Length == 0) return "a game";
        return clean.Length > GameNames.MaxLength ? clean[..GameNames.MaxLength] : clean;
    }

    public static string ViewerLabel(StreamKind kind, string streamer, string? game) => kind == StreamKind.Game
        ? $"🎮 {streamer} is streaming {game ?? "a game"}"
        : $"🖥 {streamer} is sharing their screen";
}
