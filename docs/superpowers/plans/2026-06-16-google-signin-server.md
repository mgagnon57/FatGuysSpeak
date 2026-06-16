# Google Sign-In (Server-Side) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Let users authenticate with Google by posting a Google-issued ID token to a new server endpoint that validates it, resolves or creates a local account, and returns the existing JWT `AuthResponse`.

**Architecture:** Client-driven OAuth (client obtains the ID token; server only validates it). A new `POST /api/auth/external/google` endpoint validates the token via Google's official library behind an injectable `IGoogleTokenValidator`, then resolves the account by Google `sub` → verified email (auto-link) → new account. Account linkage lives in a new `ExternalLogins` table. OAuth-only accounts store an empty `PasswordHash` and are blocked from password login / reset.

**Tech Stack:** ASP.NET Core 9, EF Core (SQLite local / PostgreSQL on Railway, no EF migrations — `EnsureCreated` + raw `CREATE TABLE IF NOT EXISTS`), `Google.Apis.Auth`, xUnit with in-memory SQLite (`TestDb`).

**Spec:** `docs/superpowers/specs/2026-06-16-google-signin-server-design.md`

**Deviation from spec (intentional):** The spec said make `User.PasswordHash` nullable. Instead we keep it non-nullable and use empty string `""` as the "no password" sentinel. Reason: the production column is already `NOT NULL` and SQLite cannot drop that without a table rebuild; the empty-string approach needs no schema change and is equivalent. The login guard also becomes mandatory (not just defensive) because `BCrypt.Verify(pw, "")` throws.

---

## File Structure

- **Create** `FatGuysSpeak.Server/Services/GoogleTokenValidator.cs` — `IGoogleTokenValidator`, the `GoogleIdentity` record, and the real implementation wrapping `GoogleJsonWebSignature.ValidateAsync`.
- **Create** `FatGuysSpeak.Server/Services/UsernameGenerator.cs` — pure `Sanitize(name, email)` helper producing a valid base username.
- **Modify** `FatGuysSpeak.Server/Models/Entities.cs` — add `ExternalLogin` entity.
- **Modify** `FatGuysSpeak.Server/Data/AppDbContext.cs` — register `ExternalLogins` DbSet + unique index.
- **Modify** `FatGuysSpeak.Server/Controllers/AuthController.cs` — new `GoogleSignIn` endpoint + `GenerateUniqueUsernameAsync` helper; guard `Login` and `ForgotPassword` against password-less accounts.
- **Modify** `FatGuysSpeak.Server/Controllers/UsersController.cs` — new `PUT me/username` endpoint.
- **Modify** `FatGuysSpeak.Shared/Dtos.cs` — add `GoogleAuthRequest` and `UpdateUsernameRequest` records.
- **Modify** `FatGuysSpeak.Server/Program.cs` — register `IGoogleTokenValidator`; add `ExternalLogins` raw `CREATE TABLE`/index for both DB branches; read `Google:ClientId`.
- **Modify** `FatGuysSpeak.Server/FatGuysSpeak.Server.csproj` — add `Google.Apis.Auth` package.
- **Modify** `FatGuysSpeak.Server/appsettings.Development.json` — add empty `Google:ClientId` placeholder.
- **Create** `FatGuysSpeak.Tests/Helpers/FakeGoogleTokenValidator.cs` — test double.
- **Create** `FatGuysSpeak.Tests/Server/UsernameGeneratorTests.cs` — sanitize unit tests.
- **Create** `FatGuysSpeak.Tests/Server/GoogleSignInTests.cs` — endpoint tests.
- **Modify** `FatGuysSpeak.Tests/Server/AuthControllerTests.cs` — password-less login/forgot guard tests.
- **Modify** `FatGuysSpeak.Tests/Server/UsersControllerTests.cs` — username rename tests.

Build commands used throughout:
- Headless build: `dotnet build FatGuysSpeak.Server --framework net9.0`
- Tests: `dotnet test FatGuysSpeak.Tests`

---

## Task 1: Add Google.Apis.Auth package and config placeholder

**Files:**
- Modify: `FatGuysSpeak.Server/FatGuysSpeak.Server.csproj`
- Modify: `FatGuysSpeak.Server/appsettings.Development.json`

- [ ] **Step 1: Add the package reference**

In `FatGuysSpeak.Server.csproj`, inside the first unconditional `<ItemGroup>` (the one with `BCrypt.Net-Next`), add:

```xml
    <PackageReference Include="Google.Apis.Auth" Version="1.68.0" />
```

