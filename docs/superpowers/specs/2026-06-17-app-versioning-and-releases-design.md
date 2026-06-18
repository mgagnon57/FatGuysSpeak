# App Versioning & Releases — Design

**Date:** 2026-06-17
**Status:** Approved for planning

## Goal

Give FatGuysSpeak one authoritative SemVer version (starting at 1.0.0) defined in a
single place, flowing automatically into the server, client, and installer; surfaced
at runtime in the API, dashboard, client, and landing page; with every build stamped
by its git commit + date; plus a CHANGELOG and a git tag per release, all driven by a
local `release.ps1`.

## Decisions (from brainstorming)

- All four pieces: single source-of-truth version, runtime surfacing, git commit +
  build-date embedding, CHANGELOG + git tags.
- Scheme: SemVer `MAJOR.MINOR.PATCH`, starting at **1.0.0** (the current shipped state).
- Release process: a **local `release.ps1`** (bump + changelog + stamp + build + commit
  + tag); it does NOT push — the operator reviews and pushes.
- `GET /api/version` is **anonymous** (build info only, no secrets).

## Current state (context)

- `FatGuysSpeak.Client.csproj` hardcodes `<ApplicationDisplayVersion>1.0</...>` and
  `<ApplicationVersion>1</...>`. The server has no version property. No
  `Directory.Build.props`, no git tags, no CHANGELOG, no VERSION file, no version shown
  anywhere.
- Server `Program.cs` ends with `app.MapControllers();` then `app.MapHub<ChatHub>(...)`.
- Client has `FatGuysSpeak.Client/Pages/SettingsPage.xaml(.cs)`.
- Dashboard HTML is built as a raw string in `MetricsController.cs` (CSP-strict).
- Landing page is `website/index.html` (source) + `docs/index.html` (kept identical;
  `.github/workflows/sync-pages.yml` syncs them).
- `dotnet build` builds `net9.0` (headless) and `net9.0-windows10.0.19041.0`; installer
  output is `FatGuysSpeak.Installer\bin\Debug\net9.0-windows10.0.19041.0\FatGuysSpeak-Server-Setup.exe`.

## 1. Single source of truth — `Directory.Build.props` (repo root)

Create `C:\FatGuysSpeak\Directory.Build.props`. Every project under the repo inherits it.

```xml
<Project>
  <PropertyGroup>
    <Version>1.0.0</Version>
  </PropertyGroup>

  <!-- Stamp the git short SHA + UTC build date into InformationalVersion on every
       build. Falls back to "nogit" when git is unavailable (clean tarball / CI). -->
  <Target Name="StampGitVersion" BeforeTargets="GetAssemblyVersion;CoreCompile">
    <Exec Command="git rev-parse --short HEAD" ConsoleToMSBuild="true"
          ContinueOnError="true" StandardOutputImportance="low">
      <Output TaskParameter="ConsoleOutput" PropertyName="GitShaRaw" />
      <Output TaskParameter="ExitCode" PropertyName="GitExit" />
    </Exec>
    <PropertyGroup>
      <GitSha Condition="'$(GitExit)' == '0'">$(GitShaRaw.Trim())</GitSha>
      <GitSha Condition="'$(GitSha)' == ''">nogit</GitSha>
      <BuildDateUtc>$([System.DateTime]::UtcNow.ToString('yyyy-MM-dd'))</BuildDateUtc>
      <InformationalVersion>$(Version)+g$(GitSha).$(BuildDateUtc)</InformationalVersion>
    </PropertyGroup>
  </Target>
</Project>
```

The MAUI client csproj is changed so its store-version properties derive from `$(Version)`
rather than hardcoded literals:
- `<ApplicationDisplayVersion>$(Version)</ApplicationDisplayVersion>`
- `<ApplicationVersion>1</ApplicationVersion>` stays an integer (Windows packaging requires
  an integer build number); the human-facing string comes from `ApplicationDisplayVersion`.

Resulting `InformationalVersion` example: `1.0.0+g1a2b3c4.2026-06-17`.

## 2. Version parsing helper — `FatGuysSpeak.Shared/VersionInfo.cs`

Pure, unit-testable. Parses the assembly informational version into parts.

```csharp
public record VersionInfo(string Version, string Commit, string BuildDate)
{
    // Parse "1.0.0+g1a2b3c4.2026-06-17" -> ("1.0.0", "1a2b3c4", "2026-06-17").
    // Tolerant: missing "+..." suffix -> Commit/BuildDate "". "nogit" sha -> Commit "".
    public static VersionInfo Parse(string? informational);
}
```

Semantics (locked, no ambiguity):
- Input null/empty → `("0.0.0", "", "")`.
- No `+` → `(input, "", "")`.
- `<ver>+g<sha>.<date>` → `(<ver>, <sha>, <date>)`; if `<sha>` == `nogit` → Commit `""`.
- A `+` present but not matching `g<sha>.<date>` → `(<ver>, <metadata>, "")` (whole
  metadata string as Commit fallback) — never throws.

