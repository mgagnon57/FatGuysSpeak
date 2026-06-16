using System.Security.Claims;
using FatGuysSpeak.Server.Data;
using FatGuysSpeak.Server.Models;
using FatGuysSpeak.Server.Services;
using FatGuysSpeak.Shared;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;

namespace FatGuysSpeak.Server.Controllers;

[ApiController]
[Route("api/[controller]")]
[EnableRateLimiting("auth")]
public class AuthController(AppDbContext db, TokenService tokens, SessionBlacklistService blacklist) : ControllerBase
{
    private int UserId => int.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);

    [HttpPost("register")]
    public async Task<ActionResult<AuthResponse>> Register(RegisterRequest req)
    {
        if (string.IsNullOrWhiteSpace(req.Username) || req.Username.Length > 32)
            return BadRequest("Username must be 1–32 characters.");
        if (!System.Text.RegularExpressions.Regex.IsMatch(req.Username, @"^[a-zA-Z0-9._\-]+$"))
            return BadRequest("Username may only contain letters, digits, underscores, hyphens, and periods.");
        if (string.IsNullOrWhiteSpace(req.Password) || req.Password.Length < 8)
            return BadRequest("Password must be at least 8 characters.");
        if (string.IsNullOrWhiteSpace(req.Email) || req.Email.Length > 200)
            return BadRequest("Email must be 1–200 characters.");
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

        var token = tokens.CreateToken(user);
        await RecordSessionAsync(user.Id, token);
        return Ok(new AuthResponse(token, user.Username, user.Id, user.AvatarUrl));
    }

    [HttpPost("login")]
    public async Task<ActionResult<AuthResponse>> Login(LoginRequest req)
    {
        var user = await db.Users.FirstOrDefaultAsync(u => u.Username == req.Username);
        var passwordHash = user?.PasswordHash ?? BCrypt.Net.BCrypt.HashPassword("__dummy__");
        if (user is null || !BCrypt.Net.BCrypt.Verify(req.Password, passwordHash))
            return Unauthorized("Invalid credentials.");

        var ip = HttpContext?.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        db.AuditLogs.Add(new AuditLog
        {
            ServerId = 0,
            ActorId = user.Id,
            ActorUsername = user.Username,
            Action = "login",
            Detail = ip
        });

        await AutoJoinDefaultServerAsync(user.Id);

        var token = tokens.CreateToken(user);
        await RecordSessionAsync(user.Id, token);
        await db.SaveChangesAsync();
        return Ok(new AuthResponse(token, user.Username, user.Id, user.AvatarUrl));
    }

    [HttpPost("forgot-password")]
    public async Task<IActionResult> ForgotPassword(ForgotPasswordRequest req)
    {
        var user = await db.Users.FirstOrDefaultAsync(u => u.Email == req.Email);
        if (user is not null)
        {
            // Expire previous tokens for this user
            var old = await db.PasswordResetTokens.Where(t => t.UserId == user.Id && !t.IsUsed).ToListAsync();
            foreach (var t in old) t.IsUsed = true;

            var token = new PasswordResetToken
            {
                UserId = user.Id,
                Token = Guid.NewGuid().ToString("N"),
                ExpiresAt = DateTime.UtcNow.AddHours(1)
            };
            db.PasswordResetTokens.Add(token);
            await db.SaveChangesAsync();

            // Log to console since no SMTP is configured — token value intentionally omitted from log
            Console.WriteLine($"[PASSWORD RESET] Password reset requested for user '{user.Username}' (expires {token.ExpiresAt:u})");
        }
        // Always return OK to avoid user enumeration
        return Ok(new { message = "If an account with that email exists, a reset token has been logged to the server console." });
    }

    [HttpPost("reset-password")]
    public async Task<IActionResult> ResetPassword(ResetPasswordRequest req)
    {
        if (string.IsNullOrWhiteSpace(req.NewPassword) || req.NewPassword.Length < 6)
            return BadRequest("Password must be at least 6 characters.");

        var token = await db.PasswordResetTokens
            .Include(t => t.User)
            .FirstOrDefaultAsync(t => t.Token == req.Token && !t.IsUsed && t.ExpiresAt > DateTime.UtcNow);

        if (token is null) return BadRequest("Invalid or expired reset token.");

        token.User.PasswordHash = BCrypt.Net.BCrypt.HashPassword(req.NewPassword);
        token.IsUsed = true;

        // Revoke all existing sessions for security
        var sessions = await db.UserSessions.Where(s => s.UserId == token.UserId && s.RevokedAt == null).ToListAsync();
        foreach (var s in sessions)
        {
            s.RevokedAt = DateTime.UtcNow;
            blacklist.Revoke(s.TokenHash);
        }

        await db.SaveChangesAsync();
        return Ok(new { message = "Password updated. Please log in again." });
    }

    [HttpGet("sessions")]
    [Authorize]
    public async Task<ActionResult<List<UserSessionDto>>> GetSessions()
    {
        var currentHash = GetCurrentTokenHash();
        var sessions = await db.UserSessions
            .Where(s => s.UserId == UserId && s.RevokedAt == null)
            .OrderByDescending(s => s.LastSeenAt)
            .ToListAsync();

        return Ok(sessions.Select(s => new UserSessionDto(s.Id, s.IpAddress, s.CreatedAt, s.LastSeenAt, s.TokenHash == currentHash)).ToList());
    }

    [HttpDelete("sessions")]
    [Authorize]
    public async Task<IActionResult> RevokeAllSessions()
    {
        var sessions = await db.UserSessions.Where(s => s.UserId == UserId && s.RevokedAt == null).ToListAsync();
        foreach (var s in sessions)
        {
            s.RevokedAt = DateTime.UtcNow;
            blacklist.Revoke(s.TokenHash);
        }
        await db.SaveChangesAsync();
        return NoContent();
    }

    [HttpDelete("sessions/{sessionId}")]
    [Authorize]
    public async Task<IActionResult> RevokeSession(int sessionId)
    {
        var session = await db.UserSessions.FirstOrDefaultAsync(s => s.Id == sessionId && s.UserId == UserId);
        if (session is null) return NotFound();
        session.RevokedAt = DateTime.UtcNow;
        blacklist.Revoke(session.TokenHash);
        await db.SaveChangesAsync();
        return NoContent();
    }

    private async Task RecordSessionAsync(int userId, string token)
    {
        var hash = SessionBlacklistService.HashToken(token);
        var ip = HttpContext?.Connection.RemoteIpAddress?.ToString();
        var ua = HttpContext?.Request.Headers.UserAgent.ToString() ?? "";
        db.UserSessions.Add(new UserSession { UserId = userId, TokenHash = hash, IpAddress = ip, UserAgent = ua.Length > 500 ? ua[..500] : ua });
    }

    private string? GetCurrentTokenHash()
    {
        var token = HttpContext?.Request.Headers.Authorization.ToString().Replace("Bearer ", "") ?? "";
        return string.IsNullOrEmpty(token) ? null : SessionBlacklistService.HashToken(token);
    }

    private async Task AutoJoinDefaultServerAsync(int userId)
    {
        var defaultServer = await db.Servers.OrderBy(s => s.Id).FirstOrDefaultAsync();
        if (defaultServer is null) return;
        var isBanned = await db.TempBans.AnyAsync(tb =>
            tb.ServerId == defaultServer.Id && tb.UserId == userId && tb.ExpiresAt > DateTime.UtcNow);
        if (isBanned) return;
        if (!await db.ServerMembers.AnyAsync(sm => sm.ServerId == defaultServer.Id && sm.UserId == userId))
        {
            db.ServerMembers.Add(new ServerMember { ServerId = defaultServer.Id, UserId = userId });
            await db.SaveChangesAsync();
        }
    }
}
