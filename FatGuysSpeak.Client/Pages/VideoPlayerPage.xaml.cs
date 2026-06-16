namespace FatGuysSpeak.Client.Pages;

public partial class VideoPlayerPage : ContentPage
{
    private static readonly HashSet<string> AllowedHosts = new(StringComparer.OrdinalIgnoreCase)
    {
        "youtube.com", "www.youtube.com", "youtu.be", "m.youtube.com",
        "twitch.tv", "www.twitch.tv", "clips.twitch.tv",
        "vimeo.com", "www.vimeo.com",
        "streamable.com", "www.streamable.com",
    };

    private readonly string _embedUrl;
    private readonly string _originalUrl;

    public VideoPlayerPage(string embedUrl, string title, string originalUrl)
    {
        InitializeComponent();
        _embedUrl = embedUrl;
        _originalUrl = originalUrl;
        TitleLabel.Text = title;
    }

    protected override void OnAppearing()
    {
        base.OnAppearing();
        if (!Uri.TryCreate(_originalUrl, UriKind.Absolute, out var uri) ||
            !AllowedHosts.Contains(uri.Host))
        {
            VideoWebView.Source = null;
            return;
        }
        // Load the original URL — WebView2 is a full browser so no iframe embedding
        // restrictions apply (YouTube error 153 only affects iframe embed contexts).
        VideoWebView.Source = new UrlWebViewSource { Url = _originalUrl };
    }

    private async void OnOpenInBrowser(object sender, EventArgs e)
    {
        try { await Launcher.OpenAsync(_originalUrl); }
        catch { }
    }
}