- [ ] **Step 2: Restore to confirm the package resolves**

Run: `dotnet restore FatGuysSpeak.Server`
Expected: restore completes with no errors (package downloaded).

- [ ] **Step 3: Add the config placeholder**

In `FatGuysSpeak.Server/appsettings.Development.json`, add a top-level `Google` section with an empty client id (do not commit a real value):

```json
  "Google": {
    "ClientId": ""
  }
```

Place it as a sibling of the existing `Jwt` section. Mind JSON commas.

- [ ] **Step 4: Build to confirm nothing broke**

Run: `dotnet build FatGuysSpeak.Server --framework net9.0`
Expected: Build succeeded.

- [ ] **Step 5: Commit**

```bash
git add FatGuysSpeak.Server/FatGuysSpeak.Server.csproj FatGuysSpeak.Server/appsettings.Development.json
git commit -m "Add Google.Apis.Auth package and Google:ClientId config placeholder"
```

---

## Task 2: ExternalLogin entity + DbContext registration

**Files:**
- Modify: `FatGuysSpeak.Server/Models/Entities.cs`
- Modify: `FatGuysSpeak.Server/Data/AppDbContext.cs`

- [ ] **Step 1: Add the ExternalLogin entity**

In `FatGuysSpeak.Server/Models/Entities.cs`, add (after the `User` class, before `GuildServer`):

```csharp
public class ExternalLogin
{
    public int Id { get; set; }
    public int UserId { get; set; }
    public string Provider { get; set; } = "";
    public string ProviderUserId { get; set; } = "";
    public string Email { get; set; } = "";
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
```

- [ ] **Step 2: Register the DbSet**

In `FatGuysSpeak.Server/Data/AppDbContext.cs`, add to the DbSet declarations (after `AppSequences`):

```csharp
    public DbSet<ExternalLogin> ExternalLogins => Set<ExternalLogin>();
```

- [ ] **Step 3: Add the unique index**

In the same file, in `OnModelCreating`, add (after the `AppSequence` key config):

```csharp
        b.Entity<ExternalLogin>()
            .HasIndex(e => new { e.Provider, e.ProviderUserId }).IsUnique();
```

- [ ] **Step 4: Build to confirm the model compiles**

Run: `dotnet build FatGuysSpeak.Server --framework net9.0`
Expected: Build succeeded. (Fresh DBs and the test `TestDb` will now create the `ExternalLogins` table via `EnsureCreated`; the production raw-SQL migration is added in Task 8.)

- [ ] **Step 5: Commit**

```bash
git add FatGuysSpeak.Server/Models/Entities.cs FatGuysSpeak.Server/Data/AppDbContext.cs
git commit -m "Add ExternalLogins entity and unique index on (Provider, ProviderUserId)"
```

---

## Task 3: Guard password login/reset against password-less accounts

OAuth-only users will have `PasswordHash == ""`. `BCrypt.Verify(pw, "")` throws, so the guard is required, not just defensive.

**Files:**
- Modify: `FatGuysSpeak.Server/Controllers/AuthController.cs:54-78` (Login), `:80-104` (ForgotPassword)
- Test: `FatGuysSpeak.Tests/Server/AuthControllerTests.cs`

- [ ] **Step 1: Write the failing tests**

In `FatGuysSpeak.Tests/Server/AuthControllerTests.cs`, add inside the class (before the closing brace):

```csharp
    [Fact]
    public async Task Login_PasswordlessAccount_ReturnsUnauthorized()
    {
        var user = new User { Username = "googleuser", Email = "g@test.com", PasswordHash = "" };
        _testDb.Db.Users.Add(user);
        await _testDb.Db.SaveChangesAsync();

        var result = await _controller.Login(new LoginRequest("googleuser", "anything"));

        var unauth = Assert.IsType<UnauthorizedObjectResult>(result.Result);
        Assert.Equal("Invalid credentials.", unauth.Value);
    }

    [Fact]
    public async Task ForgotPassword_PasswordlessAccount_CreatesNoResetToken()
    {
        var user = new User { Username = "googleuser", Email = "g@test.com", PasswordHash = "" };
        _testDb.Db.Users.Add(user);
        await _testDb.Db.SaveChangesAsync();

        await _controller.ForgotPassword(new ForgotPasswordRequest("g@test.com"));

        Assert.False(_testDb.Db.PasswordResetTokens.Any(t => t.UserId == user.Id));
    }
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test FatGuysSpeak.Tests --filter "FullyQualifiedName~AuthControllerTests.Login_PasswordlessAccount_ReturnsUnauthorized|FullyQualifiedName~AuthControllerTests.ForgotPassword_PasswordlessAccount_CreatesNoResetToken"`
Expected: FAIL — `Login_Passwordless...` throws a BCrypt `SaltParseException` (empty hash); `ForgotPassword_Passwordless...` fails because a token IS created.

