using System.Security.Claims;
using FatGuysSpeak.Server.Data;
using FatGuysSpeak.Server.Hubs;
using FatGuysSpeak.Server.Services;
using FatGuysSpeak.Shared;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace FatGuysSpeak.Server.Controllers;

[ApiController]
[Authorize]
public class NudgeController(AppDbContext db, BotService bot, TtsService tts) : ControllerBase
{
    private int UserId => int.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);

    // POST /api/channels/{channelId}/nudge — admin-only: fire PorkChop's idle roast on demand,
    // bypassing the silence wait and cooldown (for testing / for fun). Roasts whoever's currently
    // in the voice channel; if nobody's in voice, roasts the admin who triggered it.
    [HttpPost("api/channels/{channelId}/nudge")]
    public async Task<IActionResult> Nudge(int channelId)
    {
        var channel = await db.Channels.FindAsync(channelId);
        if (channel is null) return NotFound();

        var member = await db.ServerMembers.FirstOrDefaultAsync(m => m.ServerId == channel.ServerId && m.UserId == UserId);
        if (member is null || member.Role < ServerRole.Admin) return Forbid();

        // Force a fresh alias-learning pass first, so the on-demand roast uses the latest nicknames.
        await bot.LearnAliasesAsync(channel.ServerId);

        var ids = ChatHub.VoiceParticipantIds(channelId);
        if (ids.Count == 0) ids = [UserId];   // solo test: roast the admin who pressed it

        var line = await bot.GenerateAndPostIdleNudgeAsync(channelId, ids);
        if (line is null)
            return Ok(new { posted = false, reason = "Idle nudges are disabled or PorkChop has no API key." });

        _ = tts.SpeakIntoVoiceChannelAsync(channelId, line);   // speak it too, if voice is configured
        return Ok(new { posted = true, line });
    }
}
