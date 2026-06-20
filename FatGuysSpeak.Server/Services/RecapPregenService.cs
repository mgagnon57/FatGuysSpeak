using FatGuysSpeak.Server.Data;
using FatGuysSpeak.Shared;
using Microsoft.EntityFrameworkCore;

namespace FatGuysSpeak.Server.Services;

/// <summary>
/// Pre-generates PorkChop daily recaps for recently completed days so the first viewer never waits
/// on a live Claude call. Each pass walks the last few completed UTC days and, for every
/// (channel, source) that actually had Text/Voice activity and isn't cached yet, asks the
/// <see cref="BotService"/> to generate and store the recap. Idempotent: cache hits are skipped.
/// </summary>
public class RecapPregenService(
    IServiceScopeFactory scopeFactory,
    BotService bot,
    ILogger<RecapPregenService> logger) : BackgroundService
{
    // How many completed days back to backfill — covers a weekend the server was offline.
    private const int DaysBack = 7;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Let the app finish starting (DB schema/migrations) before the first pass.
        try { await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken); }
        catch (OperationCanceledException) { return; }

        while (!stoppingToken.IsCancellationRequested)
        {
            try { await RunOnceAsync(stoppingToken); }
            catch (OperationCanceledException) { break; }
            catch (Exception ex) { logger.LogError(ex, "RecapPregenService pass failed"); }

            try { await Task.Delay(TimeSpan.FromHours(6), stoppingToken); }
            catch (OperationCanceledException) { break; }
        }
    }

    /// <summary>Runs a single pre-generation pass. Public so it can be exercised directly in tests.</summary>
    public async Task RunOnceAsync(CancellationToken ct = default)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var generated = 0;
        var lastCompletedDay = DateTime.UtcNow.Date.AddDays(-1);
        for (var d = 0; d < DaysBack; d++)
        {
            var day  = lastCompletedDay.AddDays(-d);
            var next = day.AddDays(1);

            // Channel/source pairs that had real Text or Voice messages that day. Stream isn't
            // day-grouped in the client, and AI replies aren't part of the conversation.
            var combos = await db.Messages
                .Where(m => !m.IsDeleted && m.CreatedAt >= day && m.CreatedAt < next
                            && (m.Source == MessageSource.Text || m.Source == MessageSource.Voice))
                .Select(m => new { m.ChannelId, m.Source })
                .Distinct()
                .ToListAsync(ct);

            foreach (var c in combos)
            {
                if (ct.IsCancellationRequested) return;
                var already = await db.DailyChatSummaries
                    .AnyAsync(s => s.ChannelId == c.ChannelId && s.Date == day && s.Source == c.Source, ct);
                if (already) continue;

                var result = await bot.GetOrCreateDailySummaryAsync(c.ChannelId, day, c.Source);
                if (result is not null) generated++;
            }
        }

        if (generated > 0)
            logger.LogInformation("RecapPregenService generated {Count} daily recaps", generated);
    }
}