- [ ] **Step 3: Guard the Login method**

In `AuthController.cs`, replace the opening of `Login` (the first three lines after the method signature, currently looking up the user, computing `passwordHash`, and the `if (user is null || !BCrypt...)` check) with:

```csharp
        var user = await db.Users.FirstOrDefaultAsync(u => u.Username == req.Username);
        var hasPassword = user is not null && !string.IsNullOrEmpty(user.PasswordHash);
        var passwordHash = hasPassword ? user!.PasswordHash : BCrypt.Net.BCrypt.HashPassword("__dummy__");
        if (!hasPassword || !BCrypt.Net.BCrypt.Verify(req.Password, passwordHash))
            return Unauthorized("Invalid credentials.");
```

This runs BCrypt in all branches (constant-ish timing) and treats both missing user and password-less account as invalid.

- [ ] **Step 4: Guard the ForgotPassword method**

In `AuthController.cs`, change the `ForgotPassword` condition from `if (user is not null)` to:

```csharp
        if (user is not null && !string.IsNullOrEmpty(user.PasswordHash))
```

- [ ] **Step 5: Run tests to verify they pass**

Run: `dotnet test FatGuysSpeak.Tests --filter "FullyQualifiedName~AuthControllerTests"`
Expected: PASS (all AuthController tests, including the two new ones and the existing login/register tests).

- [ ] **Step 6: Commit**

```bash
git add FatGuysSpeak.Server/Controllers/AuthController.cs FatGuysSpeak.Tests/Server/AuthControllerTests.cs
git commit -m "Block password login and reset for password-less (OAuth-only) accounts"
```

---

## Task 4: Username sanitize helper (pure logic, TDD)

**Files:**
- Create: `FatGuysSpeak.Server/Services/UsernameGenerator.cs`
- Test: `FatGuysSpeak.Tests/Server/UsernameGeneratorTests.cs`

- [ ] **Step 1: Write the failing tests**

Create `FatGuysSpeak.Tests/Server/UsernameGeneratorTests.cs`:

```csharp
using FatGuysSpeak.Server.Services;

namespace FatGuysSpeak.Tests.Server;

public class UsernameGeneratorTests
{
    [Fact]
    public void Sanitize_UsesNameLowercasedAndStripped()
    {
        Assert.Equal("johnsmith", UsernameGenerator.Sanitize("John Smith", "j@x.com"));
    }

    [Fact]
    public void Sanitize_KeepsAllowedPunctuation()
    {
        Assert.Equal("john.s_mith-1", UsernameGenerator.Sanitize("John.S_mith-1", "j@x.com"));
    }

    [Fact]
    public void Sanitize_FallsBackToEmailLocalPartWhenNameBlank()
    {
        Assert.Equal("jsmith", UsernameGenerator.Sanitize("   ", "jsmith@x.com"));
    }

    [Fact]
    public void Sanitize_TrimsLeadingTrailingSeparators()
    {
        Assert.Equal("bob", UsernameGenerator.Sanitize("...bob...", "b@x.com"));
    }

    [Fact]
    public void Sanitize_ClampsTo32Chars()
    {
        var result = UsernameGenerator.Sanitize(new string('a', 50), "x@x.com");
        Assert.Equal(32, result.Length);
    }

    [Fact]
    public void Sanitize_FallsBackToUserWhenEverythingStripped()
    {
        Assert.Equal("user", UsernameGenerator.Sanitize("@@@", "@@@@"));
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test FatGuysSpeak.Tests --filter "FullyQualifiedName~UsernameGeneratorTests"`
Expected: FAIL — `UsernameGenerator` does not exist (compile error).

- [ ] **Step 3: Implement the helper**

Create `FatGuysSpeak.Server/Services/UsernameGenerator.cs`:

