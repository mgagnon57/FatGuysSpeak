using System.Net.Http.Json;
using System.Text.Json.Serialization;

namespace FatGuysSpeak.Client.Services;

public record GifResult(string PreviewUrl, string Url);

public class GifService
{
    private readonly HttpClient _http = new();
    private const string Base = "https://api.giphy.com/v1/gifs";

    public Task<List<GifResult>> GetTrendingAsync(string apiKey, int limit = 24) =>
        FetchAsync($"{Base}/trending?api_key={apiKey}&limit={limit}&rating=g");

    public Task<List<GifResult>> SearchAsync(string query, string apiKey, int limit = 24) =>
        FetchAsync($"{Base}/search?api_key={apiKey}&q={Uri.EscapeDataString(query)}&limit={limit}&rating=g");

    // Throws HttpRequestException on HTTP error, InvalidOperationException on bad JSON
    private async Task<List<GifResult>> FetchAsync(string url)
    {
        var resp = await _http.GetAsync(url);
        var body = await resp.Content.ReadAsStringAsync();
        if (!resp.IsSuccessStatusCode)
        {
            var snippet = body.Length > 200 ? body[..200] : body;
            throw new HttpRequestException($"Giphy API returned {(int)resp.StatusCode}: {snippet}");
        }

        var parsed = System.Text.Json.JsonSerializer.Deserialize<GiphyResponse>(body);
        return parsed?.Data?
            .Select(g => new GifResult(
                g.Images?.FixedHeightSmall?.Url ?? "",
                g.Images?.Original?.Url ?? ""))
            .Where(r => !string.IsNullOrEmpty(r.PreviewUrl) && !string.IsNullOrEmpty(r.Url))
            .ToList() ?? [];
    }

    // ── JSON response models ──────────────────────────────────────────────────
    private class GiphyResponse { [JsonPropertyName("data")] public List<GiphyGif>? Data { get; set; } }
    private class GiphyGif     { [JsonPropertyName("images")] public GiphyImages? Images { get; set; } }
    private class GiphyImages
    {
        [JsonPropertyName("fixed_height_small")] public GiphyImage? FixedHeightSmall { get; set; }
        [JsonPropertyName("original")]           public GiphyImage? Original { get; set; }
    }
    private class GiphyImage { [JsonPropertyName("url")] public string? Url { get; set; } }
}
