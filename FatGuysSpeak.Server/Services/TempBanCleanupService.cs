using FatGuysSpeak.Server.Data;
using Microsoft.EntityFrameworkCore;

namespace FatGuysSpeak.Server.Services;

public class TempBanCleanupService(IServiceScopeFactory scopeFactory, ILogger<TempBanCleanupService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            await Task.Delay(TimeSpan.FromMinutes(5), stoppingToken);
            try
            {
                using var scope = scopeFactory.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                var expired = await db.TempBans.Where(tb => tb.ExpiresAt <= DateTime.UtcNow).ToListAsync(stoppingToken);
                if (expired.Count > 0)
                {
                    db.TempBans.RemoveRange(expired);
                    await db.SaveChangesAsync(stoppingToken);
                    logger.LogInformation("Cleaned up {Count} expired temp bans", expired.Count);
                }
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex) { logger.LogError(ex, "TempBanCleanupService error"); }
        }
    }
}
