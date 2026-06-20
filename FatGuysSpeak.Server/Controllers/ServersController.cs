using System.Security.Claims;
using System.Security.Cryptography;
using FatGuysSpeak.Server.Data;
using FatGuysSpeak.Server.Hubs;
using FatGuysSpeak.Server.Models;
using FatGuysSpeak.Server.Services;
using FatGuysSpeak.Shared;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;

namespace FatGuysSpeak.Server.Controllers;

[ApiController]
[Route("api/[controller]")]
[Authorize]
public class ServersController(AppDbContext db, IHubContext<ChatHub> hub, WebhookDeliveryService webhooks) : ControllerBase
{
    private int UserId => int.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);
    private string Username => User.FindFirstValue(ClaimTypes.Name)!;

    [HttpGet]
    public async Task<List<ServerDto>> GetMyServers()
    {
        var memberships = await db.ServerMembers
            .Where(sm => sm.UserId == UserId)
            .Include(sm => sm.Server).ThenInclude(s => s.Members)
            .ToListAsync();

        var serverIds = memberships.ConvertAll(sm => sm.ServerId);
        var notifMap = await db.UserServerNotifs
            .Where(n => n.UserId == UserId && serverIds.Contains(n.ServerId))
            .ToDictionaryAsync(n => n.ServerId, n => n.Level);

        return memberships.Select(sm => new ServerDto(
            sm.Server.Id,
            sm.Server.Name,
            sm.Server.Description,
            sm.Server.OwnerId.ToString(),
            sm.Server.Members.Count,
            sm.Role,
            sm.Server.IconData != null,
            notifMap.TryGetValue(sm.ServerId, out var lvl) ? lvl : (NotifLevel?)null))
            .ToList();
    }

    [HttpGet("{serverId}/icon")]
    [AllowAnonymous]
    public async Task<IActionResult> GetIcon(int serverId)
    {
        var server = await db.Servers.FindAsync(serverId);
        if (server?.IconData is null) return NotFound();
        return File(server.IconData, server.IconMimeType ?? "image/png");
    }

    [HttpPut("{serverId}/icon")]
    public async Task<IActionResult> UploadIcon(int serverId, IFormFile file)
    {
        var member = await db.ServerMembers.FindAsync(serverId, UserId);
        if (member is null || member.Role < ServerRole.Admin) return Forbid();
        if (file.Length > 1024 * 1024) return BadRequest("Icon must be under 1 MB.");
        if (!file.ContentType.StartsWith("image/")) return BadRequest("File must be an image.");

        var server = await db.Servers.FindAsync(serverId);
        if (server is null) return NotFound();

        using var ms = new MemoryStream();
        await file.CopyToAsync(ms);
        server.IconData = ms.ToArray();
        server.IconMimeType = file.ContentType;
        await db.SaveChangesAsync();
        return NoContent();
    }

    [HttpDelete("{serverId}/icon")]
    public async Task<IActionResult> DeleteIcon(int serverId)
    {
        var member = await db.ServerMembers.FindAsync(serverId, UserId);
        if (member is null || member.Role < ServerRole.Admin) return Forbid();

        var server = await db.Servers.FindAsync(serverId);
        if (server is null) return NotFound();

        server.IconData = null;
        server.IconMimeType = null;
        await db.SaveChangesAsync();
        return NoContent();
    }

    [HttpPost]
    public async Task<ActionResult<ServerDto>> CreateServer(CreateServerRequest req)
    {
        var server = new GuildServer { Name = req.Name, Description = req.Description, OwnerId = UserId };
        db.Servers.Add(server);
        await db.SaveChangesAsync();

        db.ServerMembers.Add(new ServerMember { ServerId = server.Id, UserId = UserId, Role = ServerRole.Admin });
        var lobby = new Channel { Name = "lobby", Type = ChannelType.Text, ServerId = server.Id, Position = 0, IsDefault = true };
        var general = new Channel { Name = "General", Type = ChannelType.Voice, ServerId = server.Id, Position = 1 };
        // Route default channels through the same sequence as CreateChannel so the counter
        // stays authoritative — otherwise auto-increment climbs past it and the next explicit
        // id collides (see NextChannelIdAsync).
        if (db.Database.IsSqlite())
        {
            lobby.Id = await NextChannelIdAsync(db);
            general.Id = await NextChannelIdAsync(db);
        }
        db.Channels.Add(lobby);
        db.Channels.Add(general);
        await db.SaveChangesAsync();

        return Ok(new ServerDto(server.Id, server.Name, server.Description, server.OwnerId.ToString(), 1, ServerRole.Admin));
    }

    [HttpPost("{serverId}/join")]
    public async Task<IActionResult> JoinServer(int serverId)
    {
        var server = await db.Servers.FindAsync(serverId);
        if (server is null) return NotFound();

        // Only the public/default server (OwnerId 0) may be joined directly by id.
        // User-created servers are private and require an invite code (see JoinByInvite).
        if (server.OwnerId != 0) return Forbid();

        // Enforce active temp-bans so a banned user cannot rejoin.
        if (await db.TempBans.AnyAsync(tb => tb.ServerId == serverId && tb.UserId == UserId && tb.ExpiresAt > DateTime.UtcNow))
            return StatusCode(403, "You are temporarily banned from this server.");

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

        var channels = await db.Channels
            .Where(c => c.ServerId == serverId)
            .OrderBy(c => c.Position)
            .ToListAsync();

        var channelIds = channels.ConvertAll(c => c.Id);
        var notifMap = await db.UserChannelNotifs
            .Where(n => n.UserId == UserId && channelIds.Contains(n.ChannelId))
            .ToDictionaryAsync(n => n.ChannelId, n => n.Level);

        return channels.Select(c => new ChannelDto(
            c.Id, c.Name, c.Type, c.ServerId, c.Position, c.CategoryId, c.SlowmodeSeconds,
            notifMap.TryGetValue(c.Id, out var lvl) ? lvl : (NotifLevel?)null, c.Topic, c.IsNsfw, c.IsDefault))
            .ToList();
    }

    [HttpPost("{serverId}/channels")]
    public async Task<ActionResult<ChannelDto>> CreateChannel(int serverId, CreateChannelRequest req)
    {
        var member = await db.ServerMembers.FindAsync(serverId, UserId);
        if (member is null) return Forbid();
        if (member.Role < ServerRole.Admin) return Forbid();

        // Position must be one past the current MAX, not the count — using the count collides with
        // an existing channel's position after any channel has been deleted (unstable sidebar order).
        var pos = (await db.Channels.Where(c => c.ServerId == serverId).MaxAsync(c => (int?)c.Position) ?? -1) + 1;
        var channel = new Channel { Name = req.Name, Type = req.Type, ServerId = serverId, Position = pos };
        // On SQLite, force a never-before-used id so this channel can't inherit a deleted
        // channel's orphaned messages. PostgreSQL sequences never recycle, so leave it auto.
        if (db.Database.IsSqlite())
            channel.Id = await NextChannelIdAsync(db);
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
        if (channel.IsDefault) return BadRequest("The default channel can't be deleted. Rename it instead.");

        var defaultChannel = await db.Channels.FirstOrDefaultAsync(c => c.ServerId == serverId && c.IsDefault);

        // Note: the channel's messages are intentionally left in place (kept as a server-side
        // log). They will never resurface because channel ids are never reused — see
        // NextChannelIdAsync, which is what stops a new channel from inheriting old text.
        db.Channels.Remove(channel);
        db.AuditLogs.Add(new AuditLog
        {
            ServerId = serverId, ActorId = UserId, ActorUsername = Username,
            Action = "ChannelDeleted", Detail = $"#{channel.Name} ({channel.Type})"
        });
        await db.SaveChangesAsync();

        await hub.Clients.Group($"server-{serverId}").SendAsync("ChannelDeleted", channelId);

        // Bump anyone who was in the deleted channel (text or voice) into the default channel.
        if (defaultChannel is not null)
        {
            var affected = ChatHub.TextChannelSnapshot.Where(kv => kv.Value == channelId).Select(kv => kv.Key)
                .Union(ChatHub.VoiceChannelSnapshot.Where(kv => kv.Value == channelId).Select(kv => kv.Key))
                .Distinct();
            foreach (var uid in affected)
                await hub.Clients.User(uid.ToString()).SendAsync("ForceJoinChannel", defaultChannel.Id);
        }
        return NoContent();
    }

    // Hands out a channel id that has never been used before, using a persistent monotonic
    // counter that survives channel deletion (unlike the SQLite rowid, which is recycled).
    // This guarantees a freshly created channel can never collide with a deleted channel's
    // leftover content. Only needed for SQLite — PostgreSQL sequences never reuse ids.
    public static async Task<int> NextChannelIdAsync(AppDbContext db)
    {
        var seq = await db.AppSequences.FindAsync("channel");
        // High-water mark across every id ever used, including ids that linger only in old
        // messages (orphans on FK-disabled DBs). Recomputed each call so that if any other
        // insert path (e.g. auto-increment) advanced the table past our counter, we catch up
        // and never hand out a colliding id.
        int hiChannels = await db.Channels.MaxAsync(c => (int?)c.Id) ?? 0;
        int hiMessages = await db.Messages.MaxAsync(m => (int?)m.ChannelId) ?? 0;
        int hiWater = Math.Max(hiChannels, hiMessages);
        if (seq is null)
        {
            seq = new AppSequence { Name = "channel", Value = hiWater };
            db.AppSequences.Add(seq);
        }
        else if (seq.Value < hiWater)
        {
            seq.Value = hiWater;
        }
        seq.Value++;
        await db.SaveChangesAsync();
        return (int)seq.Value;
    }

    [HttpPut("{serverId}/channels/{channelId}/slowmode")]
    public async Task<IActionResult> SetSlowmode(int serverId, int channelId, SetSlowmodeRequest req)
    {
        var actor = await db.ServerMembers.FindAsync(serverId, UserId);
        if (actor is null || actor.Role < ServerRole.Admin) return Forbid();

        var channel = await db.Channels.FirstOrDefaultAsync(c => c.Id == channelId && c.ServerId == serverId);
        if (channel is null) return NotFound();

        if (req.Seconds < 0 || req.Seconds > 21600) return BadRequest("Slowmode must be 0–21600 seconds.");

        channel.SlowmodeSeconds = req.Seconds;
        db.AuditLogs.Add(new AuditLog
        {
            ServerId = serverId, ActorId = UserId, ActorUsername = Username,
            Action = "SlowmodeSet", TargetId = channelId,
            Detail = $"#{channel.Name}: {req.Seconds}s"
        });
        await db.SaveChangesAsync();

        await hub.Clients.Group($"server-{serverId}").SendAsync("ChannelSlowmodeUpdated", channelId, req.Seconds);
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
        if (caller is null) return Forbid();

        var members = await db.ServerMembers
            .Where(sm => sm.ServerId == serverId)
            .Include(sm => sm.User)
            .Select(sm => new ServerMemberDto(sm.User.Id, sm.User.Username, sm.User.Status, sm.Role, sm.JoinedAt))
            .ToListAsync();
        return Ok(members);
    }

    [HttpPut("{serverId}/members/{targetUserId}/role")]
    public async Task<IActionResult> SetMemberRole(int serverId, int targetUserId, SetRoleRequest req)
    {
        var actor = await db.ServerMembers.FindAsync(serverId, UserId);
        if (actor is null || actor.Role < ServerRole.Admin) return Forbid();

        if (targetUserId == UserId) return BadRequest("Cannot change your own role.");

        var server = await db.Servers.FindAsync(serverId);
        if (server is null) return NotFound();

        var target = await db.ServerMembers.Include(sm => sm.User).FirstOrDefaultAsync(sm => sm.ServerId == serverId && sm.UserId == targetUserId);
        if (target is null) return NotFound();

        // General no-op guard — no write, no broadcast when role doesn't change
        if (req.Role == target.Role) return BadRequest("User already has that role.");

        // Only the server owner can promote to Admin or demote an existing Admin
        if ((req.Role == ServerRole.Admin || target.Role == ServerRole.Admin) && UserId != server.OwnerId)
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

    // ── Mute / Temp-ban ───────────────────────────────────────────────────────

    [HttpPut("{serverId}/members/{targetUserId}/mute")]
    public async Task<IActionResult> MuteUser(int serverId, int targetUserId, MuteUserRequest req)
    {
        if (req.Seconds < 0 || req.Seconds > 86400) return BadRequest("Mute duration must be 0–86400 seconds.");
        var actor = await db.ServerMembers.FindAsync(serverId, UserId);
        if (actor is null || actor.Role < ServerRole.Admin) return Forbid();

        var target = await db.ServerMembers.Include(m => m.User)
            .FirstOrDefaultAsync(m => m.ServerId == serverId && m.UserId == targetUserId);
        if (target is null) return NotFound();
        if (target.Role >= actor.Role) return Forbid();

        target.MutedUntil = req.Seconds == 0 ? null : DateTime.UtcNow.AddSeconds(req.Seconds);
        db.AuditLogs.Add(new AuditLog
        {
            ServerId = serverId, ActorId = UserId, ActorUsername = Username,
            Action = req.Seconds == 0 ? "UserUnmuted" : "UserMuted",
            TargetId = targetUserId, TargetUsername = target.User.Username,
            Detail = req.Seconds == 0 ? null : $"{req.Seconds}s"
        });
        await db.SaveChangesAsync();

        await hub.Clients.Group($"server-{serverId}").SendAsync("UserMuted", targetUserId, target.MutedUntil);
        return NoContent();
    }

    [HttpPut("{serverId}/members/{targetUserId}/tempban")]
    public async Task<IActionResult> TempBanUser(int serverId, int targetUserId, TempBanRequest req)
    {
        if (req.Seconds <= 0 || req.Seconds > 2592000) return BadRequest("Ban duration must be 1–2592000 seconds.");
        var actor = await db.ServerMembers.FindAsync(serverId, UserId);
        if (actor is null || actor.Role < ServerRole.Admin) return Forbid();

        var target = await db.ServerMembers.Include(m => m.User)
            .FirstOrDefaultAsync(m => m.ServerId == serverId && m.UserId == targetUserId);
        if (target is null) return NotFound();
        if (targetUserId == UserId) return BadRequest("Cannot ban yourself.");
        if (target.Role >= actor.Role) return Forbid();

        var expiresAt = DateTime.UtcNow.AddSeconds(req.Seconds);
        db.TempBans.Add(new TempBan { ServerId = serverId, UserId = targetUserId, ActorId = UserId, ExpiresAt = expiresAt });
        db.ServerMembers.Remove(target);
        db.AuditLogs.Add(new AuditLog
        {
            ServerId = serverId, ActorId = UserId, ActorUsername = Username,
            Action = "UserTempBanned", TargetId = targetUserId, TargetUsername = target.User.Username,
            Detail = $"{req.Seconds}s"
        });
        await db.SaveChangesAsync();

        await hub.Clients.Group($"user-{targetUserId}").SendAsync("KickedFromServer", serverId);
        await hub.Clients.Group($"server-{serverId}").SendAsync("UserTempBanned", targetUserId, expiresAt);
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

    [Microsoft.AspNetCore.RateLimiting.EnableRateLimiting("auth")]
    [HttpGet("by-invite/{code}")]
    public async Task<ActionResult<ServerInviteDto>> PreviewInvite(string code)
    {
        var server = await db.Servers
            .Include(s => s.Members)
            .FirstOrDefaultAsync(s => s.InviteCode == code || s.VanityCode == code);
        if (server is null) return NotFound("Invalid or expired invite code.");
        return Ok(new ServerInviteDto(code, server.Id, server.Name, server.Members.Count));
    }

    [Microsoft.AspNetCore.RateLimiting.EnableRateLimiting("auth")]
    [HttpPost("by-invite/{code}/join")]
    public async Task<ActionResult<ServerDto>> JoinByInvite(string code)
    {
        var server = await db.Servers
            .Include(s => s.Members)
            .FirstOrDefaultAsync(s => s.InviteCode == code || s.VanityCode == code);
        if (server is null) return NotFound("Invalid or expired invite code.");

        // Enforce active temp-bans so a banned user cannot rejoin via an invite link.
        if (await db.TempBans.AnyAsync(tb => tb.ServerId == server.Id && tb.UserId == UserId && tb.ExpiresAt > DateTime.UtcNow))
            return StatusCode(403, "You are temporarily banned from this server.");

        if (server.Members.Any(m => m.UserId == UserId))
            return Conflict("Already a member.");

        db.ServerMembers.Add(new ServerMember { ServerId = server.Id, UserId = UserId });
        await db.SaveChangesAsync();

        var user = await db.Users.FindAsync(UserId);
        if (user is not null)
            await hub.Clients.Group($"server-{server.Id}")
                .SendAsync("UserJoinedServer", new UserDto(UserId, user.Username, UserStatus.Online));

        _ = DeliverMemberWebhooksAsync(server.Id, "member.joined", UserId, user?.Username ?? "");

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

    // Channel topic / NSFW
    [HttpPut("{serverId}/channels/{channelId}/topic")]
    public async Task<IActionResult> SetChannelTopic(int serverId, int channelId, SetChannelTopicRequest req)
    {
        var member = await db.ServerMembers.FindAsync(serverId, UserId);
        if (member is null || member.Role < ServerRole.Admin) return Forbid();

        var channel = await db.Channels.FirstOrDefaultAsync(c => c.Id == channelId && c.ServerId == serverId);
        if (channel is null) return NotFound();

        channel.Topic = req.Topic?.Trim().Length > 0 ? req.Topic.Trim() : null;
        if (req.IsNsfw.HasValue) channel.IsNsfw = req.IsNsfw.Value;
        await db.SaveChangesAsync();

        await hub.Clients.Group($"server-{serverId}").SendAsync("ChannelUpdated",
            new ChannelDto(channel.Id, channel.Name, channel.Type, channel.ServerId, channel.Position, channel.CategoryId, channel.SlowmodeSeconds, null, channel.Topic, channel.IsNsfw));
        return NoContent();
    }

    // Vanity invite code
    [HttpPut("{serverId}/invite/vanity")]
    public async Task<IActionResult> SetVanityCode(int serverId, SetVanityCodeRequest req)
    {
        var member = await db.ServerMembers.FindAsync(serverId, UserId);
        if (member is null || member.Role < ServerRole.Admin) return Forbid();

        if (string.IsNullOrWhiteSpace(req.Code) || req.Code.Length < 3 || req.Code.Length > 32)
            return BadRequest("Vanity code must be 3–32 characters.");
        if (!System.Text.RegularExpressions.Regex.IsMatch(req.Code, @"^[a-zA-Z0-9_-]+$"))
            return BadRequest("Vanity code may only contain letters, numbers, hyphens, and underscores.");

        var taken = await db.Servers.AnyAsync(s => s.VanityCode == req.Code && s.Id != serverId);
        if (taken) return Conflict("That vanity code is already in use.");

        var server = await db.Servers.FindAsync(serverId);
        if (server is null) return NotFound();
        server.VanityCode = req.Code;
        await db.SaveChangesAsync();
        return Ok(new { vanityCode = req.Code });
    }

    private async Task DeliverMemberWebhooksAsync(int serverId, string eventName, int userId, string username)
    {
        var serverWebhooks = await db.Webhooks.Where(w => w.ServerId == serverId).ToListAsync();
        foreach (var wh in serverWebhooks)
        {
            if (!wh.Events.Contains(eventName)) continue;
            _ = webhooks.DeliverAsync(wh.Url, eventName, new { userId, username, serverId });
        }
    }

    // @everyone mention gating
    [HttpPut("{serverId}/mention-role")]
    public async Task<IActionResult> SetMentionRole(int serverId, SetMentionRoleRequest req)
    {
        var member = await db.ServerMembers.FindAsync(serverId, UserId);
        if (member is null || member.Role < ServerRole.Admin) return Forbid();

        var server = await db.Servers.FindAsync(serverId);
        if (server is null) return NotFound();
        server.MinRoleToMentionEveryone = req.MinRole;
        await db.SaveChangesAsync();
        return NoContent();
    }
}
