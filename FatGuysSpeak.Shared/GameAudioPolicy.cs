namespace FatGuysSpeak.Shared;

/// <summary>Decides whether Stream Game may capture a process's sound. Voice must never be streamed (legal
/// requirement), and process loopback captures the target's whole process tree — so FatGuysSpeak itself,
/// anything that launched it, and voice-chat apps are always refused (the stream goes video-only).</summary>
public static class GameAudioPolicy
{
    private static readonly HashSet<string> VoiceApps = new(StringComparer.OrdinalIgnoreCase)
    {
        "FatGuysSpeak.Client", "Discord", "DiscordPTB", "DiscordCanary", "Teams", "ms-teams", "Zoom", "Skype",
        "ts3client_win64", "ts3client_win32", "TeamSpeak", "mumble", "Ventrilo", "Slack", "WhatsApp", "Telegram",
    };

    private const int MaxDepth = 64;

    /// <param name="parentOf">Parent process id of a pid, or null when unknown / at the root.</param>
    public static bool AllowsAudio(int targetPid, string targetProcessName, int selfPid, Func<int, int?> parentOf)
    {
        if (targetPid == selfPid || VoiceApps.Contains(targetProcessName)) return false;

        // Refuse if the target is an ancestor of FatGuysSpeak (its tree would include us).
        var pid = selfPid;
        for (var depth = 0; depth < MaxDepth; depth++)
        {
            if (parentOf(pid) is not int parent || parent == pid) break;
            if (parent == targetPid) return false;
            pid = parent;
        }
        return true;
    }
}
