# Update Notifications Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Notify the server admin (dashboard) and client users (in-app banner) when a newer release exists, using GitHub Releases as the single source of truth — notify only, nothing blocked.

**Architecture:** A server `BackgroundService` polls the GitHub Releases API every 6h and caches the latest tag in a singleton. An anonymous `/api/update-status` exposes `{current, latest, updateAvailable, releaseUrl}`. The dashboard renders a banner server-side when the server is behind; the client asks the server, compares its own version, and shows a dismissible banner. SemVer comparison lives as a pure helper in Shared.

**Tech Stack:** ASP.NET Core 9 (`BackgroundService`, `IHttpClientFactory`, minimal API), .NET MAUI client, `FatGuysSpeak.Shared`, xUnit.

**Spec:** `docs/superpowers/specs/2026-06-17-update-notifications-design.md`

**Conventions:**
- Headless build: `dotnet build FatGuysSpeak.Server --framework net9.0`
- Run new tests: `dotnet test FatGuysSpeak.Tests --filter "FullyQualifiedName~SemVerTests"` / `~UpdateCheckParseTests`
- Windows builds: if an MSB3027/3021 DLL-copy lock appears from a running app, stop with `Get-Process FatGuysSpeak.Server,FatGuysSpeak.Client -ErrorAction SilentlyContinue | Stop-Process -Force` (PowerShell). A lock is not a compile error.
- Commit trailer: `Co-Authored-By: Claude Opus 4.8 <noreply@anthropic.com>`

---

### Task 1: Shared — SemVer helper + UpdateStatusDto (pure, TDD)

**Files:**
- Modify: `FatGuysSpeak.Shared/DTOs.cs`
- Create: `FatGuysSpeak.Shared/SemVer.cs`
- Create: `FatGuysSpeak.Tests/Server/SemVerTests.cs`

- [ ] **Step 1: Add the DTO**

In `FatGuysSpeak.Shared/DTOs.cs`, add near the other DTOs:

```csharp
public record UpdateStatusDto(string Current, string? Latest, bool UpdateAvailable, string? ReleaseUrl);
```

- [ ] **Step 2: Write the failing tests**

Create `FatGuysSpeak.Tests/Server/SemVerTests.cs`:

```csharp
using FatGuysSpeak.Shared;
using Xunit;

namespace FatGuysSpeak.Tests.Server;

public class SemVerTests
{
    [Theory]
    [InlineData("v1.1.0", "1.1.0")]
    [InlineData("1.1.0", "1.1.0")]
    [InlineData("V2.0.3", "2.0.3")]
    [InlineData(null, null)]
    [InlineData("garbage", null)]
    [InlineData("", null)]
    public void NormalizeTag_StripsLeadingV_OrNullsGarbage(string? input, string? expected)
        => Assert.Equal(expected, SemVer.NormalizeTag(input));

    [Theory]
    [InlineData("1.0.0", "1.0.0", 0)]
    [InlineData("1.0.0", "1.0.1", -1)]
    [InlineData("1.2.0", "1.10.0", -1)]   // numeric, not lexical
    [InlineData("2.0.0", "1.9.9", 1)]
    [InlineData("garbage", "1.0.0", -1)]  // garbage -> 0.0.0
    public void Compare_IsNumericPerComponent(string a, string b, int sign)
        => Assert.Equal(sign, Math.Sign(SemVer.Compare(a, b)));

    [Theory]
    [InlineData("1.0.0", "1.1.0", true)]
    [InlineData("1.1.0", "1.1.0", false)]
    [InlineData("1.2.0", "1.1.0", false)]
    [InlineData("1.0.0", "v1.1.0", true)]   // tag-prefixed latest
    [InlineData("1.0.0", null, false)]
    [InlineData("1.0.0", "", false)]
    [InlineData("1.0.0", "garbage", false)]
    public void IsOutdated_TrueOnlyWhenStrictlyBehindAValidLatest(string current, string? latest, bool expected)
        => Assert.Equal(expected, SemVer.IsOutdated(current, latest));
}
```

