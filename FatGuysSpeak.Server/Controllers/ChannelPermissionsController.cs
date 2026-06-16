using System.Security.Claims;
using FatGuysSpeak.Server.Data;
using FatGuysSpeak.Server.Models;
using FatGuysSpeak.Shared;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace FatGuysSpeak.Server.Controllers;

[ApiController]
[Route("api/servers/{serverId}/channels/{channelId}/permissions")]
[Authorize]
public class ChannelPermissionsController(AppDbContext db) : ControllerBase
{
    private int UserId => int.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);
    private string Username => User.FindFirstValue(ClaimTypes.Name)!;

    [HttpGet]
    public async Task<ActionResult<ChannelPermissionDto>> GetPermissions(int serverId, int channelId)
    {
        if (!await db.ServerMembers.AnyAsync(sm => sm.ServerId == serverId && sm.UserId == UserId))
            return Forbid();

        var channel = await db.Channels.FirstOrDefaultAsync(c => c.Id == channelId && c.ServerId == serverId);
        if (channel is null) return NotFound();

        var perm = await db.ChannelPermissions.FindAsync(channelId);
        return Ok(new ChannelPermissionDto(
            channelId,
            perm?.MinRoleToRead ?? ServerRole.Member,
            perm?.MinRoleToWrite ?? ServerRole.Member));
    }

    [HttpPut]
    public async Task<ActionResult<ChannelPermissionDto>> SetPermissions(int serverId, int channelId, SetChannelPermissionRequest req)
    {
        var actor = await db.ServerMembers.FindAsync(serverId, UserId);
        if (actor is null || actor.Role < ServerRole.Admin) return Forbid();

        var channel = await db.Channels.FirstOrDefaultAsync(c => c.Id == channelId && c.ServerId == serverId);
        if (channel is null) return NotFound();

        if (req.MinRoleToWrite < req.MinRoleToRead)
            return BadRequest("MinRoleToWrite must be greater than or equal to MinRoleToRead.");

        var perm = await db.ChannelPermissions.FindAsync(channelId);
        if (perm is null)
        {
            perm = new ChannelPermission { ChannelId = channelId, MinRoleToRead = req.MinRoleToRead, MinRoleToWrite = req.MinRoleToWrite };
            db.ChannelPermissions.Add(perm);
        }
        else
        {
            perm.MinRoleToRead = req.MinRoleToRead;
            perm.MinRoleToWrite = req.MinRoleToWrite;
        }

        db.AuditLogs.Add(new AuditLog
        {
            ServerId = serverId, ActorId = UserId, ActorUsername = Username,
            Action = "ChannelPermissionsChanged", TargetId = channelId,
            Detail = $"#{channel.Name}: read={req.MinRoleToRead}, write={req.MinRoleToWrite}"
        });
        await db.SaveChangesAsync();

        return Ok(new ChannelPermissionDto(channelId, perm.MinRoleToRead, perm.MinRoleToWrite));
    }
}
