# Google Sign-In (Windows Client) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add "Continue with Google" to the Windows client using Google's loopback desktop OAuth flow, with the server exchanging the auth code for an ID token and reusing the merged account-resolution logic.

**Architecture:** Client runs PKCE + loopback `HttpListener` + system browser to get an auth code, posts `{code, codeVerifier, redirectUri}` to a new `POST /api/auth/external/google/exchange`; the server exchanges the code with Google (secret server-side), validates the ID token via the existing `IGoogleTokenValidator`, and runs a shared resolve-and-issue helper. Pure PKCE/URL/redirect logic lives in `FatGuysSpeak.Shared` for testability.

**Tech Stack:** ASP.NET Core 9, EF Core, `Google.Apis.Auth`, .NET MAUI (Windows), `HttpListener`, xUnit with `TestDb`.

**Spec:** `docs/superpowers/specs/2026-06-16-google-signin-client-design.md`

**Deviation from spec (intentional):** The spec describes `GoogleAuthService` under `#if WINDOWS`. Instead it is a plain cross-platform class gated at runtime (`OperatingSystem.IsWindows()` + server config), and the button is hidden on non-Windows. `HttpListener`/`Launcher` compile on all MAUI targets, so this avoids `#if` churn in DI and the view-model while remaining Windows-only at runtime. Mobile remains the separate follow-up.

---

## File Structure

- **Modify** `FatGuysSpeak.Shared/DTOs.cs` — add `GoogleCodeExchangeRequest`, `GoogleConfigResponse`.
- **Create** `FatGuysSpeak.Shared/PkceHelper.cs` — verifier / S256 challenge / state.
- **Create** `FatGuysSpeak.Shared/GoogleAuthUrlBuilder.cs` — authorization URL builder.
- **Create** `FatGuysSpeak.Shared/LoopbackRedirectParser.cs` — parse/validate the loopback redirect query.
- **Create** `FatGuysSpeak.Server/Services/GoogleCodeExchanger.cs` — `IGoogleCodeExchanger`, `GoogleCodeExchanger`, `GoogleExchangeException`.
- **Modify** `FatGuysSpeak.Server/Controllers/AuthController.cs` — extract `ResolveGoogleIdentityAndIssueAsync`; add `GoogleExchange` and `GoogleConfig` endpoints.
- **Modify** `FatGuysSpeak.Server/Program.cs` — named `google` HttpClient + `IGoogleCodeExchanger` DI.
- **Modify** `FatGuysSpeak.Server/appsettings.json` — add `Google:ClientSecret` placeholder.
- **Create** `FatGuysSpeak.Tests/Helpers/FakeGoogleCodeExchanger.cs` — test double.
- **Create** `FatGuysSpeak.Tests/Server/GoogleExchangeTests.cs` — exchange + config endpoint tests.
- **Create** `FatGuysSpeak.Tests/Server/PkceHelperTests.cs`, `GoogleAuthUrlBuilderTests.cs`, `LoopbackRedirectParserTests.cs` — Shared helper tests.
- **Modify** `FatGuysSpeak.Client/Services/ApiService.cs` — `GetGoogleConfigAsync`, `ExchangeGoogleCodeAsync`.
- **Create** `FatGuysSpeak.Client/Services/GoogleAuthService.cs` — loopback orchestration.
- **Modify** `FatGuysSpeak.Client/MauiProgram.cs` — register `GoogleAuthService`.
- **Modify** `FatGuysSpeak.Client/ViewModels/AuthViewModel.cs` — `IsGoogleAvailable`, `GoogleSignInCommand`, `CheckGoogleAvailabilityAsync`.
- **Modify** `FatGuysSpeak.Client/Pages/LoginPage.xaml` — Google button.
- **Modify** `FatGuysSpeak.Client/Pages/LoginPage.xaml.cs` — call availability check on appearing.

Build/test commands:
- Server headless build: `dotnet build FatGuysSpeak.Server --framework net9.0`
- Tests: `dotnet test FatGuysSpeak.Tests`
- Client Windows build: `dotnet build FatGuysSpeak.Client --framework net9.0-windows10.0.19041.0`

---

## Task 1: DTOs + ClientSecret config placeholder

**Files:**
- Modify: `FatGuysSpeak.Shared/DTOs.cs` (near the other auth DTOs, ~line 6)
- Modify: `FatGuysSpeak.Server/appsettings.json` (the `Google` section)

- [ ] **Step 1: Add the DTOs**

In `FatGuysSpeak.Shared/DTOs.cs`, immediately after `public record GoogleAuthRequest(string IdToken);`, add:

```csharp
public record GoogleCodeExchangeRequest(string Code, string CodeVerifier, string RedirectUri);
public record GoogleConfigResponse(string ClientId);
```

- [ ] **Step 2: Add the ClientSecret placeholder**

In `FatGuysSpeak.Server/appsettings.json`, change the `Google` section so it reads (keep the existing `ClientId` line, add `ClientSecret`):

```json
  "Google": {
    "ClientId": "",
    "ClientSecret": ""
  }
```

- [ ] **Step 3: Build**

Run: `dotnet build FatGuysSpeak.Server --framework net9.0`
Expected: Build succeeded, 0 warnings.

- [ ] **Step 4: Commit**

