using System.Security.Claims;
using FatGuysSpeak.Server.Data;
using FatGuysSpeak.Server.Models;
using FatGuysSpeak.Shared;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace FatGuysSpeak.Server.Controllers;

[ApiController]
[Authorize]
[Route("api/servers/{serverId}/members/{userId}/warnings")]
public class WarningsController(AppDbContext db) : ControllerBase
{
    private int ActorId => int.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);
    private string ActorUsername => User.FindFirstValue(ClaimTypes.Name)!;

    [HttpGet]
    public async Task<ActionResult<List<UserWarningDto>>> GetWarnings(int serverId, int userId)
    {
        var actorMember = await db.ServerMembers.FindAsync(serverId, ActorId);
        if (actorMember is null || actorMember.Role < ServerRole.Moderator) return Forbid();

        var warnings = await db.UserWarnings
            .Where(w => w.ServerId == serverId && w.UserId == userId)
            .OrderByDescending(w => w.CreatedAt)
            .ToListAsync();

        return Ok(warnings.Select(w => new UserWarningDto(w.Id, w.UserId, w.ActorUsername, w.Reason, w.CreatedAt)).ToList());
    }

    [HttpPost]
    public async Task<ActionResult<UserWarningDto>> AddWarning(int serverId, int userId, AddWarningRequest req)
    {
        if (string.IsNullOrWhiteSpace(req.Reason) || req.Reason.Length > 500)
            return BadRequest("Reason must be 1–500 characters.");

        var actorMember = await db.ServerMembers.FindAsync(serverId, ActorId);
        if (actorMember is null || actorMember.Role < ServerRole.Moderator) return Forbid();

        var targetMember = await db.ServerMembers.FindAsync(serverId, userId);
        if (targetMember is null) return NotFound("User is not a member of this server.");
        if (targetMember.Role >= actorMember.Role) return Forbid();

        var warning = new UserWarning
        {
            ServerId = serverId,
            UserId = userId,
            ActorId = ActorId,
            ActorUsername = ActorUsername,
            Reason = req.Reason.Trim()
        };
        db.UserWarnings.Add(warning);

        db.AuditLogs.Add(new AuditLog
        {
            ServerId = serverId,
            ActorId = ActorId,
            ActorUsername = ActorUsername,
            Action = "warn",
            TargetId = userId,
            Detail = req.Reason.Trim()
        });

        await db.SaveChangesAsync();
        return Ok(new UserWarningDto(warning.Id, warning.UserId, warning.ActorUsername, warning.Reason, warning.CreatedAt));
    }

    [HttpDelete("{warningId}")]
    public async Task<IActionResult> DeleteWarning(int serverId, int userId, int warningId)
    {
        var actorMember = await db.ServerMembers.FindAsync(serverId, ActorId);
        if (actorMember is null || actorMember.Role < ServerRole.Admin) return Forbid();

        var warning = await db.UserWarnings.FirstOrDefaultAsync(w => w.Id == warningId && w.ServerId == serverId && w.UserId == userId);
        if (warning is null) return NotFound();

        db.UserWarnings.Remove(warning);
        await db.SaveChangesAsync();
        return NoContent();
    }
}
