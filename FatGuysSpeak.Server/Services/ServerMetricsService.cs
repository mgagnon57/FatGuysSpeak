using System.Collections.Concurrent;
using System.Diagnostics;

namespace FatGuysSpeak.Server.Services;

public class ServerMetricsService
{
    private readonly DateTime _startTime = DateTime.UtcNow;
    private readonly ConcurrentQueue<DateTime> _messageTimestamps = new();
    private long _totalMessages;

    // Rate limit hit tracking
    private readonly ConcurrentQueue<(DateTime Time, string Who)> _rateLimitHits = new();

    public void RecordRateLimitHit(string who)
    {
        var now = DateTime.UtcNow;
        _rateLimitHits.Enqueue((now, who));
        while (_rateLimitHits.TryPeek(out var oldest) && (now - oldest.Time).TotalMinutes > 61)
            _rateLimitHits.TryDequeue(out _);
    }

    public RateLimitSnapshot GetRateLimitSnapshot()
    {
        var now = DateTime.UtcNow;
        var hits = _rateLimitHits.ToArray();

        var lastMin  = hits.Count(h => (now - h.Time).TotalSeconds <= 60);
        var lastHour = hits.Length;

        var history = new int[60];
        for (int i = 0; i < 60; i++)
            history[i] = hits.Count(h => { var age = (now - h.Time).TotalMinutes; return age >= i && age < i + 1; });

        var topOffenders = hits
            .GroupBy(h => h.Who)
            .Select(g => new RateLimitOffender(g.Key, g.Count(), g.Max(h => h.Time)))
            .OrderByDescending(o => o.Hits)
            .Take(10)
            .ToList();

        return new RateLimitSnapshot(lastMin, lastHour, history, topOffenders);
    }

    private TimeSpan _prevCpuTime = TimeSpan.Zero;
    private DateTime _prevCpuCheck;
    private double _lastCpuPercent;
    private readonly object _cpuLock = new();

    public void RecordMessage()
    {
        Interlocked.Increment(ref _totalMessages);
        var now = DateTime.UtcNow;
        _messageTimestamps.Enqueue(now);
        // Prune entries older than 61 minutes
        while (_messageTimestamps.TryPeek(out var oldest) && (now - oldest).TotalMinutes > 61)
            _messageTimestamps.TryDequeue(out _);
    }

    public MetricSnapshot GetSnapshot()
    {
        var now = DateTime.UtcNow;
        var proc = Process.GetCurrentProcess();

        double cpu;
        lock (_cpuLock)
        {
            var elapsed = (now - _prevCpuCheck).TotalMilliseconds;
            if (elapsed > 500)
            {
                var used = (proc.TotalProcessorTime - _prevCpuTime).TotalMilliseconds;
                _lastCpuPercent = Math.Min(100, used / (elapsed * Environment.ProcessorCount) * 100.0);
                _prevCpuTime = proc.TotalProcessorTime;
                _prevCpuCheck = now;
            }
            cpu = _lastCpuPercent;
        }

        var stamps = _messageTimestamps.ToArray();
        var msgsLastMin = stamps.Count(t => (now - t).TotalSeconds <= 60);

        // index 0 = current (partial) minute, index 59 = 60 min ago
        var history = new int[60];
        for (int i = 0; i < 60; i++)
            history[i] = stamps.Count(t => { var age = (now - t).TotalMinutes; return age >= i && age < i + 1; });

        return new MetricSnapshot(
            OnlineUsers: FatGuysSpeak.Server.Hubs.ChatHub.OnlineUserCount,
            VoiceParticipants: FatGuysSpeak.Server.Hubs.ChatHub.VoiceParticipantCount,
            ActiveStreams: FatGuysSpeak.Server.Hubs.ChatHub.ActiveStreamCount,
            TotalMessages: (int)Interlocked.Read(ref _totalMessages),
            MessagesLastMinute: msgsLastMin,
            MessageHistory: history,
            MemoryMb: proc.WorkingSet64 / (1024 * 1024),
            CpuPercent: Math.Round(cpu, 1),
            UptimeSeconds: (long)(now - _startTime).TotalSeconds,
            UptimeFormatted: FormatUptime(now - _startTime)
        );
    }

    private static string FormatUptime(TimeSpan t)
    {
        if (t.TotalDays >= 1) return $"{(int)t.TotalDays}d {t.Hours}h {t.Minutes}m";
        if (t.TotalHours >= 1) return $"{(int)t.TotalHours}h {t.Minutes}m {t.Seconds}s";
        return $"{t.Minutes}m {t.Seconds}s";
    }
}

public record MetricSnapshot(
    int OnlineUsers,
    int VoiceParticipants,
    int ActiveStreams,
    int TotalMessages,
    int MessagesLastMinute,
    int[] MessageHistory,
    long MemoryMb,
    double CpuPercent,
    long UptimeSeconds,
    string UptimeFormatted
);

public record RateLimitOffender(string Who, int Hits, DateTime LastSeen);
public record RateLimitSnapshot(
    int HitsLastMinute,
    int HitsLastHour,
    int[] HitHistory,
    List<RateLimitOffender> TopOffenders
);