```bash
git add FatGuysSpeak.Shared/DTOs.cs FatGuysSpeak.Server/appsettings.json
git commit -m "Add Google code-exchange/config DTOs and ClientSecret placeholder

Co-Authored-By: Claude Opus 4.8 <noreply@anthropic.com>"
```

---

## Task 2: IGoogleCodeExchanger + implementation + DI

**Files:**
- Create: `FatGuysSpeak.Server/Services/GoogleCodeExchanger.cs`
- Modify: `FatGuysSpeak.Server/Program.cs` (DI area near `AddScoped<TokenService>()` and the `anthropic` HttpClient)

- [ ] **Step 1: Create the exchanger**

Create `FatGuysSpeak.Server/Services/GoogleCodeExchanger.cs`:

```csharp
using System.Text.Json;

namespace FatGuysSpeak.Server.Services;

/// <summary>Thrown when Google's token exchange fails (bad/expired code, no id_token, network).</summary>
public class GoogleExchangeException(string message) : Exception(message);

public interface IGoogleCodeExchanger
{
    /// <summary>
    /// Exchanges an authorization code for a Google ID token.
    /// Throws <see cref="InvalidOperationException"/> if Google sign-in is not configured,
    /// and <see cref="GoogleExchangeException"/> if the exchange fails.
    /// </summary>
    Task<string> ExchangeAsync(string code, string codeVerifier, string redirectUri);
}

public class GoogleCodeExchanger(IHttpClientFactory httpFactory, IConfiguration config) : IGoogleCodeExchanger
{
    private const string TokenEndpoint = "https://oauth2.googleapis.com/token";

    public async Task<string> ExchangeAsync(string code, string codeVerifier, string redirectUri)
    {
        var clientId = config["Google:ClientId"];
        var clientSecret = config["Google:ClientSecret"];
        if (string.IsNullOrWhiteSpace(clientId) || string.IsNullOrWhiteSpace(clientSecret))
            throw new InvalidOperationException("Google sign-in is not configured (Google:ClientId/ClientSecret missing).");

        var client = httpFactory.CreateClient("google");
        using var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["client_id"] = clientId,
            ["client_secret"] = clientSecret,
            ["code"] = code,
            ["code_verifier"] = codeVerifier,
            ["grant_type"] = "authorization_code",
            ["redirect_uri"] = redirectUri,
        });

        using var resp = await client.PostAsync(TokenEndpoint, form);
        if (!resp.IsSuccessStatusCode)
            throw new GoogleExchangeException($"Google token exchange failed: {(int)resp.StatusCode}");

        var body = await resp.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(body);
        if (!doc.RootElement.TryGetProperty("id_token", out var idTokenEl)
            || idTokenEl.GetString() is not { Length: > 0 } idToken)
            throw new GoogleExchangeException("Google token response did not contain an id_token.");

        return idToken;
    }
}
```

- [ ] **Step 2: Register the named HttpClient and the service**

In `FatGuysSpeak.Server/Program.cs`, immediately after the line
`builder.Services.AddScoped<FatGuysSpeak.Server.Services.IGoogleTokenValidator, FatGuysSpeak.Server.Services.GoogleTokenValidator>();`
add:

```csharp
builder.Services.AddHttpClient("google", c => c.Timeout = TimeSpan.FromSeconds(30));
builder.Services.AddScoped<FatGuysSpeak.Server.Services.IGoogleCodeExchanger, FatGuysSpeak.Server.Services.GoogleCodeExchanger>();
```

- [ ] **Step 3: Build**

Run: `dotnet build FatGuysSpeak.Server --framework net9.0`
Expected: Build succeeded, 0 warnings.

- [ ] **Step 4: Commit**

```bash
git add FatGuysSpeak.Server/Services/GoogleCodeExchanger.cs FatGuysSpeak.Server/Program.cs
git commit -m "Add IGoogleCodeExchanger (server-side Google token exchange) + DI

Co-Authored-By: Claude Opus 4.8 <noreply@anthropic.com>"
```

---

## Task 3: Refactor — extract shared resolve-and-issue helper

This pulls the account-resolution + JWT issuance out of the existing `GoogleSignIn` endpoint so the new exchange endpoint can reuse it. No behavior change; the existing `GoogleSignInTests` (7 tests) must stay green.

**Files:**
- Modify: `FatGuysSpeak.Server/Controllers/AuthController.cs`

- [ ] **Step 1: Replace the GoogleSignIn body and add the helper**

In `AuthController.cs`, replace the **entire** `GoogleSignIn` method (from `[HttpPost("external/google")]` through its closing brace) with the following two members:

```csharp
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
```

Note: `IGoogleTokenValidator`, `GoogleIdentity`, and `UsernameGenerator` resolve via the existing `using FatGuysSpeak.Server.Services;` at the top of the file. Leave `GenerateUniqueUsernameAsync` exactly as it is.

- [ ] **Step 2: Build**

Run: `dotnet build FatGuysSpeak.Server --framework net9.0`
Expected: Build succeeded, 0 warnings.

- [ ] **Step 3: Run the existing Google tests to confirm no behavior change**

Run: `dotnet test FatGuysSpeak.Tests --filter "FullyQualifiedName~GoogleSignInTests"`
Expected: PASS (8 tests — the 7 resolution tests plus the 503 test).

