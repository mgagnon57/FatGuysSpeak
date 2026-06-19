using System.Reflection;
using FatGuysSpeak.Server.Services;
using FatGuysSpeak.Shared;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace FatGuysSpeak.Server.Controllers;

[ApiController]
[Authorize(Policy = "DashboardAdmin")]
public class MetricsController(ServerMetricsService metrics, UpdateStatus updateStatus) : ControllerBase
{
    [HttpGet("/api/metrics")]
    public IActionResult GetMetrics() => Ok(metrics.GetSnapshot());

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

    private const string Html = """
        <!DOCTYPE html>
        <html lang="en">
        <head>
        <meta charset="UTF-8">
        <meta name="viewport" content="width=device-width, initial-scale=1.0">
        <title>FatGuysSpeak — Server Dashboard</title>
        <script src="/lib/chart.umd.min.js"></script>
        <style>
          * { margin: 0; padding: 0; box-sizing: border-box; }
          body { background: #0c0b0b; color: #e9e4d9; font-family: 'Segoe UI', system-ui, sans-serif; padding: 20px 24px; }

          header {
            display: flex; justify-content: space-between; align-items: center;
            margin-bottom: 16px; padding-bottom: 14px;
            border-bottom: 1px solid #1a1818;
          }
          header h1 { font-size: 16px; color: #f04010; font-weight: 600; letter-spacing: 0.3px; }

          .live-badge { display: flex; align-items: center; gap: 7px; }
          .live-dot {
            width: 8px; height: 8px; border-radius: 50%; background: #36b864;
            animation: pulse 2s ease-in-out infinite;
          }
          @keyframes pulse { 0%,100% { opacity:1; box-shadow:0 0 0 0 rgba(68,187,68,.4); }
                             50%  { opacity:.6; box-shadow:0 0 0 5px rgba(68,187,68,0); } }
          .live-text { font-size: 11px; color: #5a544a; }

          .update-banner { background:#3a2a00; border:1px solid #6a5000; color:#e8c060; padding:8px 16px; margin:0 0 12px; font-size:13px; border-radius:4px; }
          .update-banner a { color:#f0c070; }

          /* ── Tabs ── */
          .tabs { display: flex; gap: 2px; margin-bottom: 16px; border-bottom: 1px solid #292525; }
          .tab-btn {
            background: none; border: none; color: #5a544a; font-size: 12px; font-family: inherit;
            padding: 8px 18px; cursor: pointer; border-bottom: 2px solid transparent;
            margin-bottom: -1px; transition: color .15s, border-color .15s;
          }
          .tab-btn:hover { color: #9a9183; }
          .tab-btn.active { color: #f04010; border-bottom-color: #f04010; }
          .tab-panel { display: none; }
          .tab-panel.active { display: block; }

          /* ── Overview tab ── */
          .cards {
            display: grid;
            grid-template-columns: repeat(4, 1fr);
            gap: 12px;
            margin-bottom: 16px;
          }
          .card {
            background: #161414; border: 1px solid #292525; border-radius: 8px;
            padding: 16px 18px; position: relative; overflow: hidden;
            transition: border-color .3s;
          }
          .card.flash { border-color: #d42d00; }
          .card .icon { font-size: 18px; margin-bottom: 10px; display: block; }
          .card .value {
            font-size: 30px; font-weight: 700; color: #ffffff; line-height: 1;
            margin-bottom: 6px; transition: color .4s;
          }
          .card.flash .value { color: #f04010; }
          .card .label { font-size: 10px; color: #4a443d; text-transform: uppercase; letter-spacing: .8px; }

          .chart-card {
            background: #161414; border: 1px solid #292525; border-radius: 8px;
            padding: 16px 18px;
          }
          .chart-header {
            display: flex; justify-content: space-between; align-items: center;
            margin-bottom: 12px;
          }
          .chart-header h2 { font-size: 12px; color: #5a544a; font-weight: 500; text-transform: uppercase; letter-spacing: .6px; }
          .chart-legend { font-size: 10px; color: #322e28; }
          .chart-legend span { display: inline-block; width: 8px; height: 8px; border-radius: 2px; margin-right: 4px; vertical-align: middle; }
          .chart-wrap { height: 170px; }

          /* ── Users tab ── */
          .panel-header {
            display: flex; justify-content: space-between; align-items: center;
            margin-bottom: 12px;
          }
          .panel-header h2 { font-size: 12px; color: #5a544a; font-weight: 500; text-transform: uppercase; letter-spacing: .6px; }
          .search-box {
            background: #161414; border: 1px solid #292525; border-radius: 4px;
            color: #e9e4d9; font-size: 11px; padding: 5px 10px; font-family: inherit;
            width: 200px;
          }
          .search-box:focus { outline: none; border-color: #f04010; }

          .user-table { width: 100%; border-collapse: collapse; font-size: 12px; }
          .user-table th {
            text-align: left; padding: 7px 10px; color: #4a443d;
            font-size: 10px; text-transform: uppercase; letter-spacing: .6px;
            border-bottom: 1px solid #292525; font-weight: 500;
          }
          .user-table td {
            padding: 8px 10px; border-bottom: 1px solid #1a1717;
            vertical-align: middle;
          }
          .user-table tr:hover td { background: #1a1717; }
          .badge {
            display: inline-block; padding: 2px 7px; border-radius: 10px;
            font-size: 10px; font-weight: 500;
          }
          .badge-online  { background: #12240f; color: #36b864; }
          .badge-offline { background: #1a1818; color: #4a443d; }
          .badge-voice   { background: #1a1410; color: #f04010; }
          .badge-away    { background: #3a2a0a; color: #e89000; }
          .badge-dnd     { background: #3a1010; color: #ed4245; }

          .btn-sm {
            background: #222020; border: 1px solid #3a3a3a; color: #9a9183;
            font-size: 10px; padding: 3px 9px; border-radius: 3px; cursor: pointer;
            font-family: inherit; transition: background .15s;
          }
          .btn-sm:hover { background: #3a3a3a; }
          .btn-sm.danger { border-color: #5a2020; color: #ed4245; }
          .btn-sm.danger:hover { background: #3a1515; }
          .btn-sm:disabled { opacity: .4; cursor: default; }

          .status-bar {
            display: flex; justify-content: space-between; align-items: center;
            margin-top: 12px; font-size: 10px; color: #322e28;
          }

          /* ── Role tooltip ── */
          .role-tip { position: relative; }
          .role-tip::after {
            content: attr(data-tip);
            position: absolute;
            bottom: calc(100% + 8px);
            left: 50%;
            transform: translateX(-50%);
            background: #141010;
            border: 1px solid #292525;
            border-radius: 6px;
            padding: 9px 13px;
            font-size: 11px;
            line-height: 1.75;
            color: #e9e4d9;
            white-space: pre;
            pointer-events: none;
            opacity: 0;
            transition: opacity .15s ease;
            z-index: 9999;
            box-shadow: 0 6px 20px rgba(0,0,0,.75);
            font-weight: normal;
          }
          .role-tip:hover::after { opacity: 1; }

          .user-link { color: #f04010; cursor: pointer; }
          .user-link:hover { text-decoration: underline; }
          .modal-backdrop {
            position: fixed; inset: 0; background: rgba(0,0,0,.6);
            display: flex; align-items: center; justify-content: center; z-index: 9998;
          }
          .modal-card {
            background: #141212; border: 1px solid #292525; border-radius: 10px;
            width: 460px; max-width: 92vw; max-height: 86vh; overflow-y: auto;
            box-shadow: 0 12px 40px rgba(0,0,0,.6); padding: 0 0 8px;
          }
          .pf-head {
            display: flex; justify-content: space-between; align-items: center;
            padding: 16px 18px; border-bottom: 1px solid #1a1818;
          }
          .pf-head h2 { font-size: 16px; color: #f04010; }
          .pf-close { background: none; border: none; color: #6a6358; font-size: 16px; cursor: pointer; }
          .pf-close:hover { color: #ed4245; }
          .pf-section { padding: 12px 18px; border-bottom: 1px solid #181616; }
          .pf-section:last-child { border-bottom: none; }
          .pf-row { display: flex; justify-content: space-between; gap: 16px; padding: 4px 0; font-size: 12px; }
          .pf-label { color: #5a544a; text-transform: uppercase; letter-spacing: .5px; font-size: 10px; }
          .pf-val { color: #e9e4d9; text-align: right; word-break: break-word; }

          /* ── Users table: identity, presence, sort, hover-reveal ── */
          .u-cell { display: flex; align-items: center; gap: 8px; }
          .u-av { width: 22px; height: 22px; border-radius: 50%; object-fit: cover; flex-shrink: 0; }
          .u-av-fallback { display: inline-flex; align-items: center; justify-content: center; font-size: 11px; font-weight: 600; color: #fff; }
          .presence-dot { width: 8px; height: 8px; border-radius: 50%; flex-shrink: 0; }
          .actions-cell { display: flex; gap: 6px; align-items: center; flex-wrap: wrap; }
          .sort-ind { color: #f04010; font-size: 9px; }
          thead th[data-sortkey]:hover { color: #9a9183; }
        </style>
        </head>
        <body>

        <header>
          <h1>🖥&nbsp; FatGuysSpeak &mdash; Server Dashboard</h1>
          <div class="live-badge" title="Overview cards refresh every 2 s; tabs refresh every 5 s">
            <div class="live-dot" id="liveDot"></div>
            <span class="live-text" id="lastUpdated">connecting…</span>
          </div>
        </header>

          {{UPDATE_BANNER}}
        <div class="tabs">
          <button class="tab-btn active" data-click="tab" data-tab="overview" title="Server health metrics, message throughput, and rate-limit overview">Overview</button>
          <button class="tab-btn" data-click="tab" data-tab="users" title="View all users — change roles, mute, kick, or temporarily ban">Users</button>
          <button class="tab-btn" data-click="tab" data-tab="messages" title="Search and moderate all server messages">Message Log</button>
          <button class="tab-btn" data-click="tab" data-tab="channels" title="Configure the minimum role required to read or write in each channel">Channels</button>
          <button class="tab-btn" data-click="tab" data-tab="wordfilter" title="Manage blocked words and phrases — Members who trigger the filter have their message blocked or the word replaced">Word Filter</button>
          <button class="tab-btn" data-click="tab" data-tab="audit" title="Track every moderation action taken by admins and moderators">Audit Log</button>
        </div>

        <!-- ── Overview ─────────────────────────────────── -->
        <div id="tab-overview" class="tab-panel active">

          <div class="cards">
            <div class="card" id="c-online" title="Users currently connected via SignalR (includes idle/away/DnD)">
              <span class="icon">👥</span>
              <div class="value" id="onlineUsers">—</div>
              <div class="label">Online Users</div>
            </div>
            <div class="card" id="c-voice" title="Users actively inside a voice channel right now">
              <span class="icon">🎙</span>
              <div class="value" id="voiceParticipants">—</div>
              <div class="label">Voice Sessions</div>
            </div>
            <div class="card" id="c-streams" title="Users broadcasting their screen or webcam via MJPEG stream">
              <span class="icon">📺</span>
              <div class="value" id="activeStreams">—</div>
              <div class="label">Active Streams</div>
            </div>
            <div class="card" id="c-msgsmin" title="Text messages received in the last 60 seconds">
              <span class="icon">💬</span>
              <div class="value" id="msgsPerMin">—</div>
              <div class="label">Msgs / Min</div>
            </div>
            <div class="card" id="c-total" title="Total messages ever stored in the database">
              <span class="icon">📨</span>
              <div class="value" id="totalMsgs">—</div>
              <div class="label">Total Messages</div>
            </div>
            <div class="card" id="c-mem" title="Server process working-set memory in megabytes">
              <span class="icon">🧠</span>
              <div class="value" id="memoryMb">—</div>
              <div class="label">Memory (MB)</div>
            </div>
            <div class="card" id="c-cpu" title="Server process CPU usage across all cores (0–100 %)">
              <span class="icon">⚡</span>
              <div class="value" id="cpuPct">—</div>
              <div class="label">CPU %</div>
            </div>
            <div class="card" id="c-uptime" title="Time elapsed since the server process started">
              <span class="icon">⏱</span>
              <div class="value" id="uptime">—</div>
              <div class="label">Uptime</div>
            </div>
          </div>

          <div class="chart-card">
            <div class="chart-header" title="Number of text messages sent per minute over the last hour. Bars turn red when traffic exceeds 20 messages/min.">
              <h2>Message Throughput &mdash; last 60 min</h2>
              <div class="chart-legend">
                <span style="background:#d42d00"></span>normal &nbsp;
                <span style="background:#ed4245"></span>&gt;20 / min
              </div>
            </div>
            <div class="chart-wrap">
              <canvas id="chart"></canvas>
            </div>
          </div>

          <!-- Rate limits section -->
          <div style="display:grid;grid-template-columns:1fr 1fr;gap:12px;margin-top:12px">
            <div class="chart-card">
              <div class="chart-header" title="Requests rejected by the server's rate limiter (auth: 10/min per IP; messages: 30/min per user)">
                <h2>Rate Limit Hits &mdash; last 60 min</h2>
                <div class="chart-legend">
                  <span id="rl-min-badge" style="color:#e89000;font-size:11px"></span>
                  &nbsp;
                  <span id="rl-hour-badge" style="color:#5a544a;font-size:11px"></span>
                </div>
              </div>
              <div class="chart-wrap" style="height:120px">
                <canvas id="rlChart"></canvas>
              </div>
            </div>
            <div class="chart-card">
              <div class="chart-header" title="Users or IP addresses that have triggered the most rate-limit rejections in the last hour"><h2>Top Offenders</h2></div>
              <table class="user-table" style="font-size:11px">
                <thead>
                  <tr>
                    <th>User / IP</th>
                    <th style="width:50px;text-align:right">Hits</th>
                    <th style="width:90px">Last seen</th>
                  </tr>
                </thead>
                <tbody id="rlOffenders">
                  <tr><td colspan="3" style="color:#322e28;padding:10px">No rate limit hits yet.</td></tr>
                </tbody>
              </table>
            </div>
          </div>

        </div><!-- /tab-overview -->

        <!-- ── Users ─────────────────────────────────────── -->
        <div id="tab-users" class="tab-panel">
          <div class="panel-header">
            <h2>Users <span id="userCount" style="color:#4a443d;font-size:11px;font-weight:400;margin-left:6px"></span></h2>
            <div style="display:flex;gap:8px;align-items:center">
              <label style="font-size:11px;color:#5a544a;display:flex;align-items:center;gap:4px;cursor:pointer" title="Show only users currently connected">
                <input type="checkbox" id="onlineOnly" data-change="filterUsers" style="accent-color:#f04010" /> Online only
              </label>
              <select id="roleFilter" data-change="filterUsers" title="Filter by server role"
                style="background:#161414;border:1px solid #292525;border-radius:4px;color:#e9e4d9;font-size:11px;padding:5px 8px;font-family:inherit">
                <option value="">All roles</option>
                <option value="Admin">Admin</option>
                <option value="Member">Member</option>
              </select>
              <input class="search-box" id="userSearch" placeholder="Filter by username…" data-input="filterUsers" title="Type to filter the list by username" />
            </div>
          </div>
          <table class="user-table" id="userTable">
            <thead>
              <tr>
                <th data-click="sort" data-sortkey="username" style="cursor:pointer" title="Sort by username (online users first by default)">Username <span class="sort-ind"></span></th>
                <th title="Text channel they're viewing and voice channel they're in (if any)">Channel</th>
                <th data-click="sort" data-sortkey="role" style="cursor:pointer" title="Sort by role. Hover a badge for permissions; ▲/▼ to promote/demote.">Role <span class="sort-ind"></span></th>
                <th data-click="sort" data-sortkey="created" style="cursor:pointer" title="Sort by join date">Member Since <span class="sort-ind"></span></th>
                <th style="text-align:center" title="Mute (timed), Kick Voice, Kick (rejoinable), or Temp Ban (timed entry block).">Actions</th>
              </tr>
            </thead>
            <tbody id="userTableBody">
              <tr><td colspan="5" style="color:#322e28;padding:20px 10px;">Loading…</td></tr>
            </tbody>
          </table>
        </div><!-- /tab-users -->

        <!-- ── Message Log ───────────────────────────────── -->
        <div id="tab-messages" class="tab-panel">
          <div class="panel-header">
            <h2>Message Log</h2>
            <div style="display:flex;gap:8px;align-items:center">
              <input class="search-box" id="msgAuthor"  placeholder="Author…"  data-input="loadMessages" style="width:120px" title="Filter by the username who sent the message" />
              <input class="search-box" id="msgChannel" placeholder="Channel…" data-input="loadMessages" style="width:120px" title="Filter by channel name (without #)" />
              <input class="search-box" id="msgKeyword" placeholder="Search text…" data-input="loadMessages" style="width:140px" title="Search within message content" />
              <select id="msgSource" data-change="loadMessages" title="Filter by message type: text chat, Whisper voice transcription, or stream event"
                style="background:#161414;border:1px solid #292525;border-radius:4px;color:#e9e4d9;font-size:11px;padding:5px 8px;font-family:inherit;">
                <option value="">All sources</option>
                <option value="Text">Text</option>
                <option value="Voice">Voice</option>
                <option value="Stream">Stream</option>
              </select>
              <select id="msgServer" data-change="loadMessages" title="Filter by server"
                style="background:#161414;border:1px solid #292525;border-radius:4px;color:#e9e4d9;font-size:11px;padding:5px 8px;font-family:inherit;">
                <option value="">All servers</option>
              </select>
              <select id="msgRange" data-change="loadMessages" title="Limit to a recent time window"
                style="background:#161414;border:1px solid #292525;border-radius:4px;color:#e9e4d9;font-size:11px;padding:5px 8px;font-family:inherit;">
                <option value="">Any time</option>
                <option value="1">Last 24h</option>
                <option value="7">Last 7 days</option>
                <option value="30">Last 30 days</option>
              </select>
              <label style="font-size:11px;color:#5a544a;display:flex;align-items:center;gap:5px;cursor:pointer" title="Also show messages that were soft-deleted by an admin — content is retained in the database">
                <input type="checkbox" id="msgShowDeleted" data-change="loadMessages" style="accent-color:#f04010" />
                Show deleted
              </label>
              <button class="btn-sm" id="msgExportCsv" data-click="exportMsgCsv" title="Download the currently shown rows as a CSV file">Export CSV</button>
            </div>
          </div>
          <div id="msgBulkBar" style="display:none;gap:10px;align-items:center;padding:8px 0;border-bottom:1px solid #1a1818;margin-bottom:6px">
            <span id="msgSelCount" style="color:#f04010;font-size:12px">0 selected</span>
            <button class="btn-sm danger" data-click="bulkDelSelected">Delete selected</button>
            <button class="btn-sm" data-click="bulkClearSel">Clear</button>
            <label style="font-size:11px;color:#756e62;display:flex;align-items:center;gap:5px;cursor:pointer" title="Permanently remove from the database (irreversible) instead of hiding">
              <input type="checkbox" id="msgHardDelete" style="accent-color:#ed4245" /> Permanent
            </label>
          </div>
          <div style="padding:0 0 8px 0;display:flex;gap:8px">
            <button class="btn-sm danger" data-click="bulkDelFilter" title="Delete ALL messages matching the current filters (not just the loaded page)">Delete all matching filter</button>
            <button class="btn-sm" data-click="bulkRestoreFilter" title="Restore ALL soft-deleted messages matching the current filters">Restore all matching filter</button>
          </div>
          <table class="user-table">
            <thead>
              <tr>
                <th style="width:28px"><input type="checkbox" id="msgSelectAll" data-change="toggleSelectAll" title="Select all loaded rows" style="accent-color:#f04010" /></th>
                <th style="width:130px">Time</th>
                <th style="width:100px">Author</th>
                <th style="width:90px">Channel</th>
                <th style="width:70px">Server</th>
                <th style="width:60px">Source</th>
                <th>Content</th>
                <th style="width:60px">Action</th>
              </tr>
            </thead>
            <tbody id="msgTableBody">
              <tr><td colspan="8" style="color:#322e28;padding:20px 10px;">Loading…</td></tr>
            </tbody>
          </table>
          <div style="text-align:center;padding:10px">
            <button class="btn-sm" id="msgLoadMore" data-click="loadMoreMsgs" style="display:none">Load more</button>
          </div>
        </div><!-- /tab-messages -->

        <!-- ── Channels ──────────────────────────────────── -->
        <div id="tab-channels" class="tab-panel">
          <div class="panel-header">
            <h2>Channel Permissions</h2>
          </div>
          <div style="display:flex;gap:8px;align-items:center;margin-bottom:14px;flex-wrap:wrap">
            <input id="newChannelName" type="text" placeholder="channel-name" maxlength="64"
              title="Name for the new channel (lowercase, hyphens allowed)"
              style="background:#0c0b0b;border:1px solid #3a3a3a;border-radius:6px;color:#e9e4d9;font-size:13px;padding:7px 12px;font-family:inherit;outline:none;width:200px"
              data-enter="createChannel" />
            <button class="btn-sm" data-click="createChannel" title="Create this channel in the current server">Create Channel</button>
            <span id="createChannelStatus" style="font-size:12px;color:#4a443d"></span>
          </div>
          <table class="user-table">
            <thead>
              <tr>
                <th>Server</th>
                <th>Channel</th>
                <th>Type</th>
                <th title="Users below this role cannot see or read this channel">Min Read Role</th>
                <th title="Users below this role can read but cannot send messages in this channel">Min Write Role</th>
                <th title="Permission changes are saved immediately when you change the dropdown">Actions</th>
              </tr>
            </thead>
            <tbody id="channelTableBody">
              <tr><td colspan="6" style="color:#322e28;padding:20px 10px;">Loading…</td></tr>
            </tbody>
          </table>
        </div><!-- /tab-channels -->

        <!-- ── Word Filter ────────────────────────────────── -->
        <div id="tab-wordfilter" class="tab-panel">
          <div class="panel-header">
            <h2>Word / Phrase Filter</h2>
            <div style="display:flex;gap:8px;align-items:center">
              <input class="search-box" id="wfNewPattern" placeholder="New pattern…" style="width:180px"
                data-enter="addWf"
                title="Enter a word or phrase to block. Whole-word matching is used (&#34;ass&#34; won't match &#34;assassination&#34;). Leet-speak variants (e.g. b4d → bad) are also caught." />
              <button class="btn-sm" data-click="addWf" title="Add this word or phrase to the filter list (press Enter in the text box to do the same)">Add</button>
            </div>
          </div>
          <p style="font-size:11px;color:#4a443d;margin-bottom:12px">
            New patterns use <strong style="color:#756e62">Delete</strong> severity — the message is blocked and the sender sees an error. Admins bypass the filter. The API supports <em>Log</em> (replace with ***) and <em>Mute</em> (block + auto-mute 10 min) severities.
          </p>
          <table class="user-table" id="wfTable">
            <thead>
              <tr>
                <th>Pattern</th>
                <th style="width:160px">Added</th>
                <th style="width:70px">Action</th>
              </tr>
            </thead>
            <tbody id="wfTableBody">
              <tr><td colspan="3" style="color:#322e28;padding:20px 10px;">Loading…</td></tr>
            </tbody>
          </table>
        </div><!-- /tab-wordfilter -->

        <!-- ── Audit Log ──────────────────────────────────── -->
        <div id="tab-audit" class="tab-panel">
          <div class="panel-header">
            <h2>Audit Log</h2>
            <div style="display:flex;gap:8px;align-items:center">
              <select id="auditActionFilter" data-change="loadAudit" title="Filter the audit log to a specific type of moderation action"
                style="background:#161414;border:1px solid #292525;border-radius:4px;color:#e9e4d9;font-size:11px;padding:5px 8px;font-family:inherit;">
                <option value="">All actions</option>
                <option value="RoleChanged">Role Changed</option>
                <option value="MemberKicked">Member Kicked</option>
                <option value="UserMuted">User Muted</option>
                <option value="UserUnmuted">User Unmuted</option>
                <option value="UserTempBanned">User Temp Banned</option>
                <option value="MessageDeleted">Message Deleted</option>
                <option value="ChannelCreated">Channel Created</option>
                <option value="ChannelDeleted">Channel Deleted</option>
                <option value="ChannelPermissionsChanged">Channel Permissions</option>
              </select>
            </div>
          </div>
          <table class="user-table">
            <thead>
              <tr>
                <th style="width:130px">Time</th>
                <th style="width:100px">Actor</th>
                <th style="width:130px">Action</th>
                <th style="width:100px">Target</th>
                <th>Detail</th>
              </tr>
            </thead>
            <tbody id="auditTableBody">
              <tr><td colspan="5" style="color:#322e28;padding:20px 10px;">Loading…</td></tr>
            </tbody>
          </table>
        </div><!-- /tab-audit -->

        <div class="status-bar">
          <span id="serverUrl" title="The address this server is currently listening on">http://localhost:5238</span>
          <span id="refreshNote" title="Overview and rate-limit charts update every 2–5 s automatically; open tabs refresh every 5 s">auto-refreshes every 2s</span>
          <span id="appVersion" title="Running build" style="color:#4a443d">{{VERSION}}</span>
        </div>

        <script src="/dashboard.js"></script>
        </body>
        </html>
        """;
}
