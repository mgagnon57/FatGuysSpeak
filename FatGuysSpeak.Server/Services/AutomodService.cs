using System.Collections.Concurrent;

namespace FatGuysSpeak.Server.Services;

public class AutomodService : IDisposable
{
    private readonly ConcurrentDictionary<(int userId, int channelId), Queue<DateTime>> _msgTimes = new();
    private readonly ConcurrentDictionary<(int userId, int channelId), string?> _lastContent = new();
    private readonly ConcurrentDictionary<(int userId, int channelId), DateTime> _lastSeen = new();
    private readonly Timer _cleanupTimer;

    private const int SpamWindowSeconds = 5;
    private const int SpamMessageLimit = 5;
    private static readonly TimeSpan StaleThreshold = TimeSpan.FromHours(1);

    public AutomodService()
    {
        _cleanupTimer = new Timer(_ => Cleanup(), null, TimeSpan.FromMinutes(10), TimeSpan.FromMinutes(10));
    }

    public bool IsSpam(int userId, int channelId, string content)
    {
        var key = (userId, channelId);
        var now = DateTime.UtcNow;
        var window = now.AddSeconds(-SpamWindowSeconds);

        _lastSeen[key] = now;

        var queue = _msgTimes.GetOrAdd(key, _ => new Queue<DateTime>());
        lock (queue)
        {
            while (queue.Count > 0 && queue.Peek() < window)
                queue.Dequeue();
            queue.Enqueue(now);
            if (queue.Count > SpamMessageLimit) return true;
        }

        _lastContent.TryGetValue(key, out var last);
        _lastContent[key] = content;

        return last is not null && string.Equals(last.Trim(), content.Trim(), StringComparison.OrdinalIgnoreCase);
    }

    private void Cleanup()
    {
        var cutoff = DateTime.UtcNow - StaleThreshold;
        foreach (var key in _lastSeen.Keys)
        {
            if (_lastSeen.TryGetValue(key, out var seen) && seen < cutoff)
            {
                _lastSeen.TryRemove(key, out _);
                _msgTimes.TryRemove(key, out _);
                _lastContent.TryRemove(key, out _);
            }
        }
    }

    public void Dispose() => _cleanupTimer.Dispose();
}
