namespace FatGuysSpeak.Server.Services;

/// <summary>
/// Tracks cumulative wall-clock online time per user. A user is "online" from their
/// first connection to their last disconnect; overlapping connections count once.
/// Thread-safe. Inject a clock for testing.
/// </summary>
public class OnlineTimeTracker(Func<DateTime>? clock = null)
{
    private readonly Func<DateTime> _now = clock ?? (() => DateTime.UtcNow);
    private readonly Dictionary<int, (int Count, DateTime Since)> _sessions = new();
    private readonly object _lock = new();

    public void Connect(int userId)
    {
        lock (_lock)
        {
            if (_sessions.TryGetValue(userId, out var s) && s.Count > 0)
                _sessions[userId] = (s.Count + 1, s.Since);
            else
                _sessions[userId] = (1, _now());
        }
    }

    /// <summary>Returns seconds to add to the user's total when their last connection drops; else 0.</summary>
    public long Disconnect(int userId)
    {
        lock (_lock)
        {
            if (!_sessions.TryGetValue(userId, out var s) || s.Count <= 0)
                return 0;
            if (s.Count > 1)
            {
                _sessions[userId] = (s.Count - 1, s.Since);
                return 0;
            }
            _sessions.Remove(userId);
            var secs = (long)(_now() - s.Since).TotalSeconds;
            return secs < 0 ? 0 : secs;
        }
    }

    /// <summary>Seconds of the in-progress session if currently online, else 0.</summary>
    public long LiveSeconds(int userId)
    {
        lock (_lock)
        {
            if (_sessions.TryGetValue(userId, out var s) && s.Count > 0)
            {
                var secs = (long)(_now() - s.Since).TotalSeconds;
                return secs < 0 ? 0 : secs;
            }
            return 0;
        }
    }
}
