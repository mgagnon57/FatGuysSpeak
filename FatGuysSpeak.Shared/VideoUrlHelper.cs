using System.Text.RegularExpressions;

namespace FatGuysSpeak.Shared;

public static class VideoUrlHelper
{
    private static readonly Regex YoutubeRegex = new(@"(?:youtube\.com/(?:watch\?.*v=|embed/)|youtu\.be/)([A-Za-z0-9_-]{11})", RegexOptions.Compiled);
    private static readonly Regex VimeoRegex = new(@"vimeo\.com/(\d+)", RegexOptions.Compiled);
    private static readonly Regex TwitchRegex = new(@"twitch\.tv/([A-Za-z0-9_]+)(?:$|[/?])", RegexOptions.Compiled);

    public static string? GetEmbedUrl(string? url)
    {
        if (url is null) return null;
        var yt = YoutubeRegex.Match(url);
        if (yt.Success) return $"https://www.youtube.com/embed/{yt.Groups[1].Value}";
        var vi = VimeoRegex.Match(url);
        if (vi.Success) return $"https://player.vimeo.com/video/{vi.Groups[1].Value}";
        var tw = TwitchRegex.Match(url);
        if (tw.Success) return $"https://player.twitch.tv/?channel={tw.Groups[1].Value}&parent=localhost";
        return null;
    }
}
