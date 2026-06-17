# Google Sign-In (Windows Client) — Design

**Date:** 2026-06-16
**Scope:** Windows desktop client only, plus the shared server endpoint it needs. Android/iOS/macCatalyst Google sign-in is a separate later spec.

**Builds on:** the server-side Google sign-in merged/opened in PR #1 (`docs/superpowers/specs/2026-06-16-google-signin-server-design.md`), which already provides `IGoogleTokenValidator`, the `ExternalLogins` table, passwordless-account handling, username generation, and the `POST /api/auth/external/google` (ID-token) endpoint.

## Goal

Let a user sign in to the Windows client with Google. The client runs Google's loopback desktop OAuth flow to obtain an authorization code, sends that code to the server, the server exchanges it for an ID token (secret stays server-side), validates it, resolves/creates the account, and returns the existing JWT `AuthResponse`. From "server has a verified Google identity" onward, everything reuses the merged server logic.

## Key Decisions

- **OAuth mechanism (Windows):** Loopback + system browser. MAUI `WebAuthenticator` is unreliable on this unpackaged (`WindowsPackageType=None`) Win32 app; Google's documented desktop flow (loopback `127.0.0.1` redirect) avoids MSIX/registry requirements.
- **Token exchange location:** Server-side. The client sends the auth code + PKCE verifier + redirect_uri to a new endpoint; the server exchanges with Google using the client secret. The secret never ships in the client binary.
- **Platform scope:** Windows only now. Mobile (WebAuthenticator + custom URI scheme, per-platform Google clients) is a separate follow-up spec — the loopback mechanism is desktop-specific and mobile cannot be built/verified in this environment.
- **OAuth client type:** A Google "Desktop app" OAuth client (loopback redirect allowed on any `127.0.0.1` port without per-port registration). External setup, owned by the maintainer.

## Architecture & End-to-End Flow

1. User clicks "Continue with Google" on the login page (Windows).
2. Client generates a PKCE verifier + S256 challenge and a random `state`.
3. Client starts a one-shot `HttpListener` on `http://127.0.0.1:{free-port}/`.
4. Client opens the system browser to Google's authorization URL: client_id, `redirect_uri=http://127.0.0.1:{port}`, `response_type=code`, `scope=openid email profile`, `code_challenge`, `code_challenge_method=S256`, `state`.
5. User authenticates in the real browser; Google redirects to the loopback URL with `?code=…&state=…` (or `?error=…`).
6. Listener captures the request, verifies `state`, writes a small "you can close this tab" HTML response, and shuts down.
7. Client POSTs `{ code, codeVerifier, redirectUri }` to `POST /api/auth/external/google/exchange`.
8. Server exchanges the code at `https://oauth2.googleapis.com/token` (client_id + server-held client_secret + `grant_type=authorization_code` + code + verifier + redirect_uri) → receives an ID token.
9. Server validates the ID token via the existing `IGoogleTokenValidator` (audience = our client_id, `email_verified` required), then runs the existing resolve-and-issue path, returning `AuthResponse`.
10. Client stores the token and navigates to `//main`, exactly like password login.

The client never sees the client_secret. The loopback dance is the only genuinely new client concern.

## Server Changes (all additive)

### 1. `IGoogleCodeExchanger` + `GoogleCodeExchanger`

`Task<string> ExchangeAsync(string code, string codeVerifier, string redirectUri)` → returns the `id_token` string.

- POSTs (form-encoded) to `https://oauth2.googleapis.com/token` with `client_id` (`Google:ClientId`), `client_secret` (`Google:ClientSecret`), `grant_type=authorization_code`, `code`, `code_verifier`, `redirect_uri`.
- Uses `IHttpClientFactory` (a named client, mirroring the existing `anthropic` client) so it is injectable and fakeable.
- Throws on non-success or missing `id_token` (mapped to 401 by the controller). Throws `InvalidOperationException` when `Google:ClientId`/`Google:ClientSecret` is unconfigured (mapped to 503), consistent with the validator.

### 2. `POST /api/auth/external/google/exchange`

- Request: `GoogleCodeExchangeRequest(string Code, string CodeVerifier, string RedirectUri)` (new record in `DTOs.cs`).
- Response: existing `AuthResponse`.
- Flow: call `IGoogleCodeExchanger.ExchangeAsync` → ID token → `IGoogleTokenValidator.ValidateAsync` → resolve-and-issue.
- On exchanger failure / missing id_token: 401 "Google sign-in failed", no DB writes.
- On unconfigured (`InvalidOperationException`): 503.
- Inherits `[EnableRateLimiting("auth")]` from the controller.

### 3. Refactor: shared resolve-and-issue helper

Extract the account resolution + JWT issuance from the existing `GoogleSignIn` (ID-token) endpoint into a private `ResolveGoogleIdentityAndIssueAsync(GoogleIdentity identity)` helper. Both endpoints call it. The existing ID-token endpoint stays for future/mobile clients. This avoids duplicating the find-or-create / transaction / audit-log / auto-join / session logic.

### 4. `GET /api/auth/external/google/config` (public)