- [ ] **Step 4: Commit**

```bash
git add FatGuysSpeak.Server/Controllers/AuthController.cs
git commit -m "Extract ResolveGoogleIdentityAndIssueAsync helper from GoogleSignIn

Co-Authored-By: Claude Opus 4.8 <noreply@anthropic.com>"
```

---

## Task 4: Code-exchange endpoint + tests

**Files:**
- Create: `FatGuysSpeak.Tests/Helpers/FakeGoogleCodeExchanger.cs`
- Create: `FatGuysSpeak.Tests/Server/GoogleExchangeTests.cs`
- Modify: `FatGuysSpeak.Server/Controllers/AuthController.cs`

- [ ] **Step 1: Create the fake exchanger**

Create `FatGuysSpeak.Tests/Helpers/FakeGoogleCodeExchanger.cs`:

```csharp
using FatGuysSpeak.Server.Services;

namespace FatGuysSpeak.Tests.Helpers;

public sealed class FakeGoogleCodeExchanger : IGoogleCodeExchanger
{
    public string? IdTokenToReturn { get; set; }
    public Exception? ThrowOnExchange { get; set; }

    public Task<string> ExchangeAsync(string code, string codeVerifier, string redirectUri)
    {
        if (ThrowOnExchange is not null) throw ThrowOnExchange;
        return Task.FromResult(IdTokenToReturn
            ?? throw new InvalidOperationException("FakeGoogleCodeExchanger.IdTokenToReturn must be set."));
    }
}
```

- [ ] **Step 2: Write the failing tests**

Create `FatGuysSpeak.Tests/Server/GoogleExchangeTests.cs`:

```csharp
using FatGuysSpeak.Server.Controllers;
using FatGuysSpeak.Server.Services;
using FatGuysSpeak.Shared;
using FatGuysSpeak.Tests.Helpers;
using Microsoft.AspNetCore.Mvc;

namespace FatGuysSpeak.Tests.Server;

public class GoogleExchangeTests : IDisposable
{
    private readonly TestDb _testDb;
    private readonly AuthController _controller;
    private readonly FakeGoogleCodeExchanger _exchanger = new();
    private readonly FakeGoogleTokenValidator _validator = new();

    public GoogleExchangeTests()
    {
        _testDb = new TestDb();
        _controller = new AuthController(_testDb.Db, TestHelpers.CreateTokenService(),
            new SessionBlacklistService());
    }

    public void Dispose() => _testDb.Dispose();

    private Task<ActionResult<AuthResponse>> Exchange() =>
        _controller.GoogleExchange(
            new GoogleCodeExchangeRequest("auth-code", "verifier", "http://127.0.0.1:5000/"),
            _exchanger, _validator);

    [Fact]
    public async Task ValidCode_ExchangesValidatesAndCreatesAccount()
    {
        _exchanger.IdTokenToReturn = "fake-id-token";
        _validator.Identity = new GoogleIdentity("sub-1", "jane@gmail.com", true, "Jane Doe");

        var result = await Exchange();

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var auth = Assert.IsType<AuthResponse>(ok.Value);
        Assert.False(string.IsNullOrWhiteSpace(auth.Token));
        Assert.Equal("janedoe", auth.Username);
        Assert.True(_testDb.Db.ExternalLogins.Any(e => e.ProviderUserId == "sub-1"));
    }

    [Fact]
    public async Task ExchangeFailure_ReturnsUnauthorizedAndCreatesNothing()
    {
        _exchanger.ThrowOnExchange = new GoogleExchangeException("bad code");

        var result = await Exchange();

        Assert.IsType<UnauthorizedObjectResult>(result.Result);
        Assert.Empty(_testDb.Db.Users);
        Assert.Empty(_testDb.Db.ExternalLogins);
    }

    [Fact]
    public async Task NotConfigured_ReturnsServiceUnavailable()
    {
        _exchanger.ThrowOnExchange = new InvalidOperationException("not configured");

        var result = await Exchange();

        var status = Assert.IsType<ObjectResult>(result.Result);
        Assert.Equal(503, status.StatusCode);
    }

    [Fact]
    public async Task MissingFields_ReturnsUnauthorized()
    {
        var result = await _controller.GoogleExchange(
            new GoogleCodeExchangeRequest("", "", ""), _exchanger, _validator);

        Assert.IsType<UnauthorizedObjectResult>(result.Result);
    }
}
```

- [ ] **Step 3: Run and confirm FAIL (GoogleExchange does not exist)**

Run: `dotnet test FatGuysSpeak.Tests --filter "FullyQualifiedName~GoogleExchangeTests"`
Expected: FAIL — compile error, `AuthController` has no `GoogleExchange`.

- [ ] **Step 4: Implement the endpoint**

In `AuthController.cs`, add this method immediately after the `GoogleSignIn` method (before `ResolveGoogleIdentityAndIssueAsync`):

```csharp
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
```

- [ ] **Step 5: Run and confirm PASS (4 tests)**

Run: `dotnet test FatGuysSpeak.Tests --filter "FullyQualifiedName~GoogleExchangeTests"`
Expected: PASS (4 tests).

- [ ] **Step 6: Commit**

