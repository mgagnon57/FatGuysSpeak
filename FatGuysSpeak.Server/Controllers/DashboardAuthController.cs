using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace FatGuysSpeak.Server.Controllers;

[ApiController]
[AllowAnonymous]
public class DashboardAuthController(IConfiguration config) : ControllerBase
{
    [HttpGet("/dashboard/login")]
    public ContentResult LoginPage([FromQuery] bool error = false)
    {
        var html = LoginHtml.Replace("{ERROR_BLOCK}",
            error ? """<p class="error">Invalid username or password.</p>""" : "");
        return Content(html, "text/html");
    }

    [Microsoft.AspNetCore.RateLimiting.EnableRateLimiting("dashboard")]
    [HttpPost("/dashboard/login")]
    public async Task<IActionResult> Login([FromForm] string username, [FromForm] string password)
    {
        var expectedUser = config["Dashboard:Username"] ?? "";
        var expectedPass = config["Dashboard:Password"] ?? "";

        if (username != expectedUser || password != expectedPass)
            return LocalRedirect("/dashboard/login?error=true");

        var claims = new List<Claim> { new(ClaimTypes.Name, "DashboardAdmin") };
        var identity = new ClaimsIdentity(claims, "Dashboard");
        await HttpContext.SignInAsync("Dashboard", new ClaimsPrincipal(identity),
            new AuthenticationProperties { IsPersistent = false });

        return LocalRedirect("/dashboard");
    }

    [HttpGet("/dashboard/logout")]
    public async Task<IActionResult> Logout()
    {
        await HttpContext.SignOutAsync("Dashboard");
        return LocalRedirect("/dashboard/login");
    }

    private const string LoginHtml = """
        <!DOCTYPE html>
        <html lang="en">
        <head>
        <meta charset="UTF-8">
        <meta name="viewport" content="width=device-width, initial-scale=1.0">
        <title>FatGuysSpeak — Dashboard Login</title>
        <style>
          * { margin: 0; padding: 0; box-sizing: border-box; }
          body {
            background: #1a1a1a; color: #d0d0d0;
            font-family: 'Segoe UI', system-ui, sans-serif;
            display: flex; align-items: center; justify-content: center; height: 100vh;
          }
          .card {
            background: #252525; border: 1px solid #2e2e2e; border-radius: 10px;
            padding: 36px 40px; width: 320px;
          }
          h1 { font-size: 15px; color: #8ab4d4; font-weight: 600; margin-bottom: 24px; letter-spacing: .3px; }
          .field { margin-bottom: 16px; }
          label { display: block; font-size: 11px; color: #666; text-transform: uppercase; letter-spacing: .8px; margin-bottom: 6px; }
          input {
            width: 100%; background: #1a1a1a; border: 1px solid #3a3a3a; border-radius: 6px;
            color: #d0d0d0; font-size: 13px; padding: 9px 12px; outline: none;
            font-family: inherit; transition: border-color .15s;
          }
          input:focus { border-color: #8ab4d4; }
          button {
            width: 100%; background: #2d5f9e; border: none; border-radius: 6px;
            color: #fff; font-size: 13px; font-family: inherit; padding: 10px;
            cursor: pointer; margin-top: 8px; transition: background .15s;
          }
          button:hover { background: #3a72b8; }
          .error {
            color: #e05555; font-size: 12px; margin-bottom: 14px; padding: 8px 12px;
            background: #2a1a1a; border: 1px solid #5a2222; border-radius: 6px;
          }
        </style>
        </head>
        <body>
        <div class="card">
          <h1>Server Dashboard</h1>
          {ERROR_BLOCK}
          <form method="post" action="/dashboard/login">
            <div class="field">
              <label>Username</label>
              <input type="text" name="username" autofocus autocomplete="username" />
            </div>
            <div class="field">
              <label>Password</label>
              <input type="password" name="password" autocomplete="current-password" />
            </div>
            <button type="submit">Sign In</button>
          </form>
        </div>
        </body>
        </html>
        """;
}