```csharp
namespace FatGuysSpeak.Server.Services;

public static class UsernameGenerator
{
    /// <summary>
    /// Produces a valid base username (matches AuthController's username rules:
    /// 1-32 chars of [a-zA-Z0-9._-]) from a display name, falling back to the
    /// email local-part, then to "user". Does NOT guarantee uniqueness.
    /// </summary>
    public static string Sanitize(string? name, string email)
    {
        var source = !string.IsNullOrWhiteSpace(name) ? name! : email.Split('@')[0];
        var cleaned = new string(source.ToLowerInvariant()
            .Where(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-')
            .ToArray());
        cleaned = cleaned.Trim('.', '_', '-');
        if (cleaned.Length > 32) cleaned = cleaned[..32];
        return cleaned.Length == 0 ? "user" : cleaned;
    }
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test FatGuysSpeak.Tests --filter "FullyQualifiedName~UsernameGeneratorTests"`
Expected: PASS (6 tests).

- [ ] **Step 5: Commit**

```bash
git add FatGuysSpeak.Server/Services/UsernameGenerator.cs FatGuysSpeak.Tests/Server/UsernameGeneratorTests.cs
git commit -m "Add UsernameGenerator.Sanitize for OAuth username derivation"
```

---

## Task 5: IGoogleTokenValidator + real implementation + DI + test fake

**Files:**
- Create: `FatGuysSpeak.Server/Services/GoogleTokenValidator.cs`
- Create: `FatGuysSpeak.Tests/Helpers/FakeGoogleTokenValidator.cs`
- Modify: `FatGuysSpeak.Server/Program.cs:36` (DI registration area)

- [ ] **Step 1: Create the interface, record, and real implementation**

Create `FatGuysSpeak.Server/Services/GoogleTokenValidator.cs`:

```csharp
using Google.Apis.Auth;

namespace FatGuysSpeak.Server.Services;

public record GoogleIdentity(string Sub, string Email, bool EmailVerified, string? Name);

public interface IGoogleTokenValidator
{
    /// <summary>
    /// Validates a Google ID token's signature, expiry, and audience.
    /// Throws <see cref="InvalidJwtException"/> if the token is invalid.
    /// </summary>
    Task<GoogleIdentity> ValidateAsync(string idToken);
}

public class GoogleTokenValidator(IConfiguration config) : IGoogleTokenValidator
{
    public async Task<GoogleIdentity> ValidateAsync(string idToken)
    {
        var clientId = config["Google:ClientId"];
        var settings = new GoogleJsonWebSignature.ValidationSettings
        {
            // When configured, require the token's audience to match our client id.
            Audience = string.IsNullOrEmpty(clientId) ? null : new[] { clientId }
        };
        var payload = await GoogleJsonWebSignature.ValidateAsync(idToken, settings);
        return new GoogleIdentity(payload.Subject, payload.Email, payload.EmailVerified, payload.Name);
    }
}
```

- [ ] **Step 2: Register in DI**

In `FatGuysSpeak.Server/Program.cs`, after the `builder.Services.AddScoped<TokenService>();` line (currently line 36), add:

```csharp
builder.Services.AddScoped<FatGuysSpeak.Server.Services.IGoogleTokenValidator, FatGuysSpeak.Server.Services.GoogleTokenValidator>();
```

- [ ] **Step 3: Create the test fake**

Create `FatGuysSpeak.Tests/Helpers/FakeGoogleTokenValidator.cs`:

```csharp
using FatGuysSpeak.Server.Services;

namespace FatGuysSpeak.Tests.Helpers;

public sealed class FakeGoogleTokenValidator : IGoogleTokenValidator
{
    public GoogleIdentity? Identity { get; set; }
    public Exception? ThrowOnValidate { get; set; }

    public Task<GoogleIdentity> ValidateAsync(string idToken)
    {
        if (ThrowOnValidate is not null) throw ThrowOnValidate;
        return Task.FromResult(Identity!);
    }
}
```

- [ ] **Step 4: Build server and tests to confirm everything compiles**

Run: `dotnet build FatGuysSpeak.Server --framework net9.0`
Then: `dotnet build FatGuysSpeak.Tests`
Expected: Build succeeded for both.

- [ ] **Step 5: Commit**

```bash
git add FatGuysSpeak.Server/Services/GoogleTokenValidator.cs FatGuysSpeak.Server/Program.cs FatGuysSpeak.Tests/Helpers/FakeGoogleTokenValidator.cs
git commit -m "Add IGoogleTokenValidator (Google.Apis.Auth wrapper) + test fake + DI"
```

---

## Task 6: The external/google endpoint + find-or-create logic

**Files:**
- Modify: `FatGuysSpeak.Shared/Dtos.cs:3-5` (add request record near the other auth DTOs)
- Modify: `FatGuysSpeak.Server/Controllers/AuthController.cs`
- Test: `FatGuysSpeak.Tests/Server/GoogleSignInTests.cs`