- [ ] **Step 3: Run to verify they fail**

Run: `dotnet test FatGuysSpeak.Tests --filter "FullyQualifiedName~SemVerTests"`
Expected: FAIL — `SemVer` does not exist.

- [ ] **Step 4: Implement the helper**

Create `FatGuysSpeak.Shared/SemVer.cs`:

```csharp
namespace FatGuysSpeak.Shared;

/// <summary>Pure, tolerant SemVer helpers for update-version comparison. Never throws.</summary>
public static class SemVer
{
    // "v1.1.0"/"V1.1.0" -> "1.1.0"; "1.1.0" -> "1.1.0"; null / non MAJOR.MINOR.PATCH -> null.
    public static string? NormalizeTag(string? tagName)
    {
        if (string.IsNullOrWhiteSpace(tagName)) return null;
        var s = tagName.Trim();
        if (s.Length > 0 && (s[0] == 'v' || s[0] == 'V')) s = s[1..];
        var parts = s.Split('.');
        if (parts.Length != 3) return null;
        foreach (var p in parts)
            if (!int.TryParse(p, out _)) return null;
        return s;
    }

    // Numeric per-component compare of MAJOR.MINOR.PATCH. Unparseable components -> 0.
    public static int Compare(string? a, string? b)
    {
        var (am, ai, ap) = Parts(a);
        var (bm, bi, bp) = Parts(b);
        if (am != bm) return am.CompareTo(bm);
        if (ai != bi) return ai.CompareTo(bi);
        return ap.CompareTo(bp);
    }

    // True only when `current` is strictly older than a valid `latest`.
    public static bool IsOutdated(string? current, string? latest)
    {
        var l = NormalizeTag(latest);
        return l is not null && Compare(current, l) < 0;
    }

    private static (int, int, int) Parts(string? v)
    {
        if (string.IsNullOrWhiteSpace(v)) return (0, 0, 0);
        var s = v.Trim();
        if (s.Length > 0 && (s[0] == 'v' || s[0] == 'V')) s = s[1..];
        var p = s.Split('.');
        int N(int i) => p.Length > i && int.TryParse(p[i], out var n) ? n : 0;
        return (N(0), N(1), N(2));
    }
}
```

- [ ] **Step 5: Run to verify they pass**

Run: `dotnet test FatGuysSpeak.Tests --filter "FullyQualifiedName~SemVerTests"`
Expected: PASS (all theory cases).

- [ ] **Step 6: Commit**

```bash
git add FatGuysSpeak.Shared/DTOs.cs FatGuysSpeak.Shared/SemVer.cs FatGuysSpeak.Tests/Server/SemVerTests.cs
git commit -m "$(cat <<'EOF'
Update notifications: SemVer compare helper + UpdateStatusDto

Co-Authored-By: Claude Opus 4.8 <noreply@anthropic.com>
EOF
)"
```

---

### Task 2: Server — UpdateStatus cache + UpdateCheckService poller

**Files:**
- Create: `FatGuysSpeak.Server/Services/UpdateStatus.cs`
- Create: `FatGuysSpeak.Server/Services/UpdateCheckService.cs`
- Modify: `FatGuysSpeak.Server/Program.cs` (registrations ~line 193)
- Modify: `FatGuysSpeak.Server/appsettings.json`
- Create: `FatGuysSpeak.Tests/Server/UpdateCheckParseTests.cs`

- [ ] **Step 1: Create the cache singleton**

Create `FatGuysSpeak.Server/Services/UpdateStatus.cs`:

```csharp
namespace FatGuysSpeak.Server.Services;

/// <summary>Thread-safe holder for the last successful GitHub release check.</summary>
public sealed class UpdateStatus
{
    private readonly object _lock = new();
    private string? _latest;
    private string? _url;
    private DateTime? _checkedAt;

    public string? LatestVersion { get { lock (_lock) return _latest; } }
    public string? ReleaseUrl { get { lock (_lock) return _url; } }
    public DateTime? CheckedAtUtc { get { lock (_lock) return _checkedAt; } }

    public void Set(string? latest, string? url)
    {
        lock (_lock) { _latest = latest; _url = url; _checkedAt = DateTime.UtcNow; }
    }
}
```