- Returns `{ clientId }` = `Google:ClientId` (empty string when unset).
- Unauthenticated; exposes only the non-secret client_id.
- Lets the client build its auth URL from a single source of truth and hide the Google button when the server isn't configured.
- New DTO: `GoogleConfigResponse(string ClientId)`.

### 5. Configuration

- `Google:ClientSecret` — real secret, server-side only (env `Google__ClientSecret`), empty placeholder in `appsettings.json`. Never logged; never returned by the config endpoint.
- `Google:ClientId` already exists from PR #1.

## Client Changes (Windows)

### Pure, testable logic — in `FatGuysSpeak.Shared`

(Follows the `VideoUrlHelper` precedent: the test project references Shared, not the MAUI client.)

- `PkceHelper` — generate a code verifier (43–128 chars, unreserved charset), its S256 challenge (base64url, no padding), and a random `state`.
- `GoogleAuthUrlBuilder` — compose the authorization URL from client_id, redirect_uri, scope, challenge, state, with correct percent-encoding.
- `LoopbackRedirectParser` — extract `code`/`state`/`error` from the captured redirect query; verify `state`; signal cancellation/error.

### Orchestration — `GoogleAuthService` (Windows-only, `#if WINDOWS`)

Registered in `MauiProgram`. Responsibilities:

- Fetch client_id via `ApiService.GetGoogleConfigAsync()`.
- Build PKCE values + URL via the Shared helpers.
- Open a free ephemeral loopback port with `HttpListener` (bound to `127.0.0.1`), accept exactly one request, then close.
- Launch the system browser.
- Await the redirect with a timeout (~2 min) and cancellation; write the "you can close this tab" response.
- Parse the result and return `(code, codeVerifier, redirectUri)` or a cancellation/error outcome.

Not unit-tested (thin orchestration over tested Shared helpers + interactive I/O); verified by running the Windows client.

### `ApiService`

- `GetGoogleConfigAsync()` → `GoogleConfigResponse`.
- `ExchangeGoogleCodeAsync(GoogleCodeExchangeRequest)` → `AuthResponse` (same error-unwrapping pattern as `LoginAsync`).

### `AuthViewModel`

- `IsGoogleAvailable` flag, set from the config call (controls button visibility).
- `GoogleSignInCommand` — runs `GoogleAuthService`, posts the code via `ExchangeGoogleCodeAsync`, and on success follows the identical path `LoginAsync` already uses (set token, set current user, persist server, load PTT, connect hub, navigate to `//main`). Surfaces cancellation/errors in the existing `ErrorMessage` label.

### UI — `LoginPage.xaml`

- A "Continue with Google" button below "Log In", bound to `GoogleSignInCommand` and `IsGoogleAvailable`, styled to match the dark theme.

### Cross-platform compile

`GoogleAuthService`, its DI registration, and the command's invocation of it are guarded so non-Windows builds compile; the button stays hidden where the service is absent (also the seam for the mobile follow-up).

## Error Handling & Security

- **Client cancel/deny:** `error=access_denied` or no redirect → timeout + cancellation → friendly "Google sign-in was cancelled" in `ErrorMessage`; no hang.
- **State mismatch:** hard failure (possible CSRF/interference) — abort, do not exchange.
- **Listener hygiene:** binds only `127.0.0.1` on an ephemeral port, one request then closed.
- **Server exchange failure** (bad/expired code, redirect_uri mismatch, network, missing id_token): 401 "Google sign-in failed", no DB writes, never 500.
- **ID-token validation:** unchanged — signature, audience, `email_verified` enforced; unconfigured server → 503.
- **Secrets:** client_secret read from server config only; never logged; never returned by the public config endpoint (client_id only).
- **PKCE:** an intercepted loopback code is useless without the verifier the client holds.
- **Rate limiting:** exchange endpoint inherits the controller's `auth` policy.

## Testing

### Server (xUnit, `TestDb` + fakes)

- Fake `IGoogleCodeExchanger` injected alongside the existing fake `IGoogleTokenValidator`.
- Exchange endpoint with a valid code → token returned, account resolved/created (proves reuse of the shared helper).
- Exchanger throws (Google failure) → 401, no DB writes.
- Exchange returns missing/empty id_token → 401.
- Unconfigured → 503.
- Config endpoint → returns configured client_id; empty when unset.
- The existing seven ID-token tests still guard find-or-create / auto-link / username / auto-join unchanged.

### Client logic (xUnit against Shared helpers)

- `PkceHelper`: verifier length/charset valid; S256 challenge correct (base64url, no padding) for a known verifier.
- `GoogleAuthUrlBuilder`: URL has the right endpoint and all required, properly-encoded params.
- `LoopbackRedirectParser`: extracts code/state; detects `error`; flags state mismatch.

`GoogleAuthService` (HttpListener + browser) is verified by running the Windows client, not unit-tested.

## Out of Scope (next spec)

- Mobile Google sign-in (Android/iOS/macCatalyst): WebAuthenticator + custom URI scheme, per-platform Google OAuth client registration (Android package + SHA-1, iOS bundle id), platform redirect config.
- Google Cloud Console OAuth client + secret provisioning (external setup the maintainer performs).
