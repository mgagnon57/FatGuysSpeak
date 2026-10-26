using System.Security.Claims;
using Squelch.Server.Data;
using Squelch.Server.Models;
using Squelch.Shared;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Squelch.Server.Controllers;

[ApiController]
[Authorize]
public class NotificationsController(AppDbContext db) : ControllerBase
{
    private int UserId => int.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);

    [HttpGet("api/servers/{serverId}/notif")]
    public async Task<ActionResult<NotifLevel?>> GetServerNotif(int serverId)
    {
        if (!await db.ServerMembers.AnyAsync(m => m.ServerId == serverId && m.UserId == UserId))
            return Forbid();
        var n = await db.UserServerNotifs.FindAsync(UserId, serverId);
        return Ok(n?.Level);
    }

    [HttpPut("api/servers/{serverId}/notif")]
    public async Task<IActionResult> SetServerNotif(int serverId, SetNotifLevelRequest req)
    {
        if (!await db.ServerMembers.AnyAsync(m => m.ServerId == serverId && m.UserId == UserId))
            return Forbid();

        var n = await db.UserServerNotifs.FindAsync(UserId, serverId);
        if (n is null)
        {
            db.UserServerNotifs.Add(new UserServerNotif { UserId = UserId, ServerId = serverId, Level = req.Level });
        }
        else
        {
            n.Level = req.Level;
        }
        await db.SaveChangesAsync();
        return NoContent();
    }

    [HttpGet("api/channels/{channelId}/notif")]
    public async Task<ActionResult<NotifLevel?>> GetChannelNotif(int channelId)
    {
        var channel = await db.Channels.FindAsync(channelId);
        if (channel is null) return NotFound();
        if (!await db.ServerMembers.AnyAsync(m => m.ServerId == channel.ServerId && m.UserId == UserId))
            return Forbid();
        var n = await db.UserChannelNotifs.FindAsync(UserId, channelId);
        return Ok(n?.Level);
    }

    [HttpPut("api/channels/{channelId}/notif")]
    public async Task<IActionResult> SetChannelNotif(int channelId, SetNotifLevelRequest req)
    {
        var channel = await db.Channels.FindAsync(channelId);
        if (channel is null) return NotFound();
        if (!await db.ServerMembers.AnyAsync(m => m.ServerId == channel.ServerId && m.UserId == UserId))
            return Forbid();

        var n = await db.UserChannelNotifs.FindAsync(UserId, channelId);
        if (n is null)
        {
            db.UserChannelNotifs.Add(new UserChannelNotif { UserId = UserId, ChannelId = channelId, Level = req.Level });
        }
        else
        {
            n.Level = req.Level;
        }
        await db.SaveChangesAsync();
        return NoContent();
    }
}