- [ ] **Step 2: Write the failing test for the JSON parse**

Create `FatGuysSpeak.Tests/Server/UpdateCheckParseTests.cs`:

```csharp
using FatGuysSpeak.Server.Services;
using Xunit;

namespace FatGuysSpeak.Tests.Server;

public class UpdateCheckParseTests
{
    [Fact]
    public void ParsesTagAndUrl()
    {
        var json = """{"tag_name":"v1.2.0","html_url":"https://github.com/x/y/releases/tag/v1.2.0"}""";
        var (version, url) = UpdateCheckService.ParseLatestRelease(json);
        Assert.Equal("1.2.0", version);
        Assert.Equal("https://github.com/x/y/releases/tag/v1.2.0", url);
    }

    [Fact]
    public void MissingFields_YieldNulls()
    {
        var (version, url) = UpdateCheckService.ParseLatestRelease("""{"message":"Not Found"}""");
        Assert.Null(version);
        Assert.Null(url);
    }

    [Fact]
    public void NonSemverTag_NormalizesToNull()
    {
        var (version, _) = UpdateCheckService.ParseLatestRelease("""{"tag_name":"nightly"}""");
        Assert.Null(version);
    }
}
```

- [ ] **Step 3: Run to verify it fails**

Run: `dotnet test FatGuysSpeak.Tests --filter "FullyQualifiedName~UpdateCheckParseTests"`
Expected: FAIL — `UpdateCheckService` does not exist.

- [ ] **Step 4: Create the background service**

Create `FatGuysSpeak.Server/Services/UpdateCheckService.cs`:

```csharp
using System.Text.Json;
using FatGuysSpeak.Shared;

namespace FatGuysSpeak.Server.Services;

/// <summary>Polls the GitHub Releases API for the latest release and caches it.
/// Best-effort: any failure leaves the cache unchanged and surfaces no error.</summary>
public sealed class UpdateCheckService(
    IHttpClientFactory httpFactory, IConfiguration config, UpdateStatus status,
    ILogger<UpdateCheckService> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!config.GetValue("UpdateCheck:Enabled", true)) return;
        var repo = config.GetValue<string?>("UpdateCheck:Repo") ?? "mgagnon57/FatGuysSpeak";
        var interval = TimeSpan.FromHours(Math.Max(1, config.GetValue("UpdateCheck:IntervalHours", 6)));

        while (!stoppingToken.IsCancellationRequested)
        {
            await CheckAsync(repo, stoppingToken);
            try { await Task.Delay(interval, stoppingToken); }
            catch (OperationCanceledException) { break; }
        }
    }

    private async Task CheckAsync(string repo, CancellationToken ct)
    {
        try
        {
            var http = httpFactory.CreateClient("github");
            var json = await http.GetStringAsync($"repos/{repo}/releases/latest", ct);
            var (version, url) = ParseLatestRelease(json);
            if (version is not null) status.Set(version, url);
        }
        catch (Exception ex) { log.LogDebug(ex, "Update check failed"); }
    }

    /// <summary>Pure: GitHub release JSON -> (normalized version, html_url). (null, null) when absent.</summary>
    public static (string? Version, string? Url) ParseLatestRelease(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        string? tag = root.TryGetProperty("tag_name", out var t) ? t.GetString() : null;
        string? url = root.TryGetProperty("html_url", out var u) ? u.GetString() : null;
        return (SemVer.NormalizeTag(tag), url);
    }
}
```

- [ ] **Step 5: Register the client, cache, service, and config**

In `FatGuysSpeak.Server/Program.cs`, near the other `AddHostedService`/`AddHttpClient` registrations (~line 193-198), add:

```csharp
builder.Services.AddHttpClient("github", c =>
{
    c.BaseAddress = new Uri("https://api.github.com/");
    c.Timeout = TimeSpan.FromSeconds(10);
    c.DefaultRequestHeaders.UserAgent.ParseAdd("FatGuysSpeak");
    c.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
});
builder.Services.AddSingleton<FatGuysSpeak.Server.Services.UpdateStatus>();
builder.Services.AddHostedService<FatGuysSpeak.Server.Services.UpdateCheckService>();
```