```bash
git add FatGuysSpeak.Tests/Helpers/FakeGoogleCodeExchanger.cs FatGuysSpeak.Tests/Server/GoogleExchangeTests.cs FatGuysSpeak.Server/Controllers/AuthController.cs
git commit -m "Add POST /api/auth/external/google/exchange endpoint

Co-Authored-By: Claude Opus 4.8 <noreply@anthropic.com>"
```

---

## Task 5: Public config endpoint + test

**Files:**
- Modify: `FatGuysSpeak.Server/Controllers/AuthController.cs`
- Modify: `FatGuysSpeak.Tests/Server/GoogleExchangeTests.cs`

- [ ] **Step 1: Write the failing tests**

In `FatGuysSpeak.Tests/Server/GoogleExchangeTests.cs`, add `using Microsoft.Extensions.Configuration;` to the usings, then add these two tests inside the class:

```csharp
    [Fact]
    public void Config_ReturnsConfiguredClientId()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Google:ClientId"] = "abc.apps.googleusercontent.com" })
            .Build();

        var result = _controller.GoogleConfig(config);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var cfg = Assert.IsType<GoogleConfigResponse>(ok.Value);
        Assert.Equal("abc.apps.googleusercontent.com", cfg.ClientId);
    }

    [Fact]
    public void Config_ReturnsEmptyWhenUnset()
    {
        var config = new ConfigurationBuilder().Build();

        var result = _controller.GoogleConfig(config);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var cfg = Assert.IsType<GoogleConfigResponse>(ok.Value);
        Assert.Equal("", cfg.ClientId);
    }
```

- [ ] **Step 2: Run and confirm FAIL (GoogleConfig does not exist)**

Run: `dotnet test FatGuysSpeak.Tests --filter "FullyQualifiedName~GoogleExchangeTests.Config"`
Expected: FAIL — compile error, no `GoogleConfig`.

- [ ] **Step 3: Implement the endpoint**

In `AuthController.cs`, add immediately after the `GoogleExchange` method:

```csharp
    [HttpGet("external/google/config")]
    public ActionResult<GoogleConfigResponse> GoogleConfig([FromServices] IConfiguration config)
        => Ok(new GoogleConfigResponse(config["Google:ClientId"] ?? ""));
```

Add `using Microsoft.Extensions.Configuration;` to the top of `AuthController.cs` if it is not already present (the `IConfiguration` type). The `AuthController` class has no `[Authorize]` attribute, so this endpoint is anonymous by default.

- [ ] **Step 4: Run and confirm PASS**

Run: `dotnet test FatGuysSpeak.Tests --filter "FullyQualifiedName~GoogleExchangeTests"`
Expected: PASS (6 tests total in the class).

- [ ] **Step 5: Commit**

```bash
git add FatGuysSpeak.Server/Controllers/AuthController.cs FatGuysSpeak.Tests/Server/GoogleExchangeTests.cs
git commit -m "Add public GET /api/auth/external/google/config endpoint

Co-Authored-By: Claude Opus 4.8 <noreply@anthropic.com>"
```

---

## Task 6: PkceHelper (Shared, TDD)

**Files:**
- Create: `FatGuysSpeak.Shared/PkceHelper.cs`
- Create: `FatGuysSpeak.Tests/Server/PkceHelperTests.cs`

- [ ] **Step 1: Write the failing tests**

Create `FatGuysSpeak.Tests/Server/PkceHelperTests.cs`:

```csharp
using FatGuysSpeak.Shared;

namespace FatGuysSpeak.Tests.Server;

public class PkceHelperTests
{
    [Fact]
    public void GenerateVerifier_IsUrlSafeAndRightLength()
    {
        var v = PkceHelper.GenerateVerifier();
        Assert.Equal(43, v.Length); // 32 bytes base64url, no padding
        Assert.Matches("^[A-Za-z0-9_-]+$", v);
    }

    [Fact]
    public void Challenge_MatchesRfc7636TestVector()
    {
        // RFC 7636 Appendix B
        const string verifier = "dBjftJeZ4CVP-mB92K27uhbUJU1p1r_wW1gFWFOEjXk";
        const string expected = "E9Melhoa2OwvFrEMTJguCHaoeK1t8URWbuGJSstw-cM";
        Assert.Equal(expected, PkceHelper.Challenge(verifier));
    }

    [Fact]
    public void GenerateState_IsNonEmptyAndDistinct()
    {
        var a = PkceHelper.GenerateState();
        var b = PkceHelper.GenerateState();
        Assert.False(string.IsNullOrWhiteSpace(a));
        Assert.NotEqual(a, b);
    }
}
```

- [ ] **Step 2: Run and confirm FAIL (PkceHelper does not exist)**

Run: `dotnet test FatGuysSpeak.Tests --filter "FullyQualifiedName~PkceHelperTests"`
Expected: FAIL — compile error.

- [ ] **Step 3: Implement**

Create `FatGuysSpeak.Shared/PkceHelper.cs`:

```csharp
using System.Security.Cryptography;
using System.Text;

namespace FatGuysSpeak.Shared;

/// <summary>PKCE (RFC 7636) helpers for the Google OAuth loopback flow.</summary>
public static class PkceHelper
{
    public static string GenerateVerifier() => Base64Url(RandomNumberGenerator.GetBytes(32));

    public static string Challenge(string verifier) =>
        Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));

    public static string GenerateState() => Base64Url(RandomNumberGenerator.GetBytes(16));

    private static string Base64Url(byte[] data) =>
        Convert.ToBase64String(data).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
```

- [ ] **Step 4: Run and confirm PASS (3 tests)**

Run: `dotnet test FatGuysSpeak.Tests --filter "FullyQualifiedName~PkceHelperTests"`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add FatGuysSpeak.Shared/PkceHelper.cs FatGuysSpeak.Tests/Server/PkceHelperTests.cs
git commit -m "Add PkceHelper (RFC 7636) in Shared

Co-Authored-By: Claude Opus 4.8 <noreply@anthropic.com>"
```

---

## Task 7: GoogleAuthUrlBuilder (Shared, TDD)

**Files:**
- Create: `FatGuysSpeak.Shared/GoogleAuthUrlBuilder.cs`
- Create: `FatGuysSpeak.Tests/Server/GoogleAuthUrlBuilderTests.cs`

- [ ] **Step 1: Write the failing tests**

Create `FatGuysSpeak.Tests/Server/GoogleAuthUrlBuilderTests.cs`:

```csharp
using FatGuysSpeak.Shared;

namespace FatGuysSpeak.Tests.Server;

public class GoogleAuthUrlBuilderTests
{
    [Fact]
    public void Build_IncludesAllRequiredParams()
    {
        var url = GoogleAuthUrlBuilder.Build(
            "client123", "http://127.0.0.1:5001/", "challengeXYZ", "stateABC");

        Assert.StartsWith("https://accounts.google.com/o/oauth2/v2/auth?", url);
        Assert.Contains("client_id=client123", url);
        Assert.Contains("redirect_uri=http%3A%2F%2F127.0.0.1%3A5001%2F", url);
        Assert.Contains("response_type=code", url);
        Assert.Contains("scope=openid%20email%20profile", url);
        Assert.Contains("code_challenge=challengeXYZ", url);
        Assert.Contains("code_challenge_method=S256", url);
        Assert.Contains("state=stateABC", url);
    }
}
```

- [ ] **Step 2: Run and confirm FAIL**

Run: `dotnet test FatGuysSpeak.Tests --filter "FullyQualifiedName~GoogleAuthUrlBuilderTests"`
Expected: FAIL — compile error.

- [ ] **Step 3: Implement**

Create `FatGuysSpeak.Shared/GoogleAuthUrlBuilder.cs`:

```csharp
namespace FatGuysSpeak.Shared;

/// <summary>Builds the Google OAuth 2.0 authorization URL for the loopback desktop flow.</summary>
public static class GoogleAuthUrlBuilder
{
    public const string AuthEndpoint = "https://accounts.google.com/o/oauth2/v2/auth";
    public const string Scope = "openid email profile";

    public static string Build(string clientId, string redirectUri, string codeChallenge, string state)
    {
        var parameters = new Dictionary<string, string>
        {
            ["client_id"] = clientId,
            ["redirect_uri"] = redirectUri,
            ["response_type"] = "code",
            ["scope"] = Scope,
            ["code_challenge"] = codeChallenge,
            ["code_challenge_method"] = "S256",
            ["state"] = state,
            ["access_type"] = "offline",
            ["prompt"] = "select_account",
        };
        var query = string.Join("&", parameters.Select(kv =>
            $"{Uri.EscapeDataString(kv.Key)}={Uri.EscapeDataString(kv.Value)}"));
        return $"{AuthEndpoint}?{query}";
    }
}
```

- [ ] **Step 4: Run and confirm PASS**

Run: `dotnet test FatGuysSpeak.Tests --filter "FullyQualifiedName~GoogleAuthUrlBuilderTests"`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add FatGuysSpeak.Shared/GoogleAuthUrlBuilder.cs FatGuysSpeak.Tests/Server/GoogleAuthUrlBuilderTests.cs
git commit -m "Add GoogleAuthUrlBuilder in Shared

Co-Authored-By: Claude Opus 4.8 <noreply@anthropic.com>"
```

---

## Task 8: LoopbackRedirectParser (Shared, TDD)

**Files:**
- Create: `FatGuysSpeak.Shared/LoopbackRedirectParser.cs`
- Create: `FatGuysSpeak.Tests/Server/LoopbackRedirectParserTests.cs`

- [ ] **Step 1: Write the failing tests**

Create `FatGuysSpeak.Tests/Server/LoopbackRedirectParserTests.cs`:

```csharp
using FatGuysSpeak.Shared;

namespace FatGuysSpeak.Tests.Server;

public class LoopbackRedirectParserTests
{
    [Fact]
    public void Parse_ValidCodeAndState_Succeeds()
    {
        var r = LoopbackRedirectParser.Parse("?code=abc123&state=xyz", "xyz");
        Assert.True(r.Success);
        Assert.Equal("abc123", r.Code);
        Assert.Null(r.Error);
    }

    [Fact]
    public void Parse_ErrorParam_Fails()
    {
        var r = LoopbackRedirectParser.Parse("?error=access_denied", "xyz");
        Assert.False(r.Success);
        Assert.Equal("access_denied", r.Error);
    }

    [Fact]
    public void Parse_StateMismatch_Fails()
    {
        var r = LoopbackRedirectParser.Parse("?code=abc&state=wrong", "xyz");
        Assert.False(r.Success);
        Assert.Equal("state_mismatch", r.Error);
    }

    [Fact]
    public void Parse_MissingCode_Fails()
    {
        var r = LoopbackRedirectParser.Parse("?state=xyz", "xyz");
        Assert.False(r.Success);
        Assert.Equal("missing_code", r.Error);
    }

    [Fact]
    public void Parse_UrlEncodedCode_IsDecoded()
    {
        var r = LoopbackRedirectParser.Parse("?code=a%2Fb%2Bc&state=xyz", "xyz");
        Assert.True(r.Success);
        Assert.Equal("a/b+c", r.Code);
    }
}
```

- [ ] **Step 2: Run and confirm FAIL**

Run: `dotnet test FatGuysSpeak.Tests --filter "FullyQualifiedName~LoopbackRedirectParserTests"`
Expected: FAIL — compile error.

- [ ] **Step 3: Implement**

Create `FatGuysSpeak.Shared/LoopbackRedirectParser.cs`:

```csharp
namespace FatGuysSpeak.Shared;

public record LoopbackResult(bool Success, string? Code, string? Error);

/// <summary>Parses and validates the query captured on the OAuth loopback redirect.</summary>
public static class LoopbackRedirectParser
{
    public static LoopbackResult Parse(string rawQuery, string expectedState)
    {
        string? code = null, state = null, error = null;
        foreach (var pair in rawQuery.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var i = pair.IndexOf('=');
            var key = i < 0 ? pair : pair[..i];
            var val = i < 0 ? "" : Uri.UnescapeDataString(pair[(i + 1)..]);
            switch (key)
            {
                case "code": code = val; break;
                case "state": state = val; break;
                case "error": error = val; break;
            }
        }

        if (error is not null) return new LoopbackResult(false, null, error);
        if (state != expectedState) return new LoopbackResult(false, null, "state_mismatch");
        if (string.IsNullOrEmpty(code)) return new LoopbackResult(false, null, "missing_code");
        return new LoopbackResult(true, code, null);
    }
}
```

- [ ] **Step 4: Run and confirm PASS (5 tests)**

Run: `dotnet test FatGuysSpeak.Tests --filter "FullyQualifiedName~LoopbackRedirectParserTests"`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add FatGuysSpeak.Shared/LoopbackRedirectParser.cs FatGuysSpeak.Tests/Server/LoopbackRedirectParserTests.cs
git commit -m "Add LoopbackRedirectParser in Shared

Co-Authored-By: Claude Opus 4.8 <noreply@anthropic.com>"
```

---

## Task 9: ApiService client methods

No unit tests (the MAUI client is not referenced by the test project); verified by the Windows build in Task 12.

**Files:**
- Modify: `FatGuysSpeak.Client/Services/ApiService.cs` (after the existing `LoginAsync`, ~line 92)

- [ ] **Step 1: Add the two methods**

In `FatGuysSpeak.Client/Services/ApiService.cs`, immediately after the `LoginAsync` method, add:

```csharp
    public async Task<GoogleConfigResponse?> GetGoogleConfigAsync()
    {
        try { return await _http.GetFromJsonAsync<GoogleConfigResponse>("api/auth/external/google/config"); }
        catch { return null; }
    }

    public async Task<AuthResponse?> ExchangeGoogleCodeAsync(GoogleCodeExchangeRequest req)
    {
        var resp = await _http.PostAsJsonAsync("api/auth/external/google/exchange", req);
        if (!resp.IsSuccessStatusCode)
            throw new Exception((await resp.Content.ReadAsStringAsync()).Trim('"'));
        return await resp.Content.ReadFromJsonAsync<AuthResponse>();
    }
```

`GoogleConfigResponse`, `GoogleCodeExchangeRequest`, and `AuthResponse` come from `FatGuysSpeak.Shared`, already imported at the top of the file.

- [ ] **Step 2: Build the Windows client**

Run: `dotnet build FatGuysSpeak.Client --framework net9.0-windows10.0.19041.0`
Expected: Build succeeded.

- [ ] **Step 3: Commit**

```bash
git add FatGuysSpeak.Client/Services/ApiService.cs
git commit -m "Add ApiService Google config + code-exchange methods

Co-Authored-By: Claude Opus 4.8 <noreply@anthropic.com>"
```

---

## Task 10: GoogleAuthService (loopback orchestration) + DI

Cross-platform compile, Windows-only at runtime. No unit tests (interactive I/O); verified by the Windows build.

**Files:**
- Create: `FatGuysSpeak.Client/Services/GoogleAuthService.cs`
- Modify: `FatGuysSpeak.Client/MauiProgram.cs`

- [ ] **Step 1: Create the service**

Create `FatGuysSpeak.Client/Services/GoogleAuthService.cs`:

```csharp
using System.Net;
using System.Net.Sockets;
using System.Text;
using FatGuysSpeak.Shared;

namespace FatGuysSpeak.Client.Services;

public record GoogleCodeResult(bool Success, string? Code, string? CodeVerifier, string? RedirectUri, string? Error);

