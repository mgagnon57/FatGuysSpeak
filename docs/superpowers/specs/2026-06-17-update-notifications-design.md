# Update Notifications — Design

**Date:** 2026-06-17
**Status:** Approved for planning

## Goal

Surface "a newer release exists" to both the server admin (dashboard) and client
users (in-app banner), using GitHub Releases as the single source of truth. Notify
only — nothing is blocked or auto-installed.

## Decisions (from brainstorming)

- **Architecture:** the server polls the GitHub Releases API and caches the latest
  version; the client asks the server (one GitHub integration point).
- **Behavior:** notify only — dashboard banner when the server is behind; a
  dismissible client banner with a download link when the client is behind. Nothing
  is blocked; out-of-date keeps working.
- **Policy:** on by default; poll every 6 hours (configurable); disable via config;
  any failure (offline / 404 / rate-limit) degrades silently (no banner, no errors).
- Client banner is dismissible **per session** (reappears next launch while still behind).
- GitHub repo defaults to `mgagnon57/FatGuysSpeak` (configurable).

## Context

- Versioning already exists: `Directory.Build.props` `<Version>` flows everywhere and
  stamps `InformationalVersion` as `MAJOR.MINOR.PATCH+g<sha>.<date>`. `FatGuysSpeak.Shared`
  has `VersionInfo.Parse(string?)` → `record VersionInfo(string Version, string Commit, string BuildDate)`.
  Anonymous `GET /api/version` returns the parsed running version.
- The landing page already distributes via GitHub Releases:
  `https://github.com/mgagnon57/FatGuysSpeak/releases/latest/download/...`. So the
  GitHub Releases API is the natural "latest version" source.
- The server registers hosted services in `Program.cs` (e.g. `TempBanCleanupService`,
  `AuditLogCleanupService`) and singletons (e.g. `SessionBlacklistService`). The dashboard
  HTML is a `const` served by `MetricsController.Dashboard()`, which already does a
  server-side `{{VERSION}}` token `Replace`. The client uses `HttpClient`/`GetFromJsonAsync`
  in `ApiService`.

## Latest-version source

`GET https://api.github.com/repos/{repo}/releases/latest`
- Public repo, unauthenticated (60 req/hr/IP — trivial at a 6h cadence).
- GitHub requires a `User-Agent` header; requests without one are rejected.
- Response JSON: `tag_name` (e.g. `"v1.1.0"`), `html_url` (the release page).
- `404` when no releases are published yet (current state) → treated as "no update known".
- The latest version string = `tag_name` with a leading `v` stripped.

## Shared (`FatGuysSpeak.Shared`)

Add a pure, tolerant SemVer comparison + the status DTO + the tag parse, all unit-testable.

```csharp
public record UpdateStatusDto(string Current, string? Latest, bool UpdateAvailable, string? ReleaseUrl);

public static class SemVer
{
    // Numeric per-component compare of "MAJOR.MINOR.PATCH" (so 1.10.0 > 1.2.0).
    // Tolerant: non-parseable inputs compare as 0.0.0. Returns <0, 0, >0.
    public static int Compare(string? a, string? b);

    // True when `current` is a valid version strictly older than `latest`.
    // False if latest is null/empty/unparseable or current >= latest.
    public static bool IsOutdated(string? current, string? latest);

    // "v1.1.0" -> "1.1.0"; "1.1.0" -> "1.1.0"; null/garbage -> null.
    public static string? NormalizeTag(string? tagName);
}
```

`Compare` parses each side as three integer components (missing/garbage → 0), compares
MAJOR then MINOR then PATCH. `IsOutdated(current, latest)` returns
`NormalizeTag(latest) is {} L && Compare(current, L) < 0`.

## Server

### `UpdateStatus` singleton (cache)
A thread-safe holder of the last successful check:
```csharp
public sealed class UpdateStatus            // registered AddSingleton
{
    public string? LatestVersion { get; private set; }   // normalized, e.g. "1.1.0"
    public string? ReleaseUrl { get; private set; }
    public DateTime? CheckedAtUtc { get; private set; }
    public void Set(string? latest, string? url);         // atomic update
}
```

### `UpdateCheckService : BackgroundService`
- Reads config `UpdateCheck:Enabled` (default `true`), `UpdateCheck:Repo`
  (default `mgagnon57/FatGuysSpeak`), `UpdateCheck:IntervalHours` (default `6`).
- If disabled, returns immediately (no polling).
- Loop: check once at startup, then every `IntervalHours`. Each check:
  `GET https://api.github.com/repos/{repo}/releases/latest` via an `IHttpClientFactory`
  client with `User-Agent: FatGuysSpeak` and `Accept: application/vnd.github+json`,
  short timeout. On 200, parse `tag_name` + `html_url`, normalize, and `UpdateStatus.Set(...)`.
  On any non-200 / exception / cancellation, leave the cache unchanged and continue
  (catch-and-ignore; a single low-severity debug log at most). Honors the
  `stoppingToken` for clean shutdown.
