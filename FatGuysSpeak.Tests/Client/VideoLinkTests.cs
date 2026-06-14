using FatGuysSpeak.Shared;

namespace FatGuysSpeak.Tests.Client;

public class VideoLinkTests
{
    // VideoUrlHelper.GetEmbedUrl

    [Fact]
    public void YouTube_WatchUrl_ReturnsEmbedUrl()
    {
        var embed = VideoUrlHelper.GetEmbedUrl("https://www.youtube.com/watch?v=dQw4w9WgXcQ");
        Assert.Equal("https://www.youtube.com/embed/dQw4w9WgXcQ", embed);
    }

    [Fact]
    public void YouTube_ShortUrl_ReturnsEmbedUrl()
    {
        var embed = VideoUrlHelper.GetEmbedUrl("https://youtu.be/dQw4w9WgXcQ");
        Assert.Equal("https://www.youtube.com/embed/dQw4w9WgXcQ", embed);
    }

    [Fact]
    public void YouTube_WatchUrlWithExtraParams_ReturnsEmbedUrl()
    {
        var embed = VideoUrlHelper.GetEmbedUrl("https://www.youtube.com/watch?v=dQw4w9WgXcQ&t=30s");
        Assert.Equal("https://www.youtube.com/embed/dQw4w9WgXcQ", embed);
    }

    [Fact]
    public void Vimeo_ReturnsEmbedUrl()
    {
        var embed = VideoUrlHelper.GetEmbedUrl("https://vimeo.com/123456789");
        Assert.Equal("https://player.vimeo.com/video/123456789", embed);
    }

    [Fact]
    public void Twitch_ReturnsEmbedUrl()
    {
        var embed = VideoUrlHelper.GetEmbedUrl("https://www.twitch.tv/shroud");
        Assert.Equal("https://player.twitch.tv/?channel=shroud&parent=localhost", embed);
    }

    [Fact]
    public void Twitch_WithTrailingSlash_ReturnsEmbedUrl()
    {
        var embed = VideoUrlHelper.GetEmbedUrl("https://www.twitch.tv/shroud/");
        Assert.Equal("https://player.twitch.tv/?channel=shroud&parent=localhost", embed);
    }

    [Fact]
    public void NonVideoUrl_ReturnsNull()
    {
        Assert.Null(VideoUrlHelper.GetEmbedUrl("https://github.com/dotnet/maui"));
    }

    [Fact]
    public void NullUrl_ReturnsNull()
    {
        Assert.Null(VideoUrlHelper.GetEmbedUrl(null));
    }

    [Fact]
    public void YouTube_EmbedUrl_ReturnsEmbedUrl()
    {
        var embed = VideoUrlHelper.GetEmbedUrl("https://www.youtube.com/embed/dQw4w9WgXcQ");
        Assert.Equal("https://www.youtube.com/embed/dQw4w9WgXcQ", embed);
    }
}
