using System.Text.Json;
using FatGuysSpeak.Shared;

namespace FatGuysSpeak.Server.Services;

/// <summary>Polls the GitHub Releases API for the latest release and caches it.
/// Best-effort: any failure leaves the cache unchanged and surfaces no error.</summary>
public sealed class UpdateCheckService(
    IHttpClientFactory httpFactory, IConfiguration config, UpdateStatus status,
    ILogger<UpdateCheckService> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!config.GetValue("UpdateCheck:Enabled", true)) return;
        var repo = config.GetValue<string?>("UpdateCheck:Repo") ?? "mgagnon57/FatGuysSpeak";
        var interval = TimeSpan.FromHours(Math.Max(1, config.GetValue("UpdateCheck:IntervalHours", 6)));

        while (!stoppingToken.IsCancellationRequested)
        {
            await CheckAsync(repo, stoppingToken);
            try { await Task.Delay(interval, stoppingToken); }
            catch (OperationCanceledException) { break; }
        }
    }

    private async Task CheckAsync(string repo, CancellationToken ct)
    {
        try
        {
            var http = httpFactory.CreateClient("github");
            var json = await http.GetStringAsync($"repos/{repo}/releases/latest", ct);
            var (version, url) = ParseLatestRelease(json);
            if (version is not null) status.Set(version, url);
        }
        catch (Exception ex) { log.LogDebug(ex, "Update check failed"); }
    }

    /// <summary>Pure: GitHub release JSON -> (normalized version, html_url). (null, null) when absent.</summary>
    public static (string? Version, string? Url) ParseLatestRelease(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        string? tag = root.TryGetProperty("tag_name", out var t) ? t.GetString() : null;
        string? url = root.TryGetProperty("html_url", out var u) ? u.GetString() : null;
        return (SemVer.NormalizeTag(tag), url);
    }
}
