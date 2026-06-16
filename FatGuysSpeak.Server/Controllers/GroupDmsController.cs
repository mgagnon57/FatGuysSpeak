using System.Security.Claims;
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
[Authorize]
[Route("api/gdm")]
public class GroupDmsController(AppDbContext db, IHubContext<ChatHub> hub) : ControllerBase
{
    private int UserId => int.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);

    [HttpGet]
    public async Task<ActionResult<List<GroupConversationDto>>> GetConversations()
    {
        var memberOf = await db.GroupConversationMembers
            .Where(m => m.UserId == UserId)
            .Select(m => m.GroupConversationId)
            .ToListAsync();

        var convos = await db.GroupConversations
            .Where(gc => memberOf.Contains(gc.Id))
            .Include(gc => gc.Members).ThenInclude(m => m.User)
            .Include(gc => gc.Messages.OrderByDescending(msg => msg.Id).Take(1))
            .ToListAsync();

        return Ok(convos.Select(ToDto).ToList());
    }

    [HttpPost]
    public async Task<ActionResult<GroupConversationDto>> Create(CreateGroupConversationRequest req)
    {
        if (string.IsNullOrWhiteSpace(req.Name) || req.Name.Length > 100)
            return BadRequest("Name must be 1–100 characters.");
        var allIds = req.MemberUserIds.Distinct().Where(id => id != UserId).Take(9).ToList();
        if (allIds.Count < 1) return BadRequest("Group DM requires at least one other member.");
        if (allIds.Count > 9) return BadRequest("Group DM supports up to 10 members including yourself.");

        var allMembers = allIds.Append(UserId).Distinct().ToList();
        var existing = await db.Users.Where(u => allIds.Contains(u.Id)).Select(u => u.Id).ToListAsync();
        if (existing.Count != allIds.Count) return BadRequest("One or more users not found.");

        var convo = new GroupConversation { Name = req.Name.Trim(), CreatedById = UserId };
        db.GroupConversations.Add(convo);
        await db.SaveChangesAsync();

        db.GroupConversationMembers.AddRange(allMembers.Select(uid => new GroupConversationMember { GroupConversationId = convo.Id, UserId = uid }));
        await db.SaveChangesAsync();

        await db.Entry(convo).Collection(c => c.Members).Query().Include(m => m.User).LoadAsync();
        var dto = ToDto(convo);

        foreach (var uid in allMembers)
            await hub.Clients.Group($"user-{uid}").SendAsync("GroupConversationCreated", dto);

        return Ok(dto);
    }

    [HttpGet("{groupId}/messages")]
    public async Task<ActionResult<List<GroupMessageDto>>> GetMessages(int groupId, [FromQuery] int limit = 50)
    {
        if (!await IsMemberAsync(groupId)) return Forbid();

        var messages = await db.GroupMessages
            .Where(m => m.GroupConversationId == groupId)
            .Include(m => m.Author)
            .OrderByDescending(m => m.CreatedAt)
            .Take(Math.Min(limit, 100))
            .OrderBy(m => m.CreatedAt)
            .ToListAsync();

        return Ok(messages.Select(ToMsgDto).ToList());
    }

    [HttpPost("{groupId}/messages")]
    public async Task<ActionResult<GroupMessageDto>> SendMessage(int groupId, SendGroupMessageRequest req)
    {
        if (string.IsNullOrWhiteSpace(req.Content) || req.Content.Length > 2000)
            return BadRequest("Content must be 1–2000 characters.");
        if (!await IsMemberAsync(groupId)) return Forbid();

        var user = await db.Users.FindAsync(UserId);
        var msg = new GroupMessage { GroupConversationId = groupId, AuthorId = UserId, Content = req.Content.Trim() };
        db.GroupMessages.Add(msg);
        await db.SaveChangesAsync();
        msg.Author = user!;

        var dto = ToMsgDto(msg);
        var memberIds = await db.GroupConversationMembers.Where(m => m.GroupConversationId == groupId).Select(m => m.UserId).ToListAsync();
        foreach (var uid in memberIds)
            await hub.Clients.Group($"user-{uid}").SendAsync("ReceiveGroupMessage", dto);

        return Ok(dto);
    }

    [HttpDelete("{groupId}/messages/{messageId}")]
    public async Task<IActionResult> DeleteMessage(int groupId, int messageId)
    {
        if (!await IsMemberAsync(groupId)) return Forbid();
        var msg = await db.GroupMessages.FirstOrDefaultAsync(m => m.Id == messageId && m.GroupConversationId == groupId);
        if (msg is null) return NotFound();
        if (msg.AuthorId != UserId) return Forbid();

        msg.IsDeleted = true;
        await db.SaveChangesAsync();

        var memberIds = await db.GroupConversationMembers.Where(m => m.GroupConversationId == groupId).Select(m => m.UserId).ToListAsync();
        foreach (var uid in memberIds)
            await hub.Clients.Group($"user-{uid}").SendAsync("GroupMessageDeleted", groupId, messageId);

        return NoContent();
    }

    [HttpPost("{groupId}/members/{userId}")]
    public async Task<IActionResult> AddMember(int groupId, int userId)
    {
        var convo = await db.GroupConversations.FindAsync(groupId);
        if (convo is null) return NotFound();
        if (convo.CreatedById != UserId) return Forbid();

        var count = await db.GroupConversationMembers.CountAsync(m => m.GroupConversationId == groupId);
        if (count >= 10) return BadRequest("Group DM is at maximum capacity (10 members).");

        if (await db.GroupConversationMembers.AnyAsync(m => m.GroupConversationId == groupId && m.UserId == userId))
            return Conflict("User is already in this group.");

        var user = await db.Users.FindAsync(userId);
        if (user is null) return NotFound("User not found.");

        db.GroupConversationMembers.Add(new GroupConversationMember { GroupConversationId = groupId, UserId = userId });
        await db.SaveChangesAsync();

        var memberIds = await db.GroupConversationMembers.Where(m => m.GroupConversationId == groupId).Select(m => m.UserId).ToListAsync();
        foreach (var uid in memberIds)
            await hub.Clients.Group($"user-{uid}").SendAsync("GroupMemberAdded", groupId, new UserDto(user.Id, user.Username, user.Status, user.AvatarUrl));

        return NoContent();
    }

    [HttpDelete("{groupId}/leave")]
    public async Task<IActionResult> Leave(int groupId)
    {
        var membership = await db.GroupConversationMembers.FirstOrDefaultAsync(m => m.GroupConversationId == groupId && m.UserId == UserId);
        if (membership is null) return NotFound();

        db.GroupConversationMembers.Remove(membership);
        await db.SaveChangesAsync();

        var memberIds = await db.GroupConversationMembers.Where(m => m.GroupConversationId == groupId).Select(m => m.UserId).ToListAsync();
        foreach (var uid in memberIds)
            await hub.Clients.Group($"user-{uid}").SendAsync("GroupMemberLeft", groupId, UserId);

        return NoContent();
    }

    private Task<bool> IsMemberAsync(int groupId) =>
        db.GroupConversationMembers.AnyAsync(m => m.GroupConversationId == groupId && m.UserId == UserId);

    private GroupConversationDto ToDto(GroupConversation gc)
    {
        var last = gc.Messages.OrderByDescending(m => m.CreatedAt).FirstOrDefault();
        string? preview = last is null ? null : last.IsDeleted ? "(deleted)" : last.Content.Length > 60 ? last.Content[..60] + "…" : last.Content;
        var members = gc.Members.Select(m => new UserDto(m.User.Id, m.User.Username, m.User.Status, m.User.AvatarUrl)).ToList();
        return new GroupConversationDto(gc.Id, gc.Name, members, preview, last?.CreatedAt, gc.CreatedAt);
    }

    private static GroupMessageDto ToMsgDto(GroupMessage m) =>
        new(m.Id, m.Content, m.Author.Username, m.AuthorId, m.CreatedAt, m.GroupConversationId, m.IsDeleted, m.Author.AvatarUrl);
}
