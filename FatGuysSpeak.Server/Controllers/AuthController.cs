using FatGuysSpeak.Server.Data;
using FatGuysSpeak.Server.Models;
using FatGuysSpeak.Server.Services;
using FatGuysSpeak.Shared;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;

namespace FatGuysSpeak.Server.Controllers;

[ApiController]
[Route("api/[controller]")]
[EnableRateLimiting("auth")]
public class AuthController(AppDbContext db, TokenService tokens) : ControllerBase
{
    [HttpPost("register")]
    public async Task<ActionResult<AuthResponse>> Register(RegisterRequest req)
    {
        if (await db.Users.AnyAsync(u => u.Username == req.Username))
            return Conflict("Username already taken.");
        if (await db.Users.AnyAsync(u => u.Email == req.Email))
            return Conflict("Email already in use.");

        var user = new User
        {
            Username = req.Username,
            Email = req.Email,
            PasswordHash = BCrypt.Net.BCrypt.HashPassword(req.Password),
        };
        db.Users.Add(user);
        await db.SaveChangesAsync();
        await AutoJoinDefaultServerAsync(user.Id);

        return Ok(new AuthResponse(tokens.CreateToken(user), user.Username, user.Id, user.AvatarUrl));
    }

    [HttpPost("login")]
    public async Task<ActionResult<AuthResponse>> Login(LoginRequest req)
    {
        var user = await db.Users.FirstOrDefaultAsync(u => u.Username == req.Username);
        // Perform hash verification even on miss to prevent timing-based user enumeration
        var passwordHash = user?.PasswordHash ?? BCrypt.Net.BCrypt.HashPassword("__dummy__");
        if (user is null || !BCrypt.Net.BCrypt.Verify(req.Password, passwordHash))
            return Unauthorized("Invalid credentials.");

        await AutoJoinDefaultServerAsync(user.Id);

        return Ok(new AuthResponse(tokens.CreateToken(user), user.Username, user.Id, user.AvatarUrl));
    }

    private async Task AutoJoinDefaultServerAsync(int userId)
    {
        var defaultServer = await db.Servers.OrderBy(s => s.Id).FirstOrDefaultAsync();
        if (defaultServer is null) return;
        if (!await db.ServerMembers.AnyAsync(sm => sm.ServerId == defaultServer.Id && sm.UserId == userId))
        {
            db.ServerMembers.Add(new ServerMember { ServerId = defaultServer.Id, UserId = userId });
            await db.SaveChangesAsync();
        }
    }
}
