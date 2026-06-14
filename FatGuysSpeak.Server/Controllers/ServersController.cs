using System.Security.Claims;
using System.Security.Cryptography;
using FatGuysSpeak.Server.Data;
using FatGuysSpeak.Server.Hubs;
using FatGuysSpeak.Server.Models;
using FatGuysSpeak.Shared;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;

namespace FatGuysSpeak.Server.Controllers;

[ApiController]
[Route("api/[controller]")]
[Authorize]
public class ServersController(AppDbContext db, IHubContext<ChatHub> hub) : ControllerBase
{
    private int UserId => int.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);
    private string Username => User.FindFirstValue(ClaimTypes.Name)!;

    [HttpGet]
    public async Task<List<ServerDto>> GetMyServers()
    {
        return await db.ServerMembers
            .Where(sm => sm.UserId == UserId)
            .Include(sm => sm.Server).ThenInclude(s => s.Members)
            .Select(sm => new ServerDto(
                sm.Server.Id,
                sm.Server.Name,
                sm.Server.Description,
                sm.Server.OwnerId.ToString(),
                sm.Server.Members.Count,
                sm.Role))
            .ToListAsync();
    }

    [HttpPost]
    public async Task<ActionResult<ServerDto>> CreateServer(CreateServerRequest req)
    {
        var server = new GuildServer { Name = req.Name, Description = req.Description, OwnerId = UserId };
        db.Servers.Add(server);
        await db.SaveChangesAsync();

        db.ServerMembers.Add(new ServerMember { ServerId = server.Id, UserId = UserId, Role = ServerRole.Admin });
        db.Channels.Add(new Channel { Name = "general", Type = ChannelType.Text, ServerId = server.Id, Position = 0 });
        db.Channels.Add(new Channel { Name = "General", Type = ChannelType.Voice, ServerId = server.Id, Position = 1 });
        await db.SaveChangesAsync();

        return Ok(new ServerDto(server.Id, server.Name, server.Description, server.OwnerId.ToString(), 1, ServerRole.Admin));
    }

    [HttpPost("{serverId}/join")]
    public async Task<IActionResult> JoinServer(int serverId)
    {
        var server = await db.Servers.FindAsync(serverId);
        if (server is null) return NotFound();

        if (await db.ServerMembers.AnyAsync(sm => sm.ServerId == serverId && sm.UserId == UserId))
            return Conflict("Already a member.");

        db.ServerMembers.Add(new ServerMember { ServerId = serverId, UserId = UserId });
        await db.SaveChangesAsync();

        var user = await db.Users.FindAsync(UserId);
        if (user is not null)
            await hub.Clients.Group($"server-{serverId}")
                .SendAsync("UserJoinedServer", new UserDto(UserId, user.Username, UserStatus.Online));

        return Ok();
    }

    [HttpDelete("{serverId}/leave")]
    public async Task<IActionResult> LeaveServer(int serverId)
    {
        var member = await db.ServerMembers.FindAsync(serverId, UserId);
        if (member is null) return NotFound();

        db.ServerMembers.Remove(member);
        await db.SaveChangesAsync();
        return Ok();
    }

    [HttpGet("{serverId}/channels")]
    public async Task<ActionResult<List<ChannelDto>>> GetChannels(int serverId)
    {
        if (!await db.ServerMembers.AnyAsync(sm => sm.ServerId == serverId && sm.UserId == UserId))
            return Forbid();

        return await db.Channels
            .Where(c => c.ServerId == serverId)
            .OrderBy(c => c.Position)
            .Select(c => new ChannelDto(c.Id, c.Name, c.Type, c.ServerId, c.Position))
            .ToListAsync();
    }

    [HttpPost("{serverId}/channels")]
    public async Task<ActionResult<ChannelDto>> CreateChannel(int serverId, CreateChannelRequest req)
    {
        var member = await db.ServerMembers.FindAsync(serverId, UserId);
        if (member is null) return Forbid();
        if (member.Role < ServerRole.Admin) return Forbid();

        var pos = await db.Channels.Where(c => c.ServerId == serverId).CountAsync();
        var channel = new Channel { Name = req.Name, Type = req.Type, ServerId = serverId, Position = pos };
        db.Channels.Add(channel);
        db.AuditLogs.Add(new AuditLog
        {
            ServerId = serverId, ActorId = UserId, ActorUsername = Username,
            Action = "ChannelCreated", Detail = $"#{req.Name} ({req.Type})"
        });
        await db.SaveChangesAsync();

        var dto = new ChannelDto(channel.Id, channel.Name, channel.Type, channel.ServerId, channel.Position);
        await hub.Clients.Group($"server-{serverId}").SendAsync("ChannelCreated", dto);
        return Ok(dto);
    }

    [HttpDelete("{serverId}/channels/{channelId}")]
    public async Task<IActionResult> DeleteChannel(int serverId, int channelId)
    {
        var member = await db.ServerMembers.FindAsync(serverId, UserId);
        if (member is null || member.Role < ServerRole.Admin) return Forbid();

        var channel = await db.Channels.FirstOrDefaultAsync(c => c.Id == channelId && c.ServerId == serverId);
        if (channel is null) return NotFound();

        db.Channels.Remove(channel);
        db.AuditLogs.Add(new AuditLog
        {
            ServerId = serverId, ActorId = UserId, ActorUsername = Username,
            Action = "ChannelDeleted", Detail = $"#{channel.Name} ({channel.Type})"
        });
        await db.SaveChangesAsync();

        await hub.Clients.Group($"server-{serverId}").SendAsync("ChannelDeleted", channelId);
        return NoContent();
    }

    [HttpGet("{serverId}/members")]
    public async Task<ActionResult<List<UserDto>>> GetMembers(int serverId)
    {
        if (!await db.ServerMembers.AnyAsync(sm => sm.ServerId == serverId && sm.UserId == UserId))
            return Forbid();

        return await db.ServerMembers
            .Where(sm => sm.ServerId == serverId)
            .Include(sm => sm.User)
            .Select(sm => new UserDto(sm.User.Id, sm.User.Username, sm.User.Status, sm.User.AvatarUrl))
            .ToListAsync();
    }

    [HttpGet("{serverId}/members/details")]
    public async Task<ActionResult<List<ServerMemberDto>>> GetMembersWithRoles(int serverId)
    {
        var caller = await db.ServerMembers.FindAsync(serverId, UserId);
        if (caller is null || caller.Role < ServerRole.Admin) return Forbid();

        return await db.ServerMembers
            .Where(sm => sm.ServerId == serverId)
            .Include(sm => sm.User)
            .Select(sm => new ServerMemberDto(sm.User.Id, sm.User.Username, sm.User.Status, sm.Role, sm.JoinedAt))
            .ToListAsync();
    }

    [HttpPut("{serverId}/members/{targetUserId}/role")]
    public async Task<IActionResult> SetMemberRole(int serverId, int targetUserId, SetRoleRequest req)
    {
        var actor = await db.ServerMembers.FindAsync(serverId, UserId);
        if (actor is null || actor.Role < ServerRole.Admin) return Forbid();

        if (targetUserId == UserId) return BadRequest("Cannot change your own role.");

        var server = await db.Servers.FindAsync(serverId);
        if (server is null) return NotFound();

        // Owner-only: only the server owner can promote to Admin or demote from Admin
        if (req.Role == ServerRole.Admin && UserId != server.OwnerId)
            return Forbid();

        var target = await db.ServerMembers.Include(sm => sm.User).FirstOrDefaultAsync(sm => sm.ServerId == serverId && sm.UserId == targetUserId);
        if (target is null) return NotFound();

        if (target.Role == ServerRole.Admin && UserId != server.OwnerId)
            return Forbid();

        var oldRole = target.Role;
        target.Role = req.Role;
        db.AuditLogs.Add(new AuditLog
        {
            ServerId = serverId, ActorId = UserId, ActorUsername = Username,
            Action = "RoleChanged", TargetId = targetUserId, TargetUsername = target.User.Username,
            Detail = $"{oldRole} → {req.Role}"
        });
        await db.SaveChangesAsync();

        await hub.Clients.Group($"server-{serverId}").SendAsync("MemberRoleChanged", targetUserId, req.Role.ToString());
        return NoContent();
    }

    // ── Invite links ──────────────────────────────────────────────────────────

    private static string GenerateCode() =>
        Convert.ToHexString(RandomNumberGenerator.GetBytes(5)).ToLowerInvariant(); // 10 hex chars

    [HttpGet("{serverId}/invite")]
    public async Task<ActionResult<ServerInviteDto>> GetInvite(int serverId)
    {
        var member = await db.ServerMembers.FindAsync(serverId, UserId);
        if (member is null || member.Role < ServerRole.Admin) return Forbid();

        var server = await db.Servers.FindAsync(serverId);
        if (server is null) return NotFound();

        if (server.InviteCode is null)
        {
            server.InviteCode = GenerateCode();
            await db.SaveChangesAsync();
        }

        var memberCount = await db.ServerMembers.CountAsync(sm => sm.ServerId == serverId);
        return Ok(new ServerInviteDto(server.InviteCode, server.Id, server.Name, memberCount));
    }

    [HttpPost("{serverId}/invite/reset")]
    public async Task<ActionResult<ServerInviteDto>> ResetInvite(int serverId)
    {
        var member = await db.ServerMembers.FindAsync(serverId, UserId);
        if (member is null || member.Role < ServerRole.Admin) return Forbid();

        var server = await db.Servers.FindAsync(serverId);
        if (server is null) return NotFound();

        server.InviteCode = GenerateCode();
        await db.SaveChangesAsync();

        var memberCount = await db.ServerMembers.CountAsync(sm => sm.ServerId == serverId);
        return Ok(new ServerInviteDto(server.InviteCode, server.Id, server.Name, memberCount));
    }

    [HttpGet("by-invite/{code}")]
    public async Task<ActionResult<ServerInviteDto>> PreviewInvite(string code)
    {
        var server = await db.Servers
            .Include(s => s.Members)
            .FirstOrDefaultAsync(s => s.InviteCode == code);
        if (server is null) return NotFound("Invalid or expired invite code.");
        return Ok(new ServerInviteDto(code, server.Id, server.Name, server.Members.Count));
    }

    [HttpPost("by-invite/{code}/join")]
    public async Task<ActionResult<ServerDto>> JoinByInvite(string code)
    {
        var server = await db.Servers
            .Include(s => s.Members)
            .FirstOrDefaultAsync(s => s.InviteCode == code);
        if (server is null) return NotFound("Invalid or expired invite code.");

        if (server.Members.Any(m => m.UserId == UserId))
            return Conflict("Already a member.");

        db.ServerMembers.Add(new ServerMember { ServerId = server.Id, UserId = UserId });
        await db.SaveChangesAsync();

        var user = await db.Users.FindAsync(UserId);
        if (user is not null)
            await hub.Clients.Group($"server-{server.Id}")
                .SendAsync("UserJoinedServer", new UserDto(UserId, user.Username, UserStatus.Online));

        var memberCount = await db.ServerMembers.CountAsync(sm => sm.ServerId == server.Id);
        return Ok(new ServerDto(server.Id, server.Name, server.Description, server.OwnerId.ToString(), memberCount, ServerRole.Member));
    }

    [HttpDelete("{serverId}/members/{targetUserId}")]
    public async Task<IActionResult> KickMember(int serverId, int targetUserId)
    {
        var actor = await db.ServerMembers.FindAsync(serverId, UserId);
        if (actor is null || actor.Role < ServerRole.Admin) return Forbid();

        if (targetUserId == UserId) return BadRequest("Cannot kick yourself.");

        var server = await db.Servers.FindAsync(serverId);
        if (server?.OwnerId == targetUserId) return BadRequest("Cannot kick the server owner.");

        var target = await db.ServerMembers.Include(sm => sm.User).FirstOrDefaultAsync(sm => sm.ServerId == serverId && sm.UserId == targetUserId);
        if (target is null) return NotFound();

        db.ServerMembers.Remove(target);
        db.AuditLogs.Add(new AuditLog
        {
            ServerId = serverId, ActorId = UserId, ActorUsername = Username,
            Action = "MemberKicked", TargetId = targetUserId, TargetUsername = target.User.Username
        });
        await db.SaveChangesAsync();

        await hub.Clients.User(targetUserId.ToString()).SendAsync("KickedFromServer", serverId);
        await hub.Clients.Group($"server-{serverId}").SendAsync("UserDisconnected", new UserDto(targetUserId, target.User.Username, UserStatus.Offline));
        return NoContent();
    }
}