- [ ] **Step 1: Add the request DTO**

In `FatGuysSpeak.Shared/Dtos.cs`, after line 5 (`AuthResponse`), add:

```csharp
public record GoogleAuthRequest(string IdToken);
```

- [ ] **Step 2: Write the failing endpoint tests**

Create `FatGuysSpeak.Tests/Server/GoogleSignInTests.cs`:

```csharp
using FatGuysSpeak.Server.Controllers;
using FatGuysSpeak.Server.Models;
using FatGuysSpeak.Server.Services;
using FatGuysSpeak.Shared;
using FatGuysSpeak.Tests.Helpers;
using Google.Apis.Auth;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace FatGuysSpeak.Tests.Server;

public class GoogleSignInTests : IDisposable
{
    private readonly TestDb _testDb;
    private readonly AuthController _controller;
    private readonly FakeGoogleTokenValidator _validator = new();

    public GoogleSignInTests()
    {
        _testDb = new TestDb();
        _controller = new AuthController(_testDb.Db, TestHelpers.CreateTokenService(),
            new SessionBlacklistService());
    }

    public void Dispose() => _testDb.Dispose();

    private Task<ActionResult<AuthResponse>> SignIn() =>
        _controller.GoogleSignIn(new GoogleAuthRequest("any-token"), _validator);

    [Fact]
    public async Task NewGoogleUser_CreatesAccountAndReturnsToken()
    {
        _validator.Identity = new GoogleIdentity("sub-1", "jane@gmail.com", true, "Jane Doe");

        var result = await SignIn();

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var auth = Assert.IsType<AuthResponse>(ok.Value);
        Assert.False(string.IsNullOrWhiteSpace(auth.Token));
        Assert.Equal("janedoe", auth.Username);
        var user = _testDb.Db.Users.Single(u => u.Email == "jane@gmail.com");
        Assert.Equal("", user.PasswordHash);
        Assert.True(_testDb.Db.ExternalLogins.Any(e => e.Provider == "google" && e.ProviderUserId == "sub-1" && e.UserId == user.Id));
    }

    [Fact]
    public async Task SameSub_SecondSignIn_ReturnsSameUserNoDuplicate()
    {
        _validator.Identity = new GoogleIdentity("sub-1", "jane@gmail.com", true, "Jane Doe");
        var first = await SignIn();
        var firstAuth = (AuthResponse)((OkObjectResult)first.Result!).Value!;

        var second = await SignIn();
        var secondAuth = (AuthResponse)((OkObjectResult)second.Result!).Value!;

        Assert.Equal(firstAuth.UserId, secondAuth.UserId);
        Assert.Equal(1, _testDb.Db.Users.Count(u => u.Email == "jane@gmail.com"));
        Assert.Equal(1, _testDb.Db.ExternalLogins.Count(e => e.ProviderUserId == "sub-1"));
    }

    [Fact]
    public async Task MatchingEmail_AutoLinksToExistingPasswordAccount()
    {
        var existing = new User { Username = "jane", Email = "jane@gmail.com", PasswordHash = BCrypt.Net.BCrypt.HashPassword("secret") };
        _testDb.Db.Users.Add(existing);
        await _testDb.Db.SaveChangesAsync();
        _validator.Identity = new GoogleIdentity("sub-1", "jane@gmail.com", true, "Jane Doe");

        var result = await SignIn();

        var auth = (AuthResponse)((OkObjectResult)result.Result!).Value!;
        Assert.Equal(existing.Id, auth.UserId);
        Assert.Equal("jane", auth.Username); // existing username preserved
        Assert.Equal(1, _testDb.Db.Users.Count(u => u.Email == "jane@gmail.com"));
        Assert.True(_testDb.Db.ExternalLogins.Any(e => e.UserId == existing.Id && e.ProviderUserId == "sub-1"));
        var reloaded = _testDb.Db.Users.Single(u => u.Id == existing.Id);
        Assert.False(string.IsNullOrEmpty(reloaded.PasswordHash)); // password untouched
    }

    [Fact]
    public async Task UnverifiedEmail_ReturnsUnauthorizedAndCreatesNothing()
    {
        _validator.Identity = new GoogleIdentity("sub-1", "jane@gmail.com", false, "Jane Doe");

        var result = await SignIn();

        Assert.IsType<UnauthorizedObjectResult>(result.Result);
        Assert.Empty(_testDb.Db.Users);
        Assert.Empty(_testDb.Db.ExternalLogins);
    }

    [Fact]
    public async Task InvalidToken_ReturnsUnauthorizedAndTouchesNoDb()
    {
        _validator.ThrowOnValidate = new InvalidJwtException("bad token");

        var result = await SignIn();

        Assert.IsType<UnauthorizedObjectResult>(result.Result);
        Assert.Empty(_testDb.Db.Users);
        Assert.Empty(_testDb.Db.ExternalLogins);
    }

    [Fact]
    public async Task UsernameCollision_AppendsSuffix()
    {
        _testDb.Db.Users.Add(new User { Username = "janedoe", Email = "other@gmail.com", PasswordHash = "x" });
        await _testDb.Db.SaveChangesAsync();
        _validator.Identity = new GoogleIdentity("sub-1", "jane@gmail.com", true, "Jane Doe");

        var result = await SignIn();

        var auth = (AuthResponse)((OkObjectResult)result.Result!).Value!;
        Assert.Equal("janedoe1", auth.Username);
    }

    [Fact]
    public async Task NewGoogleUser_AutoJoinsDefaultServer()
    {
        var (server, _) = await TestHelpers.SeedServerAsync(_testDb.Db, "owner");
        _validator.Identity = new GoogleIdentity("sub-1", "jane@gmail.com", true, "Jane Doe");

        var result = await SignIn();

        var auth = (AuthResponse)((OkObjectResult)result.Result!).Value!;
        Assert.True(_testDb.Db.ServerMembers.Any(sm => sm.ServerId == server.Id && sm.UserId == auth.UserId));
    }
}
```

