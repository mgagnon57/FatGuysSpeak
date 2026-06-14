using FatGuysSpeak.Server.Data;
using FatGuysSpeak.Server.Hubs;
using FatGuysSpeak.Server.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;

namespace FatGuysSpeak.Server.Controllers;

[ApiController]
[Route("api/admin")]
[AllowAnonymous]
public class AdminController(AppDbContext db, IHubContext<ChatHub> hub, ServerMetricsService metrics) : ControllerBase
{
    // Restrict to localhost only — dashboard is the only consumer
    private bool IsLocal => HttpContext.Connection.RemoteIpAddress is { } ip &&
        (ip.Equals(System.Net.IPAddress.Loopback) ||
         ip.Equals(System.Net.IPAddress.IPv6Loopback) ||
         ip.ToString() == "::1");

    [HttpGet("users")]
    public async Task<IActionResult> GetUsers()
    {
        if (!IsLocal) return Forbid();

        var online = ChatHub.OnlineUserSnapshot;
        var voice  = ChatHub.VoiceChannelSnapshot;

        var users = await db.Users
            .OrderBy(u => u.Username)
            .Select(u => new
            {
                u.Id,
                u.Username,
                u.Email,
                Status    = u.Status.ToString(),
                u.CreatedAt,
                IsOnline  = online.ContainsKey(u.Id),
                VoiceChannelId = voice.ContainsKey(u.Id) ? (int?)voice[u.Id] : null,
            })
            .ToListAsync();

        return Ok(users);
    }

    [HttpPost("users/{userId}/kick-voice")]
    public async Task<IActionResult> KickFromVoice(int userId)
    {
        if (!IsLocal) return Forbid();
        await hub.Clients.User(userId.ToString()).SendAsync("KickFromVoice");
        return NoContent();
    }

    [HttpGet("messages")]
    public async Task<IActionResult> GetMessages(
        [FromQuery] int limit = 100,
        [FromQuery] string? author = null,
        [FromQuery] string? channel = null,
        [FromQuery] string? source = null)
    {
        if (!IsLocal) return Forbid();

        var query = db.Messages
            .Include(m => m.Author)
            .Include(m => m.Channel).ThenInclude(c => c.Server)
            .AsQueryable();

        if (!string.IsNullOrWhiteSpace(author))
            query = query.Where(m => m.Author.Username.ToLower().Contains(author.ToLower()));
        if (!string.IsNullOrWhiteSpace(channel))
            query = query.Where(m => m.Channel.Name.ToLower().Contains(channel.ToLower()));
        if (!string.IsNullOrWhiteSpace(source) && Enum.TryParse<FatGuysSpeak.Shared.MessageSource>(source, true, out var src))
            query = query.Where(m => m.Source == src);

        var messages = await query
            .OrderByDescending(m => m.CreatedAt)
            .Take(Math.Min(limit, 500))
            .Select(m => new
            {
                m.Id,
                m.Content,
                Author   = m.Author.Username,
                Channel  = m.Channel.Name,
                Server   = m.Channel.Server.Name,
                Source   = m.Source.ToString(),
                m.CreatedAt,
                m.IsDeleted,
            })
            .ToListAsync();

        return Ok(messages);
    }

    [HttpGet("rate-limits")]
    public IActionResult GetRateLimits()
    {
        if (!IsLocal) return Forbid();
        return Ok(metrics.GetRateLimitSnapshot());
    }

    [HttpDelete("messages/{messageId}")]
    public async Task<IActionResult> DeleteMessage(int messageId)
    {
        if (!IsLocal) return Forbid();

        var msg = await db.Messages.Include(m => m.Channel).ThenInclude(c => c.Server)
            .Include(m => m.Author)
            .FirstOrDefaultAsync(m => m.Id == messageId);
        if (msg is null) return NotFound();

        msg.IsDeleted = true;
        msg.Content = "[deleted by admin]";
        db.AuditLogs.Add(new FatGuysSpeak.Server.Models.AuditLog
        {
            ServerId = msg.Channel.ServerId,
            ActorId = 0,
            ActorUsername = "admin",
            Action = "MessageDeleted",
            TargetId = msg.AuthorId,
            TargetUsername = msg.Author.Username,
            Detail = $"Dashboard deletion: msg #{messageId} in #{msg.Channel.Name}"
        });
        await db.SaveChangesAsync();

        await hub.Clients.Group($"channel-{msg.ChannelId}").SendAsync("MessageDeleted", msg.Id, msg.ChannelId);
        return NoContent();
    }

    [HttpGet("audit")]
    public async Task<IActionResult> GetAuditLog(
        [FromQuery] int? serverId = null,
        [FromQuery] string? action = null,
        [FromQuery] int limit = 100)
    {
        if (!IsLocal) return Forbid();

        var query = db.AuditLogs.AsQueryable();
        if (serverId.HasValue) query = query.Where(a => a.ServerId == serverId.Value);
        if (!string.IsNullOrWhiteSpace(action)) query = query.Where(a => a.Action == action);

        var logs = await query
            .OrderByDescending(a => a.CreatedAt)
            .Take(Math.Min(limit, 500))
            .Select(a => new FatGuysSpeak.Shared.AuditLogDto(
                a.Id, a.ServerId, a.ActorUsername, a.Action,
                a.TargetUsername, a.Detail, a.CreatedAt))
            .ToListAsync();

        return Ok(logs);
    }

    [HttpGet("servers/{serverId}/members")]
    public async Task<IActionResult> GetServerMembers(int serverId)
    {
        if (!IsLocal) return Forbid();

        var members = await db.ServerMembers
            .Where(sm => sm.ServerId == serverId)
            .Include(sm => sm.User)
            .Select(sm => new FatGuysSpeak.Shared.ServerMemberDto(
                sm.User.Id, sm.User.Username, sm.User.Status, sm.Role, sm.JoinedAt))
            .ToListAsync();

        return Ok(members);
    }

