using FatGuysSpeak.Server.Data;
using Microsoft.EntityFrameworkCore;

namespace FatGuysSpeak.Server.Services;

/// <summary>
/// Once a day, posts PorkChop's server-wide digest for the most recently completed week (Monday-anchored
/// UTC) into each server's default channel. Idempotent: <see cref="BotService.GenerateAndPostWeeklyDigestAsync"/>
/// skips any (server, week) already posted, so repeated passes are safe.
/// </summary>
public class WeeklyDigestService(
    IServiceScopeFactory scopeFactory,
    BotService bot,
    ILogger<WeeklyDigestService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try { await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken); }
        catch (OperationCanceledException) { return; }

        while (!stoppingToken.IsCancellationRequested)
        {
            try { await RunOnceAsync(stoppingToken); }
            catch (OperationCanceledException) { break; }
            catch (Exception ex) { logger.LogError(ex, "WeeklyDigestService pass failed"); }

            try { await Task.Delay(TimeSpan.FromHours(24), stoppingToken); }
            catch (OperationCanceledException) { break; }
        }
    }

    /// <summary>Runs a single digest pass for the last completed week. Public for tests.</summary>
    public async Task RunOnceAsync(CancellationToken ct = default)
    {
        var lastWeekStart = MondayOf(DateTime.UtcNow).AddDays(-7);

        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var serverIds = await db.Servers.Select(s => s.Id).ToListAsync(ct);

        var posted = 0;
        foreach (var id in serverIds)
        {
            if (ct.IsCancellationRequested) return;
            var digest = await bot.GenerateAndPostWeeklyDigestAsync(id, lastWeekStart);
            if (digest is not null) posted++;
        }

        if (posted > 0)
            logger.LogInformation("WeeklyDigestService posted {Count} weekly digests for week of {Week:yyyy-MM-dd}", posted, lastWeekStart);
    }

    // The Monday 00:00 UTC that begins the calendar week containing <paramref name="d"/>.
    public static DateTime MondayOf(DateTime d) => d.Date.AddDays(-(((int)d.DayOfWeek + 6) % 7));
}