- [ ] **Step 3: Run tests to verify they fail**

Run: `dotnet test FatGuysSpeak.Tests --filter "FullyQualifiedName~GoogleSignInTests"`
Expected: FAIL — `AuthController.GoogleSignIn` does not exist (compile error).

- [ ] **Step 4: Implement the endpoint and helper**

In `FatGuysSpeak.Server/Controllers/AuthController.cs`, add this method after `Login` (before `ForgotPassword`):

```csharp
    [HttpPost("external/google")]
    public async Task<ActionResult<AuthResponse>> GoogleSignIn(
        GoogleAuthRequest req, [FromServices] FatGuysSpeak.Server.Services.IGoogleTokenValidator validator)
    {
        if (string.IsNullOrWhiteSpace(req.IdToken))
            return Unauthorized("Missing Google token.");

        FatGuysSpeak.Server.Services.GoogleIdentity identity;
        try
        {
            identity = await validator.ValidateAsync(req.IdToken);
        }
        catch (Google.Apis.Auth.InvalidJwtException)
        {
            return Unauthorized("Invalid Google token.");
        }

        if (!identity.EmailVerified)
            return Unauthorized("Your Google email must be verified to sign in.");

        const string provider = "google";

        // 1. Existing external login for this Google account?
        var link = await db.ExternalLogins
            .FirstOrDefaultAsync(e => e.Provider == provider && e.ProviderUserId == identity.Sub);

        User? user;
        if (link is not null)
        {
            user = await db.Users.FindAsync(link.UserId);
        }
        else
        {
            // 2. Existing account by verified email → auto-link. Otherwise 3. create new.
            user = await db.Users.FirstOrDefaultAsync(u => u.Email == identity.Email);
            if (user is null)
            {
                var baseName = FatGuysSpeak.Server.Services.UsernameGenerator.Sanitize(identity.Name, identity.Email);
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
        }

        if (user is null) return Unauthorized("Account could not be resolved.");

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
```

Note: `db`, `tokens`, `AutoJoinDefaultServerAsync`, and `RecordSessionAsync` are already members of the controller. `User` and `ExternalLogin` are in `FatGuysSpeak.Server.Models`, already imported.

- [ ] **Step 5: Run tests to verify they pass**

Run: `dotnet test FatGuysSpeak.Tests --filter "FullyQualifiedName~GoogleSignInTests"`
Expected: PASS (7 tests).

- [ ] **Step 6: Commit**

```bash
git add FatGuysSpeak.Shared/Dtos.cs FatGuysSpeak.Server/Controllers/AuthController.cs FatGuysSpeak.Tests/Server/GoogleSignInTests.cs
git commit -m "Add POST /api/auth/external/google endpoint with find-or-create + auto-link"
```

---

## Task 7: Username rename endpoint

