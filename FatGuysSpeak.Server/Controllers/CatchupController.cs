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

    // GET /api/catchup — PorkChop's personal recap of what the caller missed since last online.
    [HttpGet]
    public async Task<ActionResult<CatchupDto>> Get() => await bot.GenerateCatchupAsync(UserId);
}