/// <summary>
/// Runs Google's loopback desktop OAuth flow: PKCE + system browser + a one-shot local
/// HttpListener that captures the redirect. Returns the auth code for the server to exchange.
/// Windows-only at runtime (gated by the caller).
/// </summary>
public class GoogleAuthService
{
    public async Task<GoogleCodeResult> SignInAsync(string clientId, CancellationToken ct = default)
    {
        var verifier = PkceHelper.GenerateVerifier();
        var challenge = PkceHelper.Challenge(verifier);
        var state = PkceHelper.GenerateState();

        var port = GetFreeLoopbackPort();
        var redirectUri = $"http://127.0.0.1:{port}/";

        using var listener = new HttpListener();
        listener.Prefixes.Add(redirectUri);
        listener.Start();
        try
        {
            var url = GoogleAuthUrlBuilder.Build(clientId, redirectUri, challenge, state);
            await Launcher.Default.OpenAsync(url);

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(TimeSpan.FromMinutes(2));

            var contextTask = listener.GetContextAsync();
            var finished = await Task.WhenAny(contextTask, Task.Delay(Timeout.Infinite, timeoutCts.Token));
            if (finished != contextTask)
                return new GoogleCodeResult(false, null, null, null, "Google sign-in was cancelled.");

            var context = await contextTask;
            var rawQuery = context.Request.Url?.Query ?? "";

            var html = "<html><body style='font-family:sans-serif;background:#1e1f22;color:#fff;text-align:center;padding-top:48px'>"
                     + "<h2>You can close this tab and return to FatGuysSpeak.</h2></body></html>";
            var buffer = Encoding.UTF8.GetBytes(html);
            context.Response.ContentType = "text/html";
            context.Response.ContentLength64 = buffer.Length;
            await context.Response.OutputStream.WriteAsync(buffer, ct);
            context.Response.Close();

            var parsed = LoopbackRedirectParser.Parse(rawQuery, state);
            if (!parsed.Success)
            {
                var msg = parsed.Error == "access_denied"
                    ? "Google sign-in was cancelled."
                    : "Google sign-in failed.";
                return new GoogleCodeResult(false, null, null, null, msg);
            }
            return new GoogleCodeResult(true, parsed.Code, verifier, redirectUri, null);
        }
        finally
        {
            listener.Stop();
        }
    }

    private static int GetFreeLoopbackPort()
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
    }
}
```

- [ ] **Step 2: Register in DI**

In `FatGuysSpeak.Client/MauiProgram.cs`, add after `builder.Services.AddSingleton<ToastNotificationService>();`:

```csharp
        builder.Services.AddSingleton<GoogleAuthService>();
```

- [ ] **Step 3: Build the Windows client**

Run: `dotnet build FatGuysSpeak.Client --framework net9.0-windows10.0.19041.0`
Expected: Build succeeded.

- [ ] **Step 4: Commit**

```bash
git add FatGuysSpeak.Client/Services/GoogleAuthService.cs FatGuysSpeak.Client/MauiProgram.cs
git commit -m "Add GoogleAuthService (loopback desktop OAuth) + DI

Co-Authored-By: Claude Opus 4.8 <noreply@anthropic.com>"
```

---

## Task 11: AuthViewModel command + LoginPage button

No unit tests; verified by the Windows build (and manual run in Task 12).

**Files:**
- Modify: `FatGuysSpeak.Client/ViewModels/AuthViewModel.cs`
- Modify: `FatGuysSpeak.Client/Pages/LoginPage.xaml`
- Modify: `FatGuysSpeak.Client/Pages/LoginPage.xaml.cs`

- [ ] **Step 1: Update the view-model**

In `FatGuysSpeak.Client/ViewModels/AuthViewModel.cs`:

(a) Change the constructor signature to inject `GoogleAuthService`:

```csharp
public partial class AuthViewModel(ApiService api, ChatHubService hub, PttService ptt, GoogleAuthService google) : ObservableObject
```

(b) Add an observable field alongside the existing ones (e.g. after `_isLoading`):

```csharp
    [ObservableProperty] private bool _isGoogleAvailable;
```

(c) Add these two members inside the class (e.g. after the `RegisterAsync` command):

```csharp
    public async Task CheckGoogleAvailabilityAsync()
    {
        if (!OperatingSystem.IsWindows()) { IsGoogleAvailable = false; return; }
        try
        {
            api.SetServerUrl(ServerUrl);
            var cfg = await api.GetGoogleConfigAsync();
            IsGoogleAvailable = cfg is not null && !string.IsNullOrWhiteSpace(cfg.ClientId);
        }
        catch { IsGoogleAvailable = false; }
    }

    [RelayCommand]
    private async Task GoogleSignInAsync()
    {
        ErrorMessage = "";
        IsLoading = true;
        try
        {
            api.SetServerUrl(ServerUrl);
            var cfg = await api.GetGoogleConfigAsync();
            if (cfg is null || string.IsNullOrWhiteSpace(cfg.ClientId))
            {
                ErrorMessage = "Google sign-in is not available on this server.";
                return;
            }

            var result = await google.SignInAsync(cfg.ClientId);
            if (!result.Success)
            {
                ErrorMessage = result.Error ?? "Google sign-in failed.";
                return;
            }

            var auth = await api.ExchangeGoogleCodeAsync(
                new FatGuysSpeak.Shared.GoogleCodeExchangeRequest(result.Code!, result.CodeVerifier!, result.RedirectUri!));
            if (auth is null) { ErrorMessage = "Google sign-in failed."; return; }

            api.SetToken(auth.Token);
            api.SetCurrentUser(auth.UserId, auth.Username, auth.AvatarUrl);
            PersistServer(ServerUrl);
            ptt.LoadForUser(auth.UserId);
            await hub.ConnectAsync(auth.Token, api.ServerUrl);
            await Shell.Current.GoToAsync("//main");
        }
        catch (Exception ex) { ErrorMessage = ex.Message; }
        finally { IsLoading = false; }
    }
