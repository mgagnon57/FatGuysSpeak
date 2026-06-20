using System.Security.Claims;
using FatGuysSpeak.Server.Services;
using FatGuysSpeak.Shared;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace FatGuysSpeak.Server.Controllers;

[ApiController]
[Route("api/catchup")]
[Authorize]
public class CatchupController(BotService bot) : ControllerBase
{
    private int UserId => int.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);

    // GET /api/catchup?source=Text|Voice — PorkChop's personal recap of what the caller missed in
    // that chat source since last online (matches the tab they're viewing).
    [HttpGet]
    public async Task<ActionResult<CatchupDto>> Get([FromQuery] string source = "Text")
    {
        if (!Enum.TryParse<MessageSource>(source, ignoreCase: true, out var src))
            src = MessageSource.Text;
        return await bot.GenerateCatchupAsync(UserId, src);
    }
}
