using System.Globalization;
using System.Security.Claims;
using FatGuysSpeak.Server.Data;
using FatGuysSpeak.Server.Services;
using FatGuysSpeak.Shared;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace FatGuysSpeak.Server.Controllers;

[ApiController]
[Route("api/channels/{channelId}/summary")]
[Authorize]
[Microsoft.AspNetCore.RateLimiting.EnableRateLimiting("ai")]
public class SummariesController(AppDbContext db, BotService bot) : ControllerBase
{
    private int UserId => int.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);

    // GET /api/channels/{channelId}/summary?date=yyyy-MM-dd&source=Text|Voice|Stream  (UTC day)
    [HttpGet]
    public async Task<ActionResult<DailySummaryDto>> Get(int channelId, [FromQuery] string date, [FromQuery] string source = "Text")
    {
        if (!DateTime.TryParseExact(date, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var day))
            return BadRequest("Date must be yyyy-MM-dd.");
        if (!Enum.TryParse<MessageSource>(source, ignoreCase: true, out var src))
            src = MessageSource.Text;

        // Caller must be a member of the channel's server.
        var channel = await db.Channels.FirstOrDefaultAsync(c => c.Id == channelId);
        if (channel is null) return NotFound();
        if (!await db.ServerMembers.AnyAsync(m => m.ServerId == channel.ServerId && m.UserId == UserId))
            return Forbid();

        var summary = await bot.GetOrCreateDailySummaryAsync(channelId, day, src);
        if (summary is null) return NoContent();   // today/future day, or generation unavailable
        return summary;
    }
}