## 3. Runtime surfacing

### Server `GET /api/version` (anonymous)
In `Program.cs`, before `app.MapHub(...)`:
```csharp
app.MapGet("/api/version", () =>
{
    var info = typeof(Program).Assembly
        .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
    return Results.Ok(FatGuysSpeak.Shared.VersionInfo.Parse(info));
});
```
Returns `{ version, commit, buildDate }`. No auth, no rate limit beyond defaults.

### Dashboard footer (server-side render)
`MetricsController.cs` builds the dashboard HTML string. Add a footer line near the
bottom of the page body: `FatGuysSpeak v{version} · {commit} · {buildDate}` using the
same `VersionInfo.Parse(...)` of the entry assembly's informational version. Rendered
server-side (no new JS, no CSP impact). Muted styling consistent with the dashboard.

### Client SettingsPage
`SettingsPage.xaml(.cs)` shows a read-only "Version {version} ({commit})" line, read from
`Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyInformationalVersionAttribute>()`
parsed via `VersionInfo.Parse`. Placed in an "About" area of the settings page.

### Landing page
A small footer label `v1.0.0` in `website/index.html` and `docs/index.html` (identical).
Static text; `release.ps1` rewrites it on each release. Place it in the existing footer.

## 4. CHANGELOG + tags

Create `CHANGELOG.md` (root), keep-a-changelog format:
```
# Changelog
All notable changes are documented here. This project adheres to Semantic Versioning.

## [Unreleased]

## [1.0.0] - 2026-06-17
### Added
- Real-time text chat, push-to-talk voice (Opus), screen share + webcam, Whisper STT.
- Remote desktop control during screen share.
- Admin dashboard: live metrics, users, channels, word filter, audit log.
- Message Log moderation console (search, bulk/criteria delete, restore, CSV export).
- Default (Lobby) channel; security hardening; Google sign-in (server + Windows client).
```
Releases are tagged `vX.Y.Z`. (Seed entry summarizes already-shipped work; exact bullets
finalized when writing the file.)

## 5. `release.ps1 <version>` (repo root)

Single argument `Version` (e.g. `1.1.0`). Steps, in order:
1. Validate `Version` matches `^\d+\.\d+\.\d+$` and is strictly greater than the current
   `<Version>` in `Directory.Build.props` (parse + compare numerically); abort otherwise.
2. Abort if the git working tree is dirty (uncommitted changes) — releases are cut from a
   clean tree.
3. Set `<Version>` in `Directory.Build.props` to the new value.
4. CHANGELOG: require a non-empty `## [Unreleased]` section; move its contents under a new
   `## [X.Y.Z] - <yyyy-MM-dd>` heading and leave a fresh empty `## [Unreleased]`.
5. Rewrite the landing-page version label (`v\d+\.\d+\.\d+` → `vX.Y.Z`) in both
   `website/index.html` and `docs/index.html`.
6. Build: `dotnet build FatGuysSpeak.Server -c Release --framework net9.0`;
   `... --framework net9.0-windows10.0.19041.0`; client
   `--framework net9.0-windows10.0.19041.0`; installer
   `--framework net9.0-windows10.0.19041.0`. Abort on any non-zero exit.
7. Copy the produced installer to a `release-output/` dir as
   `FatGuysSpeak-Server-Setup-X.Y.Z.exe` (and the client output zipped as
   `FatGuysSpeak-Client-X.Y.Z.zip`). `release-output/` stays gitignored.
8. `git add Directory.Build.props CHANGELOG.md website/index.html docs/index.html`;
   `git commit -m "Release X.Y.Z"`; `git tag vX.Y.Z`.
9. Print the exact next step and STOP: `git push && git push origin vX.Y.Z`. The script
   never pushes.

The script is idempotent-safe to abort: every mutating step is after the validation gate,
and a dirty-tree / bad-version input aborts before any change.

## 6. Testing

- `VersionInfoTests` (xUnit, pure): null/empty → `0.0.0`; no-suffix passthrough;
  `1.2.3+gabc1234.2026-06-17` → parts; `nogit` → empty commit; malformed metadata → no throw.
- `/api/version` endpoint test: returns 200 with a `VersionInfo`-shaped body whose
  `version` is non-empty. (Use the app's test host if one exists; otherwise assert the
  parse path via the helper, since the endpoint is a one-liner over `VersionInfo.Parse`.)
- Runtime verification (not unit-tested): after wiring, build + run; confirm `/api/version`,
  the dashboard footer, the client settings line, and the landing-page label all show the
  same version, and that the embedded commit matches `git rev-parse --short HEAD`. Then a
  real `release.ps1` dry run on a throwaway patch bump to confirm the end-to-end flow
  (revert the throwaway bump afterward).

## Out of scope (v1)

- GitHub Actions release automation / CI builds (local script only).
- Code signing of the installer.
- Auto-update / update-check in the client.
- Per-component independent versions (one version for the whole product).
