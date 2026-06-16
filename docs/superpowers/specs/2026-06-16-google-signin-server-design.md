# Google Sign-In (Server-Side) — Design

**Date:** 2026-06-16
**Scope:** Server-side only. The MAUI client UI and Windows WebAuthenticator flow are a separate, later deliverable.

## Goal

Let users authenticate with Google in addition to the existing username/password flow. The server accepts a Google-issued ID token, validates it, resolves or creates a local account, and returns the same `AuthResponse` (JWT) the client already consumes — so everything downstream (SignalR, sessions, revocation, auto-join) is unchanged.

## Key Decisions

- **Trust model:** Client-driven OAuth. The client runs Google's authorization-code-with-PKCE flow and obtains a Google **ID token** (a signed JWT). It posts that ID token to the server. The server validates the token's signature, audience, and issuer against Google's published keys, then trusts the `email` and stable `sub` inside. The server does **not** run the redirect/code-exchange flow itself.
- **Account linking:** Auto-link on verified email. If a Google sign-in's `email_verified` is true and the email matches an existing account, the Google identity is attached to that account automatically.
- **Username for new users:** Auto-generate a sanitized, unique username from the Google name/email. User can rename later via a new endpoint.
- **Validation library:** Google's official `Google.Apis.Auth` (`GoogleJsonWebSignature.ValidateAsync`), wrapped behind an injectable interface for testability. No hand-rolled JWKS handling.

## Architecture & Request Flow

New endpoint: `POST /api/auth/external/google`, added to the existing `AuthController` so it inherits the `auth` rate-limit policy (10 req/min per IP).

Request body: `GoogleAuthRequest(string IdToken)` (new record in `Shared/Dtos.cs`).
Response: existing `AuthResponse(Token, Username, UserId, AvatarUrl)`.

Flow inside the endpoint:

1. **Validate** the ID token via `IGoogleTokenValidator` (real impl wraps `GoogleJsonWebSignature.ValidateAsync` with the configured client ID as expected audience). Reject invalid/expired/wrong-audience tokens with **401** *before any DB access*.
2. Reject if `email_verified` is false → **401** (we never auto-link or create on an unverified email).
3. Extract `sub` (permanent Google user id), `email`, `name` from the validated payload.
4. **Resolve account**, in priority order:
   1. Existing `ExternalLogin` row for (`google`, `sub`) → that user. Log in.
   2. Else existing `User` by verified `email` → auto-link: insert an `ExternalLogin` row pointing at that user. Log in.
   3. Else create a new `User` (no password) with an auto-generated username, then insert the `ExternalLogin` row.
5. Rejoin the existing path: `AutoJoinDefaultServerAsync`, issue JWT via `TokenService`, record a `UserSession`, return `AuthResponse`.

## Data Model

### New table: `ExternalLogins`

| Column | Type | Notes |
|---|---|---|
| `Id` | int PK | |
| `UserId` | int FK → User | |
| `Provider` | string | e.g. `"google"` |
| `ProviderUserId` | string | Google `sub` |
| `Email` | string | verified email captured at link time (audit) |
| `CreatedAt` | DateTime | UTC |

- Unique index on (`Provider`, `ProviderUserId`) — same external identity can't link twice.
- A given provider should link at most once per user.
- Registered on `AppDbContext`; created in **both** the SQLite and PostgreSQL branches in `Program.cs` (this codebase uses `EnsureCreated()` + raw `ALTER TABLE` checks at startup, not EF migrations). The unique index is created in both branches.

### `User.PasswordHash` becomes nullable

- Change from non-nullable `string` (default `""`) to `string?`. OAuth-only users have no password.
- Existing rows keep their hash; existing password login is untouched.
- The linkage lives entirely in `ExternalLogins` — no new column on `User`. This keeps multi-provider support (Facebook, etc.) open without further schema churn.

## Username Generation

From Google `name` (fallback: local-part of `email`):

1. Lowercase.
2. Strip everything outside `[a-zA-Z0-9._-]`; collapse/trim stray separators.
3. Clamp to 32 chars. If empty after sanitizing, fall back to `user`.
4. Ensure uniqueness: if taken, append a numeric suffix (`john.smith`, `john.smith1`, `john.smith2`, …) until free, staying within 32 chars.

Reuses the exact validation rules already enforced in `Register`, so generated names are always valid.

## Username Rename Endpoint

`PUT api/users/me/username`, following the existing `me/bio` and `me/status` pattern in `UsersController.cs`.

- Body carries the new username.
- Same regex + length validation as `Register`; same uniqueness check; **409** on collision.
- Available to all users, not just OAuth users (small general improvement). This is what makes auto-generated names "editable later."

## Error Handling & Security

- **Invalid/malformed/expired token, signature/audience mismatch:** 401. Validation runs before any DB lookup, so a bad token never touches the DB. Catch `InvalidJwtException` specifically and map to 401 (no 500).
- **Unverified email (`email_verified` false):** 401. Never auto-link or create. The auto-link decision rests entirely on trusting this flag.
- **Account-creation races:** Two simultaneous first-time sign-ins for the same new email could both attempt insert. The unique index on (`Provider`, `ProviderUserId`) plus the existing email-unique constraint cause the second insert to fail at the DB. Wrap creation in the same transaction pattern `Register` uses; treat a unique-constraint violation as "someone else just created it" — re-fetch and log in. The username-suffix loop has a similar small race, also handled by re-query on conflict.
- **Login-path guards:** Password `Login`, `ForgotPassword`, and `ResetPassword` all require a present `PasswordHash`. An OAuth-only account (null/empty hash) gets "Invalid credentials" from password login and is silently ignored by forgot-password — nothing leaks whether an email is OAuth-only.

## Configuration

- `Google:ClientId` in configuration, read like the existing `Jwt:*` values — `appsettings.Development.json` locally, env var on Railway.
- The client ID is not secret, but follow the existing pattern of not committing real values.
- No client secret server-side: PKCE public-client flow does not use one.

## Testing

Follows the existing `AuthControllerTests` pattern — controller instantiated directly against a `TestDb` (in-memory SQLite), with a **fake `IGoogleTokenValidator`** injected (returns a canned `GoogleIdentity` or throws) so nothing hits Google's network.

Cases:

1. New Google user creates account, returns a token; generated username is valid and unique.
2. Second sign-in by the same Google `sub` returns the same user, not a duplicate.
3. Google email matching an existing password account auto-links — an `ExternalLogin` row is created, existing user returned, password account otherwise untouched.
4. Unverified email → 401, creates nothing.
5. Invalid token (fake validator throws) → 401, never touches the DB.
6. Username collision on generation appends a suffix and still produces a valid name.
7. New Google users auto-join the default server like password users.
8. Rename endpoint: success; length/regex rejection; 409 on collision.
9. Guard: OAuth-only account (null password hash) can't log in via password endpoint and is ignored by forgot-password.

## Out of Scope (next deliverable)

- MAUI client "Sign in with Google" button and command in `AuthViewModel`.
- Windows WebAuthenticator flow and custom URI callback-scheme registration.
- Google Cloud Console OAuth client registration (external setup).
- Other providers (Facebook, etc.) — the `ExternalLogins` design leaves this open.
