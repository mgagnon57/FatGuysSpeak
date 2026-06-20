using System.Collections.Concurrent;
using FatGuysSpeak.Server.Hubs;

namespace FatGuysSpeak.Server.Services;

/// <summary>
/// Watches voice channels and, when two or more people are sitting in one with nobody talking for a
/// few minutes, has PorkChop bust their balls about it — posting a roast as text and speaking it into
/// the channel. Per-channel cooldown so it ribs them without nagging.
/// </summary>
public class IdleNudgeService(
    IServiceScopeFactory scopeFactory,
    BotService bot,
    TtsService tts,
    ILogger<IdleNudgeService> logger) : BackgroundService
{
    private static readonly TimeSpan IdleThreshold = TimeSpan.FromMinutes(4);
    private static readonly TimeSpan NudgeCooldown = TimeSpan.FromMinutes(15);
    private readonly ConcurrentDictionary<int, DateTime> _lastNudge = new();

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try { await Task.Delay(TimeSpan.FromSeconds(45), stoppingToken); }
        catch (OperationCanceledException) { return; }

        while (!stoppingToken.IsCancellationRequested)
        {
            try { await TickAsync(); }
            catch (OperationCanceledException) { break; }
            catch (Exception ex) { logger.LogError(ex, "IdleNudgeService tick failed"); }

            try { await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken); }
            catch (OperationCanceledException) { break; }
        }
    }

    private async Task TickAsync()
    {
        // scopeFactory isn't used directly (BotService manages its own scope), but kept so the service
        // can grow DB access later without re-plumbing.
        _ = scopeFactory;

        var now = DateTime.UtcNow;
        foreach (var (channelId, count, lastActivity) in ChatHub.VoiceActivitySnapshot())
        {
            if (count < 2) continue;                                   // need a room, not one lonely guy
            if (now - lastActivity < IdleThreshold) continue;          // someone spoke recently
            if (_lastNudge.TryGetValue(channelId, out var last) && now - last < NudgeCooldown) continue;

            _lastNudge[channelId] = now;
            var ids = ChatHub.VoiceParticipantIds(channelId);
            var line = await bot.GenerateAndPostIdleNudgeAsync(channelId, ids);
            if (line is not null)
                await tts.SpeakIntoVoiceChannelAsync(channelId, line);   // also say it out loud
        }
    }
}