    [HttpPut("servers/{serverId}/members/{userId}/role")]
    public async Task<IActionResult> SetMemberRole(int serverId, int userId, FatGuysSpeak.Shared.SetRoleRequest req)
    {
        if (!IsLocal) return Forbid();

        var server = await db.Servers.FindAsync(serverId);
        if (server is null) return NotFound("Server not found.");

        var target = await db.ServerMembers.Include(sm => sm.User)
            .FirstOrDefaultAsync(sm => sm.ServerId == serverId && sm.UserId == userId);
        if (target is null) return NotFound("Member not found.");

        var oldRole = target.Role;
        target.Role = req.Role;
        db.AuditLogs.Add(new FatGuysSpeak.Server.Models.AuditLog
        {
            ServerId = serverId, ActorId = 0, ActorUsername = "dashboard",
            Action = "RoleChanged", TargetId = userId, TargetUsername = target.User.Username,
            Detail = $"{oldRole} → {req.Role}"
        });
        await db.SaveChangesAsync();

        await hub.Clients.Group($"server-{serverId}").SendAsync("MemberRoleChanged", userId, req.Role.ToString());
        return NoContent();
    }

    [HttpDelete("servers/{serverId}/members/{userId}")]
    public async Task<IActionResult> KickMember(int serverId, int userId)
    {
        if (!IsLocal) return Forbid();

        var server = await db.Servers.FindAsync(serverId);
        if (server?.OwnerId == userId) return BadRequest("Cannot kick the server owner.");

        var target = await db.ServerMembers.Include(sm => sm.User)
            .FirstOrDefaultAsync(sm => sm.ServerId == serverId && sm.UserId == userId);
        if (target is null) return NotFound();

        db.ServerMembers.Remove(target);
        db.AuditLogs.Add(new FatGuysSpeak.Server.Models.AuditLog
        {
            ServerId = serverId, ActorId = 0, ActorUsername = "dashboard",
            Action = "MemberKicked", TargetId = userId, TargetUsername = target.User.Username
        });
        await db.SaveChangesAsync();

        await hub.Clients.User(userId.ToString()).SendAsync("KickedFromServer", serverId);
        await hub.Clients.Group($"server-{serverId}").SendAsync("UserDisconnected",
            new FatGuysSpeak.Shared.UserDto(userId, target.User.Username, FatGuysSpeak.Shared.UserStatus.Offline));
        return NoContent();
    }

    [HttpPut("servers/{serverId}/channels/{channelId}/permissions")]
    public async Task<IActionResult> SetChannelPermissions(int serverId, int channelId, FatGuysSpeak.Shared.SetChannelPermissionRequest req)
    {
        if (!IsLocal) return Forbid();

        var channel = await db.Channels.FirstOrDefaultAsync(c => c.Id == channelId && c.ServerId == serverId);
        if (channel is null) return NotFound();

        var perm = await db.ChannelPermissions.FindAsync(channelId);
        if (perm is null)
        {
            perm = new FatGuysSpeak.Server.Models.ChannelPermission
            {
                ChannelId = channelId,
                MinRoleToRead = req.MinRoleToRead,
                MinRoleToWrite = req.MinRoleToWrite
            };
            db.ChannelPermissions.Add(perm);
        }
        else
        {
            perm.MinRoleToRead = req.MinRoleToRead;
            perm.MinRoleToWrite = req.MinRoleToWrite;
        }

        db.AuditLogs.Add(new FatGuysSpeak.Server.Models.AuditLog
        {
            ServerId = serverId, ActorId = 0, ActorUsername = "dashboard",
            Action = "ChannelPermissionsChanged", TargetId = channelId,
            Detail = $"#{channel.Name}: read={req.MinRoleToRead}, write={req.MinRoleToWrite}"
        });
        await db.SaveChangesAsync();
        return NoContent();
    }

    [HttpGet("servers")]
    public async Task<IActionResult> GetServers()
    {
        if (!IsLocal) return Forbid();

        var servers = await db.Servers
            .Select(s => new { s.Id, s.Name, MemberCount = s.Members.Count })
            .ToListAsync();

        return Ok(servers);
    }

    [HttpGet("channels")]
    public async Task<IActionResult> GetChannelsWithPermissions([FromQuery] int? serverId = null)
    {
        if (!IsLocal) return Forbid();

        var query = db.Channels.Include(c => c.Server).AsQueryable();
        if (serverId.HasValue) query = query.Where(c => c.ServerId == serverId.Value);

        var channels = await query
            .OrderBy(c => c.ServerId).ThenBy(c => c.Position)
            .Select(c => new { c.Id, c.Name, c.Type, c.ServerId, ServerName = c.Server.Name, c.Position })
            .ToListAsync();

        var channelIds = channels.Select(c => c.Id).ToList();
        var perms = await db.ChannelPermissions
            .Where(p => channelIds.Contains(p.ChannelId))
            .ToDictionaryAsync(p => p.ChannelId);

        return Ok(channels.Select(c =>
        {
            perms.TryGetValue(c.Id, out var perm);
            return new
            {
                c.Id, c.Name, c.Type, c.ServerId, c.ServerName,
                MinRoleToRead  = (perm?.MinRoleToRead  ?? FatGuysSpeak.Shared.ServerRole.Member).ToString(),
                MinRoleToWrite = (perm?.MinRoleToWrite ?? FatGuysSpeak.Shared.ServerRole.Member).ToString(),
            };
        }));
    }
}
