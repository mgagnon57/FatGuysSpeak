using System.Security.Claims;
using FatGuysSpeak.Server.Data;
using FatGuysSpeak.Server.Models;
using FatGuysSpeak.Server.Services;
using FatGuysSpeak.Shared;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;

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
        if (await db.Users.AnyAsync(u => u.Username == req.Username))
            return Conflict("Username already taken.");

        var user = new User
        {
            Username = req.Username,
            PasswordHash = BCrypt.Net.BCrypt.HashPassword(req.Password),
        };
        await using var tx = await db.Database.BeginTransactionAsync();
        db.Users.Add(user);
        await db.SaveChangesAsync();
        await AutoJoinDefaultServerAsync(user.Id);

        var token = tokens.CreateToken(user);
        await RecordSessionAsync(user.Id, token);
        await db.SaveChangesAsync();
        await tx.CommitAsync();
        return Ok(new AuthResponse(token, user.Username, user.Id, user.AvatarUrl));
    }

    [HttpPost("login")]
    public async Task<ActionResult<AuthResponse>> Login(LoginRequest req)
    {
        var user = await db.Users.FirstOrDefaultAsync(u => u.Username == req.Username);
        var hasPassword = user is not null && !string.IsNullOrEmpty(user.PasswordHash);
        // Always run a BCrypt verify (against a dummy hash when there's no password) to keep
        // login timing constant for unknown users and OAuth-only accounts.
        var passwordHash = hasPassword ? user!.PasswordHash : BCrypt.Net.BCrypt.HashPassword("__dummy__");
        var verified = BCrypt.Net.BCrypt.Verify(req.Password, passwordHash);
        if (user is null || !hasPassword || !verified)
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

    [HttpPost("external/google")]
    public async Task<ActionResult<AuthResponse>> GoogleSignIn(
        GoogleAuthRequest req, [FromServices] IGoogleTokenValidator validator)
    {
        if (string.IsNullOrWhiteSpace(req.IdToken))
            return Unauthorized("Missing Google token.");

        GoogleIdentity identity;
        try
        {
            identity = await validator.ValidateAsync(req.IdToken);
        }
        catch (InvalidOperationException)
        {
            return StatusCode(503, "Google sign-in is not configured on this server.");
        }
        catch (Google.Apis.Auth.InvalidJwtException)
        {
            return Unauthorized("Invalid Google token.");
        }

        if (!identity.EmailVerified)
            return Unauthorized("Your Google email must be verified to sign in.");

        return await ResolveGoogleIdentityAndIssueAsync(identity);
    }

    [HttpPost("external/google/exchange")]
    public async Task<ActionResult<AuthResponse>> GoogleExchange(
        GoogleCodeExchangeRequest req,
        [FromServices] IGoogleCodeExchanger exchanger,
        [FromServices] IGoogleTokenValidator validator)
    {
        if (string.IsNullOrWhiteSpace(req.Code)
            || string.IsNullOrWhiteSpace(req.CodeVerifier)
            || string.IsNullOrWhiteSpace(req.RedirectUri))
            return Unauthorized("Missing Google authorization code.");

        GoogleIdentity identity;
        try
        {
            var idToken = await exchanger.ExchangeAsync(req.Code, req.CodeVerifier, req.RedirectUri);
            identity = await validator.ValidateAsync(idToken);
        }
        catch (InvalidOperationException)
        {
            return StatusCode(503, "Google sign-in is not configured on this server.");
        }
        catch (GoogleExchangeException)
        {
            return Unauthorized("Google sign-in failed.");
        }
        catch (Google.Apis.Auth.InvalidJwtException)
        {
            return Unauthorized("Invalid Google token.");
        }

        if (!identity.EmailVerified)
            return Unauthorized("Your Google email must be verified to sign in.");

        return await ResolveGoogleIdentityAndIssueAsync(identity);
    }

    [HttpGet("external/google/config")]
    public ActionResult<GoogleConfigResponse> GoogleConfig([FromServices] IConfiguration config)
        => Ok(new GoogleConfigResponse(config["Google:ClientId"] ?? ""));

    // Resolve a validated Google identity to a local account (existing link -> email auto-link ->
    // create) and issue the normal JWT. Shared by the id-token and code-exchange endpoints.
    private async Task<ActionResult<AuthResponse>> ResolveGoogleIdentityAndIssueAsync(GoogleIdentity identity)
    {
        const string provider = "google";

        var link = await db.ExternalLogins
            .FirstOrDefaultAsync(e => e.Provider == provider && e.ProviderUserId == identity.Sub);

        User? user;
        if (link is not null)
        {
            user = await db.Users.FindAsync(link.UserId);
        }
        else
        {
            await using var tx = await db.Database.BeginTransactionAsync();
            try
            {
                user = await db.Users.FirstOrDefaultAsync(u => u.Email == identity.Email);
                if (user is null)
                {
                    var baseName = UsernameGenerator.Sanitize(identity.Name, identity.Email);
                    var username = await GenerateUniqueUsernameAsync(baseName);
                    user = new User { Username = username, Email = identity.Email, PasswordHash = "" };
                    db.Users.Add(user);
                    await db.SaveChangesAsync();
                }
                db.ExternalLogins.Add(new ExternalLogin
                {
                    UserId = user.Id,
                    Provider = provider,
                    ProviderUserId = identity.Sub,
                    Email = identity.Email
                });
                await db.SaveChangesAsync();
                await tx.CommitAsync();
            }
            catch (DbUpdateException)
            {
                await tx.RollbackAsync();
                return Conflict("Account could not be created; please try signing in again.");
            }
        }

        if (user is null) return Unauthorized("Account could not be resolved.");

        var ip = HttpContext?.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        db.AuditLogs.Add(new AuditLog
        {
            ServerId = 0,
            ActorId = user.Id,
            ActorUsername = user.Username,
            Action = "google_login",
            Detail = ip
        });

        await AutoJoinDefaultServerAsync(user.Id);
        var token = tokens.CreateToken(user);
        await RecordSessionAsync(user.Id, token);
        await db.SaveChangesAsync();
        return Ok(new AuthResponse(token, user.Username, user.Id, user.AvatarUrl));
    }

    private async Task<string> GenerateUniqueUsernameAsync(string baseName)
    {
        if (!await db.Users.AnyAsync(u => u.Username == baseName))
            return baseName;
        for (int i = 1; i < 10000; i++)
        {
            var suffix = i.ToString();
            var candidate = baseName.Length + suffix.Length > 32
                ? baseName[..(32 - suffix.Length)] + suffix
                : baseName + suffix;
            if (!await db.Users.AnyAsync(u => u.Username == candidate))
                return candidate;
        }
        return baseName[..Math.Min(baseName.Length, 24)] + Guid.NewGuid().ToString("N")[..8];
    }

    [HttpPost("forgot-password")]
    public async Task<IActionResult> ForgotPassword(ForgotPasswordRequest req)
    {
        var user = await db.Users.FirstOrDefaultAsync(u => u.Email == req.Email);
        if (user is not null && !string.IsNullOrEmpty(user.PasswordHash))
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
        if (string.IsNullOrWhiteSpace(req.NewPassword) || req.NewPassword.Length < 8)
            return BadRequest("Password must be at least 8 characters.");

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
