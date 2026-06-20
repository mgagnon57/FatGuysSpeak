using FatGuysSpeak.Server.Data;
using Microsoft.EntityFrameworkCore;

namespace FatGuysSpeak.Server.Services;

/// <summary>
/// Every few hours, has PorkChop read each server's recent chat and learn the nicknames/aliases people
/// go by, so "what others say about them" can be attributed even when the crew never uses the username.
/// </summary>
public class AliasLearningService(
    IServiceScopeFactory scopeFactory,
    BotService bot,
    ILogger<AliasLearningService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try { await Task.Delay(TimeSpan.FromMinutes(2), stoppingToken); }
        catch (OperationCanceledException) { return; }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = scopeFactory.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                var serverIds = await db.Servers.Select(s => s.Id).ToListAsync(stoppingToken);
                foreach (var id in serverIds)
                {
                    if (stoppingToken.IsCancellationRequested) break;
                    await bot.LearnAliasesAsync(id);
                }
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex) { logger.LogError(ex, "AliasLearningService pass failed"); }

            try { await Task.Delay(TimeSpan.FromHours(3), stoppingToken); }
            catch (OperationCanceledException) { break; }
        }
    }
}
