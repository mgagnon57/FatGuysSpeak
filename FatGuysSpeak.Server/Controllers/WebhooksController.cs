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
[Route("api/servers/{serverId}/webhooks")]
public class WebhooksController(AppDbContext db) : ControllerBase
{
    private int UserId => int.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);

    [HttpGet]
    public async Task<ActionResult<List<WebhookDto>>> GetWebhooks(int serverId)
    {
        var member = await db.ServerMembers.FindAsync(serverId, UserId);
        if (member is null || member.Role < ServerRole.Admin) return Forbid();

        var webhooks = await db.Webhooks.Where(w => w.ServerId == serverId).ToListAsync();
        return Ok(webhooks.Select(w => new WebhookDto(w.Id, w.Name, w.Url, w.Events, w.CreatedAt)).ToList());
    }

    [HttpPost]
    public async Task<ActionResult<WebhookDto>> CreateWebhook(int serverId, CreateWebhookRequest req)
    {
        var member = await db.ServerMembers.FindAsync(serverId, UserId);
        if (member is null || member.Role < ServerRole.Admin) return Forbid();

        if (string.IsNullOrWhiteSpace(req.Name) || req.Name.Length > 100)
            return BadRequest("Name must be 1–100 characters.");
        if (string.IsNullOrWhiteSpace(req.Url) || !Uri.TryCreate(req.Url, UriKind.Absolute, out var uri)
            || (uri.Scheme != "http" && uri.Scheme != "https"))
            return BadRequest("URL must be a valid http/https URL.");
        try
        {
            var addrs = await System.Net.Dns.GetHostAddressesAsync(uri.Host);
            if (addrs.Length == 0 || addrs.Any(Services.WebhookDeliveryService.IsPrivateOrLoopback))
                return BadRequest("URL must not target a private or loopback address.");
        }
        catch { return BadRequest("URL host could not be resolved."); }
        if (await db.Webhooks.CountAsync(w => w.ServerId == serverId) >= 20)
            return BadRequest("Maximum 20 webhooks per server.");

        var webhook = new Webhook { ServerId = serverId, Name = req.Name.Trim(), Url = req.Url.Trim(), Events = req.Events, CreatedById = UserId };
        db.Webhooks.Add(webhook);
        await db.SaveChangesAsync();
        return Ok(new WebhookDto(webhook.Id, webhook.Name, webhook.Url, webhook.Events, webhook.CreatedAt));
    }

    [HttpDelete("{webhookId}")]
    public async Task<IActionResult> DeleteWebhook(int serverId, int webhookId)
    {
        var member = await db.ServerMembers.FindAsync(serverId, UserId);
        if (member is null || member.Role < ServerRole.Admin) return Forbid();

        var webhook = await db.Webhooks.FirstOrDefaultAsync(w => w.Id == webhookId && w.ServerId == serverId);
        if (webhook is null) return NotFound();

        db.Webhooks.Remove(webhook);
        await db.SaveChangesAsync();
        return NoContent();
    }
}
