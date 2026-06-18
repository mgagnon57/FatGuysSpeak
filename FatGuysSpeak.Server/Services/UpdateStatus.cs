namespace FatGuysSpeak.Server.Services;

/// <summary>Thread-safe holder for the last successful GitHub release check.</summary>
public sealed class UpdateStatus
{
    private readonly object _lock = new();
    private string? _latest;
    private string? _url;
    private DateTime? _checkedAt;

    public string? LatestVersion { get { lock (_lock) return _latest; } }
    public string? ReleaseUrl { get { lock (_lock) return _url; } }
    public DateTime? CheckedAtUtc { get { lock (_lock) return _checkedAt; } }

    public void Set(string? latest, string? url)
    {
        lock (_lock) { _latest = latest; _url = url; _checkedAt = DateTime.UtcNow; }
    }
}
