using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace FatGuysSpeak.Server.Controllers;

[ApiController]
[Authorize]
[Route("api/gifs")]
public class GifController(IHttpClientFactory httpFactory, IConfiguration config) : ControllerBase
{
    [HttpGet("trending")]
    public Task<IActionResult> Trending([FromQuery] int limit = 24) =>
        FetchAsync($"trending?api_key={{KEY}}&limit={limit}&rating=g");

    [HttpGet("search")]
    public Task<IActionResult> Search([FromQuery] string q = "", [FromQuery] int limit = 24) =>
        FetchAsync($"search?api_key={{KEY}}&q={Uri.EscapeDataString(q)}&limit={limit}&rating=g");

    private async Task<IActionResult> FetchAsync(string urlTemplate)
    {
        var apiKey = config["Giphy:ApiKey"] ?? "";
        if (string.IsNullOrWhiteSpace(apiKey))
            return Ok(Array.Empty<object>());

        var url = urlTemplate.Replace("{KEY}", apiKey);
        var http = httpFactory.CreateClient("giphy");
        var resp = await http.GetFromJsonAsync<GiphyResponse>(url);
        var results = resp?.Data?
            .Select(g => new
            {
                PreviewUrl = g.Images?.FixedHeightSmall?.Url ?? "",
                Url        = g.Images?.Original?.Url ?? "",
            })
            .Where(r => r.PreviewUrl.Length > 0 && r.Url.Length > 0)
            .ToList() ?? [];

        return Ok(results);
    }

    private class GiphyResponse { [JsonPropertyName("data")] public List<GiphyGif>? Data { get; set; } }
    private class GiphyGif     { [JsonPropertyName("images")] public GiphyImages? Images { get; set; } }
    private class GiphyImages
    {
        [JsonPropertyName("fixed_height_small")] public GiphyImage? FixedHeightSmall { get; set; }
        [JsonPropertyName("original")]           public GiphyImage? Original { get; set; }
    }
    private class GiphyImage { [JsonPropertyName("url")] public string? Url { get; set; } }
}
