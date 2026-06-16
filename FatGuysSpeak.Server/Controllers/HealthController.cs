using FatGuysSpeak.Server.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace FatGuysSpeak.Server.Controllers;

[ApiController]
[AllowAnonymous]
public class HealthController(ServerMetricsService metrics) : ControllerBase
{
    [HttpGet("/health")]
    public IActionResult Get()
    {
        var snap = metrics.GetSnapshot();
        return Ok(new
        {
            status = "healthy",
            uptime = snap.UptimeFormatted,
            uptimeSeconds = snap.UptimeSeconds,
            onlineUsers = snap.OnlineUsers,
            timestamp = DateTime.UtcNow
        });
    }
}