In `FatGuysSpeak.Server/appsettings.json`, add a top-level entry (e.g. after the `"Giphy"` block — match the file's JSON formatting and comma placement):

```json
  "UpdateCheck": { "Enabled": true, "Repo": "mgagnon57/FatGuysSpeak", "IntervalHours": 6 },
```

- [ ] **Step 6: Run to verify the parse tests pass + build**

Run: `dotnet test FatGuysSpeak.Tests --filter "FullyQualifiedName~UpdateCheckParseTests"`
Expected: PASS (3 tests).
Run: `dotnet build FatGuysSpeak.Server --framework net9.0`
Expected: Build succeeded.

- [ ] **Step 7: Commit**

```bash
git add FatGuysSpeak.Server/Services/UpdateStatus.cs FatGuysSpeak.Server/Services/UpdateCheckService.cs FatGuysSpeak.Server/Program.cs FatGuysSpeak.Server/appsettings.json FatGuysSpeak.Tests/Server/UpdateCheckParseTests.cs
git commit -m "$(cat <<'EOF'
Update notifications: GitHub release poller + cache + config

Co-Authored-By: Claude Opus 4.8 <noreply@anthropic.com>
EOF
)"
```

---

### Task 3: Server — GET /api/update-status endpoint

**Files:**
- Modify: `FatGuysSpeak.Server/Program.cs` (near the `/api/version` endpoint)

**Context:** `Program.cs` already has `using System.Reflection;` and an `app.MapGet("/api/version", ...)` just before `app.MapHub<ChatHub>(...)`.

- [ ] **Step 1: Add the endpoint**

In `Program.cs`, immediately AFTER the existing `app.MapGet("/api/version", ...)` block, add:

```csharp
app.MapGet("/api/update-status", (FatGuysSpeak.Server.Services.UpdateStatus s) =>
{
    var current = FatGuysSpeak.Shared.VersionInfo.Parse(typeof(Program).Assembly
        .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion).Version;
    return Results.Ok(new FatGuysSpeak.Shared.UpdateStatusDto(
        current, s.LatestVersion, FatGuysSpeak.Shared.SemVer.IsOutdated(current, s.LatestVersion), s.ReleaseUrl));
});
```

- [ ] **Step 2: Build to verify it compiles**

Run: `dotnet build FatGuysSpeak.Server --framework net9.0`
Expected: Build succeeded. (Runtime JSON checked in Final Verification.)

- [ ] **Step 3: Commit**

```bash
git add FatGuysSpeak.Server/Program.cs
git commit -m "$(cat <<'EOF'
Update notifications: anonymous GET /api/update-status

Co-Authored-By: Claude Opus 4.8 <noreply@anthropic.com>
EOF
)"
```

---

### Task 4: Dashboard — server-update banner

**Files:**
- Modify: `FatGuysSpeak.Server/Controllers/MetricsController.cs`

**Context:** `MetricsController(ServerMetricsService metrics)` serves the dashboard. `Dashboard()` already does a `{{VERSION}}` token `Replace`. The HTML `const` has a `<style>` block, an `<h1>` (~line 212), then `<div class="tabs">` (~line 219).

- [ ] **Step 1: Inject the UpdateStatus singleton**

Change the controller declaration to also take the cache:

```csharp
public class MetricsController(ServerMetricsService metrics, UpdateStatus updateStatus) : ControllerBase
```

- [ ] **Step 2: Add a banner CSS rule + the {{UPDATE_BANNER}} token**

In the `<style>` block of the `Html` const, add a rule (place it near the other top-level rules):

```css
          .update-banner { background:#3a2a00; border:1px solid #6a5000; color:#e8c060; padding:8px 16px; margin:0 0 12px; font-size:13px; border-radius:4px; }
          .update-banner a { color:#f0c070; }
```

Between the `<h1>...</h1>` line and the `<div class="tabs">` line, add the token on its own line:

```html
          {{UPDATE_BANNER}}
```

- [ ] **Step 3: Render the banner in Dashboard()**

Replace the `Dashboard()` method body with:

```csharp
    [HttpGet("/dashboard")]
    public ContentResult Dashboard()
    {
        var v = VersionInfo.Parse(typeof(MetricsController).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion);
        var label = $"FatGuysSpeak v{v.Version}"
            + (v.Commit.Length > 0 ? $" · {v.Commit}" : "")
            + (v.BuildDate.Length > 0 ? $" · {v.BuildDate}" : "");

        var banner = "";
        if (SemVer.IsOutdated(v.Version, updateStatus.LatestVersion))
        {
            var lv = System.Net.WebUtility.HtmlEncode(updateStatus.LatestVersion);
            var url = System.Net.WebUtility.HtmlEncode(
                updateStatus.ReleaseUrl ?? "https://github.com/mgagnon57/FatGuysSpeak/releases/latest");
            banner = $"<div class=\"update-banner\">⬆ Server update available: v{lv} — "
                + $"<a href=\"{url}\" target=\"_blank\" rel=\"noopener\">Release notes</a></div>";
        }

        return Content(Html
            .Replace("{{VERSION}}", System.Net.WebUtility.HtmlEncode(label))
            .Replace("{{UPDATE_BANNER}}", banner), "text/html");
    }
```

- [ ] **Step 4: Build to verify it compiles**

Run: `dotnet build FatGuysSpeak.Server --framework net9.0`
Expected: Build succeeded. (Visual check in Final Verification.)

- [ ] **Step 5: Commit**

```bash
git add FatGuysSpeak.Server/Controllers/MetricsController.cs
git commit -m "$(cat <<'EOF'
Update notifications: dashboard banner when server is behind

Co-Authored-By: Claude Opus 4.8 <noreply@anthropic.com>
EOF
)"
```

---

### Task 5: Client — update-status fetch + dismissible banner

**Files:**
- Modify: `FatGuysSpeak.Client/Services/ApiService.cs`
- Modify: `FatGuysSpeak.Client/ViewModels/MainViewModel.cs`
- Modify: `FatGuysSpeak.Client/MainPage.xaml`

**Context:** `ApiService` (class at line 8) wraps `_http` with `GetFromJsonAsync`. `MainViewModel.Initialize()` (line 481) runs on startup and `LoadServersAsync()` (line 578) after login. `MainPage.xaml` already hosts banners (e.g. the remote-control "being controlled" banner) bound to `MainViewModel` observable state. READ these files first to match patterns.

- [ ] **Step 1: Add the ApiService call (tolerant)**

In `FatGuysSpeak.Client/Services/ApiService.cs`, add a method near the other `GetFromJsonAsync` wrappers:

```csharp
    public async Task<UpdateStatusDto?> GetUpdateStatusAsync()
    {
        try { return await _http.GetFromJsonAsync<UpdateStatusDto>("api/update-status"); }
        catch { return null; }
    }
```

(Ensure `using FatGuysSpeak.Shared;` and `using System.Net.Http.Json;` are present — other methods in the file already use both.)

- [ ] **Step 2: Add VM state + a check method**

In `MainViewModel`, add observable state (match the file's CommunityToolkit.Mvvm style):

```csharp
    [ObservableProperty] private bool _updateAvailable;
    [ObservableProperty] private string? _latestVersion;
    [ObservableProperty] private string _updateUrl = "https://github.com/mgagnon57/FatGuysSpeak/releases/latest";
```

Add the check (reads the client's OWN version, compares to the server-reported latest):

```csharp
    public async Task CheckForUpdatesAsync()
    {
        var status = await api.GetUpdateStatusAsync();
        if (status?.Latest is null) return;
        var mine = FatGuysSpeak.Shared.VersionInfo.Parse(
            System.Reflection.Assembly.GetExecutingAssembly()
                .GetCustomAttribute<System.Reflection.AssemblyInformationalVersionAttribute>()?.InformationalVersion).Version;
        if (FatGuysSpeak.Shared.SemVer.IsOutdated(mine, status.Latest))
        {
            MainThread.BeginInvokeOnMainThread(() =>
            {
                LatestVersion = status.Latest;
                UpdateUrl = status.ReleaseUrl ?? UpdateUrl;
                UpdateAvailable = true;
            });
        }
    }

    [RelayCommand]
    private async Task OpenUpdate() => await Launcher.OpenAsync(UpdateUrl);

    [RelayCommand]
    private void DismissUpdate() => UpdateAvailable = false;
```

(`api` is the injected `ApiService` field name used elsewhere in this VM — match the actual parameter name. Add `using System.Reflection;` if not present.)

- [ ] **Step 3: Trigger the check after login**

In `LoadServersAsync()` (line 578), after the servers load successfully, fire the check without blocking:

```csharp
        _ = CheckForUpdatesAsync();
```

(Place it at the end of the successful path. Fire-and-forget — it must never block or fail the load.)

- [ ] **Step 4: Add the banner to MainPage.xaml**

Find where the existing top-of-window banners live (e.g. the remote-control banner bound to `IsBeingControlled`). Add, in the same area, a banner shown when `UpdateAvailable`:

```xml
                <Grid IsVisible="{Binding UpdateAvailable}" ColumnDefinitions="*,Auto,Auto"
                      BackgroundColor="#3a2a00" Padding="12,6">
                    <Label Grid.Column="0" VerticalOptions="Center" TextColor="#e8c060"
                           Text="{Binding LatestVersion, StringFormat='⬆ Update available — v{0}'}" />
                    <Button Grid.Column="1" Text="Download" Command="{Binding OpenUpdateCommand}"
                            BackgroundColor="#6a5000" TextColor="#f0d090" Padding="10,2" Margin="6,0" />
                    <Button Grid.Column="2" Text="✕" Command="{Binding DismissUpdateCommand}"
                            BackgroundColor="Transparent" TextColor="#e8c060" Padding="6,2" />
                </Grid>
```

(Match the placement/styling of the existing banners in the real file; adjust colors to the app's palette if needed.)

- [ ] **Step 5: Build to verify it compiles**

Run: `dotnet build FatGuysSpeak.Client --framework net9.0-windows10.0.19041.0`
Expected: Build succeeded (pre-existing XamlC XC0022 warnings are fine).

- [ ] **Step 6: Commit**

```bash
git add FatGuysSpeak.Client/Services/ApiService.cs FatGuysSpeak.Client/ViewModels/MainViewModel.cs FatGuysSpeak.Client/MainPage.xaml
git commit -m "$(cat <<'EOF'
Update notifications: client update-status fetch + dismissible banner

Co-Authored-By: Claude Opus 4.8 <noreply@anthropic.com>
EOF
)"
```

---

## Final verification (after all tasks)

- [ ] `dotnet test FatGuysSpeak.Tests` — all green (incl. SemVerTests, UpdateCheckParseTests).
- [ ] Build server (net9.0-windows) + client; relaunch via `launch.ps1`.
- [ ] `Invoke-RestMethod http://localhost:5238/api/update-status` → `{ current: "1.0.0", latest: <null or version>, updateAvailable: false, releaseUrl: <null or url> }`. (Likely `latest: null` until a GitHub release is published, or 404 → null — that's correct.)
- [ ] Simulate "behind" without publishing a release: temporarily set `UpdateCheck:Repo` to a public repo that HAS a release whose tag is a higher SemVer (or point at `mgagnon57/FatGuysSpeak` once a release exists). Restart the server; after the startup poll, `GET /api/update-status` shows `latest` set; the dashboard (`/dashboard`) shows the amber "Server update available" banner; a client (whose version is below `latest`) shows the "Update available" banner with a working Download button. Revert the config change afterward.
- [ ] Confirm graceful degradation: with `UpdateCheck:Enabled=false` (or offline), no banners appear anywhere and nothing errors.
- [ ] Dispatch a final code review over the whole branch.
