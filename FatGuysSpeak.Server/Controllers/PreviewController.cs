using System.Net;
using System.Net.Sockets;
using System.Text.RegularExpressions;
using FatGuysSpeak.Shared;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace FatGuysSpeak.Server.Controllers;

[ApiController]
[Route("api/[controller]")]
[Authorize]
public partial class PreviewController(IHttpClientFactory httpClientFactory) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> Get([FromQuery] string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
            (uri.Scheme != "http" && uri.Scheme != "https"))
            return BadRequest("Invalid URL.");

        // SSRF: resolve hostname and block private/loopback ranges
        try
        {
            var addresses = await Dns.GetHostAddressesAsync(uri.Host);
            if (addresses.Length == 0 || addresses.Any(IsPrivateOrLoopback))
                return BadRequest("Invalid URL.");
        }
        catch
        {
            return BadRequest("Invalid URL.");
        }

        var client = httpClientFactory.CreateClient("preview");
        try
        {
            using var resp = await client.GetAsync(uri, HttpCompletionOption.ResponseContentRead);
            var contentType = resp.Content.Headers.ContentType?.MediaType ?? "";
            if (!contentType.Contains("html")) return NoContent();

            var html = await resp.Content.ReadAsStringAsync();
            var preview = ParseOgTags(html, url);
            return preview is null ? NoContent() : Ok(preview);
        }
        catch
        {
            return NoContent();
        }
    }

    private static bool IsPrivateOrLoopback(IPAddress ip)
    {
        if (IPAddress.IsLoopback(ip)) return true;
        if (ip.AddressFamily == AddressFamily.InterNetworkV6)
            return ip.IsIPv6LinkLocal || ip.IsIPv6SiteLocal || ip.Equals(IPAddress.IPv6Loopback);
        var b = ip.GetAddressBytes();
        return b[0] == 10
            || (b[0] == 172 && b[1] >= 16 && b[1] <= 31)
            || (b[0] == 192 && b[1] == 168)
            || (b[0] == 169 && b[1] == 254)
            || b[0] == 127;
    }

    private static LinkPreviewDto? ParseOgTags(string html, string originalUrl)
    {
        static string? GetOg(string html, string property)
        {
            // Handle both attribute orderings
            var m = OgPropertyFirst().Match(html.Replace('\n', ' '));
            while (m.Success)
            {
                if (m.Groups[1].Value.Equals(property, StringComparison.OrdinalIgnoreCase))
                    return WebUtility.HtmlDecode(m.Groups[2].Value);
                m = m.NextMatch();
            }
            m = OgContentFirst().Match(html.Replace('\n', ' '));
            while (m.Success)
            {
                if (m.Groups[2].Value.Equals(property, StringComparison.OrdinalIgnoreCase))
                    return WebUtility.HtmlDecode(m.Groups[1].Value);
                m = m.NextMatch();
            }
            return null;
        }

        var title = GetOg(html, "title")
            ?? WebUtility.HtmlDecode(PageTitle().Match(html).Groups[1].Value.Trim());
        var description = GetOg(html, "description");
        var image = GetOg(html, "image");
        var siteName = GetOg(html, "site_name");

        if (string.IsNullOrWhiteSpace(title) && string.IsNullOrWhiteSpace(image))
            return null;

        // Only allow absolute image URLs
        if (image is not null && !image.StartsWith("http", StringComparison.OrdinalIgnoreCase))
            image = null;

        return new LinkPreviewDto(originalUrl, title?.Trim(), description?.Trim(), image, siteName?.Trim());
    }

    [GeneratedRegex(@"<meta[^>]+property=""og:([^""]+)""[^>]+content=""([^""]*)""", RegexOptions.IgnoreCase)]
    private static partial Regex OgPropertyFirst();

    [GeneratedRegex(@"<meta[^>]+content=""([^""]*)""[^>]+property=""og:([^""]+)""", RegexOptions.IgnoreCase)]
    private static partial Regex OgContentFirst();

    [GeneratedRegex(@"<title[^>]*>([^<]*)</title>", RegexOptions.IgnoreCase)]
    private static partial Regex PageTitle();
}