```

- [ ] **Step 2: Add the button to the login page**

In `FatGuysSpeak.Client/Pages/LoginPage.xaml`, immediately after the existing "Log In" `<Button …/>` (the one with `Command="{Binding LoginCommand}"`), add:

```xml
            <Button Text="Continue with Google" Command="{Binding GoogleSignInCommand}"
                    IsVisible="{Binding IsGoogleAvailable}"
                    BackgroundColor="#ffffff" TextColor="#1e1f22"
                    CornerRadius="4" HeightRequest="44"
                    IsEnabled="{Binding IsLoading, Converter={StaticResource InvertBoolConverter}}" />
```

- [ ] **Step 3: Trigger the availability check when the page appears**

Replace the body of `FatGuysSpeak.Client/Pages/LoginPage.xaml.cs` with:

```csharp
using FatGuysSpeak.Client.ViewModels;

namespace FatGuysSpeak.Client.Pages;

public partial class LoginPage : ContentPage
{
    public LoginPage(AuthViewModel vm)
    {
        InitializeComponent();
        BindingContext = vm;
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        if (BindingContext is AuthViewModel vm)
            await vm.CheckGoogleAvailabilityAsync();
    }

    private async void OnRegisterTapped(object sender, EventArgs e)
    {
        await Shell.Current.GoToAsync("//register");
    }
}
```

- [ ] **Step 4: Build the Windows client**

Run: `dotnet build FatGuysSpeak.Client --framework net9.0-windows10.0.19041.0`
Expected: Build succeeded. (`InvertBoolConverter` is already used by the existing Login button, so the resource exists.)

- [ ] **Step 5: Commit**

```bash
git add FatGuysSpeak.Client/ViewModels/AuthViewModel.cs FatGuysSpeak.Client/Pages/LoginPage.xaml FatGuysSpeak.Client/Pages/LoginPage.xaml.cs
git commit -m "Add Continue-with-Google button and sign-in flow to the client

Co-Authored-By: Claude Opus 4.8 <noreply@anthropic.com>"
```

---

## Task 12: Full verification

**Files:** none (verification only)

- [ ] **Step 1: Build server (both frameworks)**

Run: `dotnet build FatGuysSpeak.Server --framework net9.0`
Run: `dotnet build FatGuysSpeak.Server --framework net9.0-windows10.0.19041.0`
Expected: Build succeeded, 0 warnings, both.

- [ ] **Step 2: Build the Windows client**

Run: `dotnet build FatGuysSpeak.Client --framework net9.0-windows10.0.19041.0`
Expected: Build succeeded.

- [ ] **Step 3: Run the full test suite**

Run: `dotnet test FatGuysSpeak.Tests`
Expected: all tests pass — the prior suite plus the new server tests (6) and Shared helper tests (3 + 1 + 5 = 9).

- [ ] **Step 4: Manual smoke note (no commit)**

The interactive loopback flow (browser + HttpListener) is not unit-tested. Final confirmation is running the Windows client against a server configured with real `Google:ClientId`/`Google:ClientSecret` for a Google "Desktop app" OAuth client, clicking "Continue with Google", and verifying sign-in completes and lands on `//main`. Document this as a manual step; do not block the plan on it if Google credentials are not available in this environment (the button stays hidden when unconfigured).

---

## Self-Review Notes

- **Spec coverage:** loopback flow (Tasks 6–8, 10), server code exchange (Tasks 2, 4), shared resolve-and-issue refactor (Task 3), public config endpoint + button gating (Tasks 5, 11), ApiService (Task 9), config/secret (Tasks 1, 2), error handling (401/503/cancel/state-mismatch across Tasks 2, 4, 10, 11), testing (server + Shared helpers; interactive part manual). Mobile remains out of scope.
- **Deviation noted:** `GoogleAuthService` is cross-platform-compiled and runtime-gated rather than `#if WINDOWS`; functionally Windows-only.
- **Type consistency:** `IGoogleCodeExchanger.ExchangeAsync(code, codeVerifier, redirectUri)` and `GoogleExchangeException` are used identically in the exchanger, fake, controller, and tests. `GoogleCodeExchangeRequest(Code, CodeVerifier, RedirectUri)` / `GoogleConfigResponse(ClientId)` match across server, ApiService, and view-model. `GoogleCodeResult(Success, Code, CodeVerifier, RedirectUri, Error)` and `LoopbackResult(Success, Code, Error)` are consistent with their consumers.
