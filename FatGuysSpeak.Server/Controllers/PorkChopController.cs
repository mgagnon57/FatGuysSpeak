using FatGuysSpeak.Server.Services;
using FatGuysSpeak.Shared;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace FatGuysSpeak.Server.Controllers;

/// <summary>
/// The ephemeral @PorkChop tab. Direct questions to PorkChop come through here and the answer is
/// returned straight to the caller — nothing is written to the database, no channel history is
/// mined, and nothing is broadcast. This is the one PorkChop ability available in Private Mode,
/// and for everyone it keeps direct Q&A out of channel chat. The client holds the conversation in
/// memory only, so it clears when the app closes.
/// </summary>
[ApiController]
[Route("api/porkchop")]
[Authorize]
public class PorkChopController(BotService bot) : ControllerBase
{
    [HttpPost("ask")]
    [EnableRateLimiting("messages")]
    public async Task<ActionResult<PorkChopAskResponse>> Ask(PorkChopAskRequest req)
    {
        if (string.IsNullOrWhiteSpace(req.Question))
            return BadRequest("Ask PorkChop something.");
        if (req.Question.Length > 2000)
            return BadRequest("Question must be 2000 characters or fewer.");

        var answer = await bot.AskEphemeralAsync(req.Question);
        return new PorkChopAskResponse(answer ?? "PorkChop isn't around right now — try again in a bit.");
    }
}
