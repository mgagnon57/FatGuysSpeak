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
[Authorize(Policy = "DashboardAdmin")]
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

    [HttpGet("users/{id}/profile")]
    public async Task<IActionResult> GetUserProfile(int id, [FromServices] OnlineTimeTracker onlineTime, [FromQuery] int? serverId = null)
    {
        if (!IsLocal) return Forbid();

        var user = await db.Users.FindAsync(id);
        if (user is null) return NotFound();

        var voice = ChatHub.VoiceChannelSnapshot;

        var sid = serverId ?? await db.Servers.OrderBy(s => s.Id).Select(s => (int?)s.Id).FirstOrDefaultAsync();
        var member = sid is null ? null
            : await db.ServerMembers.FirstOrDefaultAsync(m => m.ServerId == sid && m.UserId == id);
        var tempBan = sid is null ? null
            : await db.TempBans.Where(tb => tb.ServerId == sid && tb.UserId == id && tb.ExpiresAt > DateTime.UtcNow)
                .OrderByDescending(tb => tb.ExpiresAt).FirstOrDefaultAsync();

        var lastSession = await db.UserSessions.Where(s => s.UserId == id)
            .OrderByDescending(s => s.CreatedAt).FirstOrDefaultAsync();
        var activeSessions = await db.UserSessions.CountAsync(s => s.UserId == id && s.RevokedAt == null);

        var messageCount = await db.Messages.CountAsync(m => m.AuthorId == id);
        var topChannelId = await db.Messages.Where(m => m.AuthorId == id)
            .GroupBy(m => m.ChannelId)
            .OrderByDescending(g => g.Count())
            .Select(g => (int?)g.Key)
            .FirstOrDefaultAsync();
        string? topChannel = topChannelId is null ? null
            : await db.Channels.Where(c => c.Id == topChannelId).Select(c => c.Name).FirstOrDefaultAsync();

        var dto = new FatGuysSpeak.Shared.UserProfileAdminDto(
            user.Id, user.Username, user.Email, user.AvatarUrl, user.Bio,
            user.Status.ToString(), voice.ContainsKey(id),
            user.CreatedAt, member?.Role.ToString() ?? "—", member?.MutedUntil, tempBan?.ExpiresAt,
            lastSession?.CreatedAt, lastSession?.IpAddress, lastSession?.UserAgent,
            user.LastSeenAt, activeSessions,
            messageCount, topChannel, user.TotalOnlineSeconds + onlineTime.LiveSeconds(id));
        return Ok(dto);
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
            .Select(sm => new
            {
                UserId     = sm.User.Id,
                Username   = sm.User.Username,
                Status     = sm.User.Status,
                Role       = sm.Role,
                JoinedAt   = sm.JoinedAt,
                MutedUntil = sm.MutedUntil,
            })
            .ToListAsync();

        return Ok(members);
    }

    [HttpPut("servers/{serverId}/members/{userId}/mute")]
    public async Task<IActionResult> MuteUser(int serverId, int userId, [FromBody] FatGuysSpeak.Shared.MuteUserRequest req)
    {
        if (!IsLocal) return Forbid();
        if (req.Seconds < 0 || req.Seconds > 86400) return BadRequest("Mute duration must be 0–86400 seconds.");

        var target = await db.ServerMembers.Include(m => m.User)
            .FirstOrDefaultAsync(m => m.ServerId == serverId && m.UserId == userId);
        if (target is null) return NotFound();

        target.MutedUntil = req.Seconds == 0 ? null : DateTime.UtcNow.AddSeconds(req.Seconds);
        db.AuditLogs.Add(new FatGuysSpeak.Server.Models.AuditLog
        {
            ServerId = serverId, ActorId = 0, ActorUsername = "dashboard",
            Action = req.Seconds == 0 ? "UserUnmuted" : "UserMuted",
            TargetId = userId, TargetUsername = target.User.Username,
            Detail = req.Seconds == 0 ? null : $"{req.Seconds}s"
        });
        await db.SaveChangesAsync();

        await hub.Clients.Group($"server-{serverId}").SendAsync("UserMuted", userId, target.MutedUntil);
        return NoContent();
    }

    [HttpPut("servers/{serverId}/members/{userId}/tempban")]
    public async Task<IActionResult> TempBanUser(int serverId, int userId, [FromBody] FatGuysSpeak.Shared.TempBanRequest req)
    {
        if (!IsLocal) return Forbid();
        if (req.Seconds <= 0 || req.Seconds > 2592000) return BadRequest("Ban duration must be 1–2592000 seconds.");

        var server = await db.Servers.FindAsync(serverId);
        if (server?.OwnerId == userId) return BadRequest("Cannot ban the server owner.");

        var target = await db.ServerMembers.Include(m => m.User)
            .FirstOrDefaultAsync(m => m.ServerId == serverId && m.UserId == userId);
        if (target is null) return NotFound();

        var expiresAt = DateTime.UtcNow.AddSeconds(req.Seconds);
        db.TempBans.Add(new FatGuysSpeak.Server.Models.TempBan
        {
            ServerId = serverId, UserId = userId, ActorId = 0, ExpiresAt = expiresAt
        });
        db.ServerMembers.Remove(target);
        db.AuditLogs.Add(new FatGuysSpeak.Server.Models.AuditLog
        {
            ServerId = serverId, ActorId = 0, ActorUsername = "dashboard",
            Action = "UserTempBanned", TargetId = userId, TargetUsername = target.User.Username,
            Detail = $"{req.Seconds}s"
        });
        await db.SaveChangesAsync();

        await hub.Clients.Group($"user-{userId}").SendAsync("KickedFromServer", serverId);
        await hub.Clients.Group($"server-{serverId}").SendAsync("UserTempBanned", userId, expiresAt);
        return NoContent();
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

    [HttpGet("servers/{serverId}/wordfilter")]
    public async Task<IActionResult> GetWordFilters(int serverId)
    {
        if (!IsLocal) return Forbid();
        var filters = await db.WordFilters
            .Where(f => f.ServerId == serverId)
            .OrderBy(f => f.Pattern)
            .Select(f => new FatGuysSpeak.Shared.WordFilterDto(f.Id, f.Pattern, f.CreatedAt, f.Severity, f.CaseSensitive))
            .ToListAsync();
        return Ok(filters);
    }

    [HttpPost("servers/{serverId}/wordfilter")]
    public async Task<IActionResult> AddWordFilter(int serverId, [FromBody] FatGuysSpeak.Shared.AddWordFilterRequest req)
    {
        if (!IsLocal) return Forbid();
        var pattern = req.Pattern.Trim();
        if (string.IsNullOrEmpty(pattern) || pattern.Length > 100)
            return BadRequest("Pattern must be 1–100 characters.");
        var count = await db.WordFilters.CountAsync(f => f.ServerId == serverId);
        if (count >= 200) return BadRequest("Maximum 200 patterns.");
        if (await db.WordFilters.AnyAsync(f => f.ServerId == serverId && f.Pattern.ToLower() == pattern.ToLower()))
            return Conflict("Pattern already exists.");
        var filter = new FatGuysSpeak.Server.Models.WordFilter { ServerId = serverId, Pattern = pattern };
        db.WordFilters.Add(filter);
        await db.SaveChangesAsync();
        return Ok(new FatGuysSpeak.Shared.WordFilterDto(filter.Id, filter.Pattern, filter.CreatedAt));
    }

    [HttpDelete("servers/{serverId}/wordfilter/{filterId}")]
    public async Task<IActionResult> RemoveWordFilter(int serverId, int filterId)
    {
        if (!IsLocal) return Forbid();
        var filter = await db.WordFilters.FindAsync(filterId);
        if (filter is null || filter.ServerId != serverId) return NotFound();
        db.WordFilters.Remove(filter);
        await db.SaveChangesAsync();
        return NoContent();
    }

    [HttpPatch("servers/{serverId}/channels/{channelId}/name")]
    public async Task<IActionResult> RenameChannel(int serverId, int channelId, [FromBody] AdminRenameChannelRequest req)
    {
        if (!IsLocal) return Forbid();

        var channel = await db.Channels.FirstOrDefaultAsync(c => c.Id == channelId && c.ServerId == serverId);
        if (channel is null) return NotFound();

        var name = req.Name.Trim().ToLower().Replace(' ', '-');
        if (string.IsNullOrEmpty(name) || name.Length > 64)
            return BadRequest("Channel name must be 1–64 characters.");

        var oldName = channel.Name;
        channel.Name = name;
        db.AuditLogs.Add(new FatGuysSpeak.Server.Models.AuditLog
        {
            ServerId = serverId, ActorId = 0, ActorUsername = "dashboard",
            Action = "ChannelRenamed", Detail = $"#{oldName} → #{name}"
        });
        await db.SaveChangesAsync();

        var dto = new FatGuysSpeak.Shared.ChannelDto(channel.Id, channel.Name, channel.Type, channel.ServerId, channel.Position, channel.CategoryId, channel.SlowmodeSeconds, null, channel.Topic, channel.IsNsfw);
        await hub.Clients.Group($"server-{serverId}").SendAsync("ChannelUpdated", dto);
        return NoContent();
    }

    public record AdminRenameChannelRequest(string Name);

    [HttpDelete("servers/{serverId}/channels/{channelId}")]
    public async Task<IActionResult> DeleteChannel(int serverId, int channelId)
    {
        if (!IsLocal) return Forbid();

        var channel = await db.Channels.FirstOrDefaultAsync(c => c.Id == channelId && c.ServerId == serverId);
        if (channel is null) return NotFound();

        var lobby = await db.Channels
            .Where(c => c.ServerId == serverId && c.Id != channelId)
            .OrderBy(c => c.Position)
            .FirstOrDefaultAsync();

        // Messages are kept as a server-side log; they never resurface because channel ids
        // are never reused (see ServersController.NextChannelIdAsync).
        db.Channels.Remove(channel);
        db.AuditLogs.Add(new FatGuysSpeak.Server.Models.AuditLog
        {
            ServerId = serverId, ActorId = 0, ActorUsername = "dashboard",
            Action = "ChannelDeleted", Detail = $"#{channel.Name}"
        });
        await db.SaveChangesAsync();

        await hub.Clients.Group($"server-{serverId}").SendAsync("ChannelDeleted", channelId);

        if (lobby is not null)
        {
            var textUsers  = FatGuysSpeak.Server.Hubs.ChatHub.TextChannelSnapshot
                .Where(kv => kv.Value == channelId).Select(kv => kv.Key);
            var voiceUsers = FatGuysSpeak.Server.Hubs.ChatHub.VoiceChannelSnapshot
                .Where(kv => kv.Value == channelId).Select(kv => kv.Key);
            var affected   = textUsers.Union(voiceUsers).Distinct();
            foreach (var uid in affected)
                await hub.Clients.User(uid.ToString()).SendAsync("ForceJoinChannel", lobby.Id);
        }

        return NoContent();
    }

    [HttpPost("servers/{serverId}/channels")]
    public async Task<IActionResult> CreateChannel(int serverId, [FromBody] AdminCreateChannelRequest req)
    {
        if (!IsLocal) return Forbid();

        var server = await db.Servers.FindAsync(serverId);
        if (server is null) return NotFound("Server not found.");

        var name = req.Name.Trim().ToLower().Replace(' ', '-');
        if (string.IsNullOrEmpty(name) || name.Length > 64)
            return BadRequest("Channel name must be 1–64 characters.");

        var pos = await db.Channels.Where(c => c.ServerId == serverId).CountAsync();
        var channel = new FatGuysSpeak.Server.Models.Channel
        {
            Name = name, Type = FatGuysSpeak.Shared.ChannelType.Text, ServerId = serverId, Position = pos
        };
        // Never reuse a channel id on SQLite (see ServersController.NextChannelIdAsync).
        if (db.Database.IsSqlite())
            channel.Id = await ServersController.NextChannelIdAsync(db);
        db.Channels.Add(channel);
        db.AuditLogs.Add(new FatGuysSpeak.Server.Models.AuditLog
        {
            ServerId = serverId, ActorId = 0, ActorUsername = "dashboard",
            Action = "ChannelCreated", Detail = $"#{name}"
        });
        await db.SaveChangesAsync();

        var dto = new FatGuysSpeak.Shared.ChannelDto(channel.Id, channel.Name, channel.Type, channel.ServerId, channel.Position);
        await hub.Clients.Group($"server-{serverId}").SendAsync("ChannelCreated", dto);
        return Ok(dto);
    }

    public record AdminCreateChannelRequest(string Name);

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