- Uses a dedicated named `HttpClient` ("github") configured in `Program.cs` with the
  base address `https://api.github.com/` and the required headers.

### `GET /api/update-status` (anonymous, minimal API in `Program.cs`)
```csharp
app.MapGet("/api/update-status", (UpdateStatus s) =>
{
    var current = VersionInfo.Parse(typeof(Program).Assembly
        .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion).Version;
    return Results.Ok(new UpdateStatusDto(
        current, s.LatestVersion, SemVer.IsOutdated(current, s.LatestVersion), s.ReleaseUrl));
});
```
`UpdateAvailable` here is for the **server**. The client uses `Latest`/`ReleaseUrl`
and compares against its own version.

### Dashboard banner (`MetricsController`)
- Add an `{{UPDATE_BANNER}}` token near the top of the dashboard body (above the tabs).
- In `Dashboard()` (already injected — change it to take the `UpdateStatus` singleton via
  the controller constructor), compute: if `SemVer.IsOutdated(serverVersion, cache.Latest)`,
  render a banner `Server update available: v{latest} — <a href="{releaseUrl}">Release notes</a>`
  (HTML-encoded values, link target `_blank`), else replace the token with empty string.
- Muted/attention styling consistent with the dashboard; no new JS (CSP-safe).

### Config (`appsettings.json` + `appsettings.Development.json` as needed)
```json
"UpdateCheck": { "Enabled": true, "Repo": "mgagnon57/FatGuysSpeak", "IntervalHours": 6 }
```

## Client

### `ApiService.GetUpdateStatusAsync()`
```csharp
public Task<UpdateStatusDto?> GetUpdateStatusAsync() =>
    _http.GetFromJsonAsync<UpdateStatusDto>("api/update-status");   // tolerant: returns null on failure
```
(Wrapped so a failure/offline returns null without throwing.)

### `MainViewModel` + banner
- On startup/login (where the client already loads initial data), call
  `GetUpdateStatusAsync()`. Compute the CLIENT's own version via
  `VersionInfo.Parse(Assembly.GetExecutingAssembly()...)`.
- If `status?.Latest` is non-null and `SemVer.IsOutdated(clientVersion, status.Latest)`,
  set observable state: `UpdateAvailable = true`, `LatestVersion = status.Latest`,
  `UpdateUrl = status.ReleaseUrl ?? "https://github.com/{repo}/releases/latest"`.
- A dismissible banner (top of the main window) shows
  "Update available — v{LatestVersion}" with a "Download" button opening `UpdateUrl`
  (via `Launcher.OpenAsync`/`Browser.OpenAsync`) and a "✕" that sets `UpdateAvailable=false`
  for the session. No persistence — reappears next launch while still behind.
- If the call returns null or not outdated, no banner.

## Error handling

- Every GitHub call and the client's status call are best-effort; failures leave the
  cache empty / return null, and all surfaces degrade to "no notification."
- The background check never blocks server startup; the client check never blocks login
  or any feature.
- 404 (no releases yet) is a normal, expected state → no banner.

## Testing

Pure unit tests (`FatGuysSpeak.Tests`, no network):
- `SemVer.Compare`: equal; older; newer; multi-digit components (1.10.0 vs 1.2.0 → newer);
  garbage/null inputs → treated as 0.0.0.
- `SemVer.IsOutdated`: current < latest → true; equal/newer → false; null/empty latest → false;
  `v`-prefixed latest handled via NormalizeTag.
- `SemVer.NormalizeTag`: "v1.1.0"→"1.1.0", "1.1.0"→"1.1.0", null/garbage→null.
- A pure `tag_name`→version parse used by the poller (extract so it's testable without HTTP).
- `UpdateStatusDto` `UpdateAvailable` computation in the endpoint path (via the helper).

Runtime verification (not unit-tested): the background poll hitting GitHub, the
`/api/update-status` JSON, the dashboard banner, and the client banner. Simulated by
pointing `UpdateCheck:Repo` at a repo with a higher release (or temporarily seeding the
`UpdateStatus` cache) so "behind" renders, then confirming "up to date" hides both banners.

## Out of scope (v1)

- Hard version gating / minimum-supported-client enforcement (notify only).
- In-app download/launch of the installer (banner links to the release page).
- Auto-update.
- Authenticated GitHub API calls / higher rate limits (unauthenticated is sufficient).
- Notifying about pre-release/draft releases (uses `/releases/latest`, which excludes them).