**Files:**
- Modify: `FatGuysSpeak.Shared/Dtos.cs:75` (near `UpdateBioRequest`)
- Modify: `FatGuysSpeak.Server/Controllers/UsersController.cs`
- Test: `FatGuysSpeak.Tests/Server/UsersControllerTests.cs`

- [ ] **Step 1: Add the request DTO**

In `FatGuysSpeak.Shared/Dtos.cs`, after the `UpdateBioRequest` line, add:

```csharp
public record UpdateUsernameRequest(string Username);
```

- [ ] **Step 2: Write the failing tests**

Open `FatGuysSpeak.Tests/Server/UsersControllerTests.cs`. It already has the required `using` directives and a shared `_testDb`/`_controller` constructed in its constructor. Add these tests inside the class, reusing that shared instance (matching the file's existing pattern):

```csharp
    [Fact]
    public async Task UpdateUsername_Success_ChangesName()
    {
        var user = new User { Username = "oldname", Email = "u@test.com", PasswordHash = "x" };
        _testDb.Db.Users.Add(user);
        await _testDb.Db.SaveChangesAsync();
        TestHelpers.SetUser(_controller, user.Id);

        var result = await _controller.UpdateUsername(new UpdateUsernameRequest("newname"));

        Assert.IsType<NoContentResult>(result);
        Assert.Equal("newname", _testDb.Db.Users.Single(u => u.Id == user.Id).Username);
    }

    [Fact]
    public async Task UpdateUsername_InvalidChars_ReturnsBadRequest()
    {
        var user = new User { Username = "oldname", Email = "u@test.com", PasswordHash = "x" };
        _testDb.Db.Users.Add(user);
        await _testDb.Db.SaveChangesAsync();
        TestHelpers.SetUser(_controller, user.Id);

        var result = await _controller.UpdateUsername(new UpdateUsernameRequest("bad name!"));

        Assert.IsType<BadRequestObjectResult>(result);
    }

    [Fact]
    public async Task UpdateUsername_Taken_ReturnsConflict()
    {
        var user = new User { Username = "oldname", Email = "u@test.com", PasswordHash = "x" };
        var other = new User { Username = "taken", Email = "o@test.com", PasswordHash = "x" };
        _testDb.Db.Users.AddRange(user, other);
        await _testDb.Db.SaveChangesAsync();
        TestHelpers.SetUser(_controller, user.Id);

        var result = await _controller.UpdateUsername(new UpdateUsernameRequest("taken"));

        Assert.IsType<ConflictObjectResult>(result);
    }
```

- [ ] **Step 3: Run tests to verify they fail**

Run: `dotnet test FatGuysSpeak.Tests --filter "FullyQualifiedName~UsersControllerTests.UpdateUsername"`
Expected: FAIL — `UsersController.UpdateUsername` does not exist (compile error).

- [ ] **Step 4: Implement the endpoint**

In `FatGuysSpeak.Server/Controllers/UsersController.cs`, add after `UpdateBio` (before `UpdateStatus`):

```csharp
    [HttpPut("me/username")]
    public async Task<IActionResult> UpdateUsername(UpdateUsernameRequest req)
    {
        var name = req.Username?.Trim() ?? "";
        if (string.IsNullOrWhiteSpace(name) || name.Length > 32)
            return BadRequest("Username must be 1–32 characters.");
        if (!System.Text.RegularExpressions.Regex.IsMatch(name, @"^[a-zA-Z0-9._\-]+$"))
            return BadRequest("Username may only contain letters, digits, underscores, hyphens, and periods.");

        var user = await db.Users.FindAsync(UserId);
        if (user is null) return NotFound();
        if (name != user.Username && await db.Users.AnyAsync(u => u.Username == name))
            return Conflict("Username already taken.");

        user.Username = name;
        await db.SaveChangesAsync();
        return NoContent();
    }
```

- [ ] **Step 5: Run tests to verify they pass**

Run: `dotnet test FatGuysSpeak.Tests --filter "FullyQualifiedName~UsersControllerTests.UpdateUsername"`
Expected: PASS (3 tests).

- [ ] **Step 6: Commit**

```bash
git add FatGuysSpeak.Shared/Dtos.cs FatGuysSpeak.Server/Controllers/UsersController.cs FatGuysSpeak.Tests/Server/UsersControllerTests.cs
git commit -m "Add PUT /api/users/me/username rename endpoint"
```

---

## Task 8: Production DB migration for ExternalLogins (raw SQL, both branches)

`EnsureCreated` does not add tables to an already-existing production database, so the table must also be created via raw SQL on startup (matching the existing pattern).

**Files:**
- Modify: `FatGuysSpeak.Server/Program.cs:674-678` (the AppSequences block — add ExternalLogins right after)

- [ ] **Step 1: Add the raw CREATE TABLE + index for both providers**

In `FatGuysSpeak.Server/Program.cs`, immediately after the `AppSequences` creation block (currently lines 675-678, the `if (isPostgres) ... else ...` for AppSequences), add:

```csharp
    // OAuth external logins (added for Google sign-in).
    if (isPostgres)
    {
        ctx.Database.ExecuteSqlRaw(@"
            CREATE TABLE IF NOT EXISTS ""ExternalLogins"" (
                ""Id"" SERIAL PRIMARY KEY,
                ""UserId"" INTEGER NOT NULL,
                ""Provider"" TEXT NOT NULL,
                ""ProviderUserId"" TEXT NOT NULL,
                ""Email"" TEXT NOT NULL DEFAULT '',
                ""CreatedAt"" TIMESTAMPTZ NOT NULL DEFAULT NOW()
            )");
        ctx.Database.ExecuteSqlRaw(@"CREATE UNIQUE INDEX IF NOT EXISTS ""IX_ExternalLogins_Provider_ProviderUserId"" ON ""ExternalLogins"" (""Provider"", ""ProviderUserId"")");
    }
    else
    {
        ctx.Database.ExecuteSqlRaw(@"
            CREATE TABLE IF NOT EXISTS ExternalLogins (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                UserId INTEGER NOT NULL,
                Provider TEXT NOT NULL,
                ProviderUserId TEXT NOT NULL,
                Email TEXT NOT NULL DEFAULT '',
                CreatedAt TEXT NOT NULL DEFAULT (datetime('now'))
            )");
        ctx.Database.ExecuteSqlRaw(@"CREATE UNIQUE INDEX IF NOT EXISTS IX_ExternalLogins_Provider_ProviderUserId ON ExternalLogins (Provider, ProviderUserId)");
    }
```

- [ ] **Step 2: Build the server**

Run: `dotnet build FatGuysSpeak.Server --framework net9.0`
Expected: Build succeeded.

- [ ] **Step 3: Smoke-test startup against the local SQLite DB**

Run: `dotnet run --project FatGuysSpeak.Server --framework net9.0`
Expected: server starts and logs listening on `http://localhost:5238` with no SQL exceptions. Stop it with Ctrl+C after it reports started. (This confirms the raw migration runs cleanly against the existing local `fatguys.db`.)

- [ ] **Step 4: Commit**

```bash
git add FatGuysSpeak.Server/Program.cs
git commit -m "Create ExternalLogins table + unique index on startup (SQLite + Postgres)"
```

---

## Task 9: Full verification

**Files:** none (verification only)

- [ ] **Step 1: Build both target frameworks**

Run: `dotnet build FatGuysSpeak.Server --framework net9.0`
Then: `dotnet build FatGuysSpeak.Server --framework net9.0-windows10.0.19041.0`
Expected: Build succeeded for both. Address any nullable warnings introduced by the changes.

- [ ] **Step 2: Run the full test suite**

Run: `dotnet test FatGuysSpeak.Tests`
Expected: All tests pass (the suite plus the ~18 new tests added here). Note: per project memory there may be a small number of pre-existing failures unrelated to this work — confirm any failure is pre-existing by checking it does not reference Google, ExternalLogin, UpdateUsername, or the password-less guards.

- [ ] **Step 3: Final commit if anything was adjusted**

```bash
git add -A
git commit -m "Finalize server-side Google sign-in: build + tests green"
```

---

## Self-Review Notes

- **Spec coverage:** endpoint + token validation (Tasks 5, 6); ExternalLogins table (Tasks 2, 8); password-less account handling via empty-string sentinel instead of nullable column (Task 3, deviation noted above); username generation (Tasks 4, 6); username rename (Task 7); config (Task 1); error handling — 401 before DB on invalid token and unverified email, `InvalidJwtException` mapped to 401 (Task 6 tests); tests mirror `AuthControllerTests` with a fake validator (Tasks 4, 6, 7).
- **Out of scope (next plan):** MAUI client UI, Windows WebAuthenticator flow, Google Cloud Console registration, other providers.
- **Type consistency:** `IGoogleTokenValidator.ValidateAsync` → `GoogleIdentity(Sub, Email, EmailVerified, Name)` used identically in the real impl, fake, and endpoint. `GenerateUniqueUsernameAsync` and `UsernameGenerator.Sanitize` signatures match their call sites.
