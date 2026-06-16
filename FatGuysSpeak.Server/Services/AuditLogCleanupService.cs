using FatGuysSpeak.Server.Data;
using Microsoft.EntityFrameworkCore;

namespace FatGuysSpeak.Server.Services;

public class AuditLogCleanupService(IServiceScopeFactory scopeFactory, IConfiguration config, ILogger<AuditLogCleanupService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            await Task.Delay(TimeSpan.FromHours(24), stoppingToken);
            try
            {
                var retentionDays = config.GetValue<int>("AuditLog:RetentionDays", 90);
                var cutoff = DateTime.UtcNow.AddDays(-retentionDays);
                using var scope = scopeFactory.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                var deleted = await db.AuditLogs.Where(a => a.CreatedAt < cutoff).ExecuteDeleteAsync(stoppingToken);
                if (deleted > 0)
                    logger.LogInformation("Purged {Count} audit log entries older than {Days} days", deleted, retentionDays);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex) { logger.LogError(ex, "AuditLogCleanupService error"); }
        }
    }
}
