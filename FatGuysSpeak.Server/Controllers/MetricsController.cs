using FatGuysSpeak.Server.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace FatGuysSpeak.Server.Controllers;

[ApiController]
[Authorize(Policy = "DashboardAdmin")]
public class MetricsController(ServerMetricsService metrics) : ControllerBase
{
    [HttpGet("/api/metrics")]
    public IActionResult GetMetrics() => Ok(metrics.GetSnapshot());

    [HttpGet("/dashboard")]
    public ContentResult Dashboard() => Content(Html, "text/html");

    private const string Html = """
        <!DOCTYPE html>
        <html lang="en">
        <head>
        <meta charset="UTF-8">
        <meta name="viewport" content="width=device-width, initial-scale=1.0">
        <title>FatGuysSpeak — Server Dashboard</title>
        <script src="https://cdn.jsdelivr.net/npm/chart.js@4.4.0/dist/chart.umd.min.js"></script>
        <style>
          * { margin: 0; padding: 0; box-sizing: border-box; }
          body { background: #1a1a1a; color: #d0d0d0; font-family: 'Segoe UI', system-ui, sans-serif; padding: 20px 24px; }

          header {
            display: flex; justify-content: space-between; align-items: center;
            margin-bottom: 16px; padding-bottom: 14px;
            border-bottom: 1px solid #2a2a2a;
          }
          header h1 { font-size: 16px; color: #8ab4d4; font-weight: 600; letter-spacing: 0.3px; }

          .live-badge { display: flex; align-items: center; gap: 7px; }
          .live-dot {
            width: 8px; height: 8px; border-radius: 50%; background: #44bb44;
            animation: pulse 2s ease-in-out infinite;
          }
          @keyframes pulse { 0%,100% { opacity:1; box-shadow:0 0 0 0 rgba(68,187,68,.4); }
                             50%  { opacity:.6; box-shadow:0 0 0 5px rgba(68,187,68,0); } }
          .live-text { font-size: 11px; color: #666; }

          /* ── Tabs ── */
          .tabs { display: flex; gap: 2px; margin-bottom: 16px; border-bottom: 1px solid #2e2e2e; }
          .tab-btn {
            background: none; border: none; color: #666; font-size: 12px; font-family: inherit;
            padding: 8px 18px; cursor: pointer; border-bottom: 2px solid transparent;
            margin-bottom: -1px; transition: color .15s, border-color .15s;
          }
          .tab-btn:hover { color: #aaa; }
          .tab-btn.active { color: #8ab4d4; border-bottom-color: #8ab4d4; }
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
            background: #252525; border: 1px solid #2e2e2e; border-radius: 8px;
            padding: 16px 18px; position: relative; overflow: hidden;
            transition: border-color .3s;
          }
          .card.flash { border-color: #2d5f9e; }
          .card .icon { font-size: 18px; margin-bottom: 10px; display: block; }
          .card .value {
            font-size: 30px; font-weight: 700; color: #ffffff; line-height: 1;
            margin-bottom: 6px; transition: color .4s;
          }
          .card.flash .value { color: #8ab4d4; }
          .card .label { font-size: 10px; color: #555; text-transform: uppercase; letter-spacing: .8px; }

          .chart-card {
            background: #252525; border: 1px solid #2e2e2e; border-radius: 8px;
            padding: 16px 18px;
          }
          .chart-header {
            display: flex; justify-content: space-between; align-items: center;
            margin-bottom: 12px;
          }
          .chart-header h2 { font-size: 12px; color: #666; font-weight: 500; text-transform: uppercase; letter-spacing: .6px; }
          .chart-legend { font-size: 10px; color: #444; }
          .chart-legend span { display: inline-block; width: 8px; height: 8px; border-radius: 2px; margin-right: 4px; vertical-align: middle; }
          .chart-wrap { height: 170px; }

          /* ── Users tab ── */
          .panel-header {
            display: flex; justify-content: space-between; align-items: center;
            margin-bottom: 12px;
          }
          .panel-header h2 { font-size: 12px; color: #666; font-weight: 500; text-transform: uppercase; letter-spacing: .6px; }
          .search-box {
            background: #252525; border: 1px solid #333; border-radius: 4px;
            color: #d0d0d0; font-size: 11px; padding: 5px 10px; font-family: inherit;
            width: 200px;
          }
          .search-box:focus { outline: none; border-color: #8ab4d4; }

          .user-table { width: 100%; border-collapse: collapse; font-size: 12px; }
          .user-table th {
            text-align: left; padding: 7px 10px; color: #555;
            font-size: 10px; text-transform: uppercase; letter-spacing: .6px;
            border-bottom: 1px solid #2e2e2e; font-weight: 500;
          }
          .user-table td {
            padding: 8px 10px; border-bottom: 1px solid #222;
            vertical-align: middle;
          }
          .user-table tr:hover td { background: #222; }
          .badge {
            display: inline-block; padding: 2px 7px; border-radius: 10px;
            font-size: 10px; font-weight: 500;
          }
          .badge-online  { background: #1a3a1a; color: #44bb44; }
          .badge-offline { background: #2a2a2a; color: #555; }
          .badge-voice   { background: #1a2a3a; color: #8ab4d4; }
          .badge-away    { background: #3a2a0a; color: #f0a030; }
          .badge-dnd     { background: #3a1010; color: #ed4245; }

          .btn-sm {
            background: #2d2d2d; border: 1px solid #3a3a3a; color: #aaa;
            font-size: 10px; padding: 3px 9px; border-radius: 3px; cursor: pointer;
            font-family: inherit; transition: background .15s;
          }
          .btn-sm:hover { background: #3a3a3a; }
          .btn-sm.danger { border-color: #5a2020; color: #ed4245; }
          .btn-sm.danger:hover { background: #3a1515; }
          .btn-sm:disabled { opacity: .4; cursor: default; }

          .status-bar {
            display: flex; justify-content: space-between; align-items: center;
            margin-top: 12px; font-size: 10px; color: #444;
          }

          /* ── Role tooltip ── */
          .role-tip { position: relative; }
          .role-tip::after {
            content: attr(data-tip);
            position: absolute;
            bottom: calc(100% + 8px);
            left: 50%;
            transform: translateX(-50%);
            background: #141420;
            border: 1px solid #3a3a5a;
            border-radius: 6px;
            padding: 9px 13px;
            font-size: 11px;
            line-height: 1.75;
            color: #c8c8d8;
            white-space: pre;
            pointer-events: none;
            opacity: 0;
            transition: opacity .15s ease;
            z-index: 9999;
            box-shadow: 0 6px 20px rgba(0,0,0,.75);
            font-weight: normal;
          }
          .role-tip:hover::after { opacity: 1; }
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

        <div class="tabs">
          <button class="tab-btn active" onclick="showTab('overview')" title="Server health metrics, message throughput, and rate-limit overview">Overview</button>
          <button class="tab-btn" onclick="showTab('users')" title="View all users — change roles, mute, kick, or temporarily ban">Users</button>
          <button class="tab-btn" onclick="showTab('messages')" title="Search and moderate all server messages">Message Log</button>
          <button class="tab-btn" onclick="showTab('channels')" title="Configure the minimum role required to read or write in each channel">Channels</button>
          <button class="tab-btn" onclick="showTab('wordfilter')" title="Manage blocked words and phrases — Members who trigger the filter have their message blocked or the word replaced">Word Filter</button>
          <button class="tab-btn" onclick="showTab('audit')" title="Track every moderation action taken by admins and moderators">Audit Log</button>
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
                <span style="background:#2d5f9e"></span>normal &nbsp;
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
                  <span id="rl-min-badge" style="color:#f0a030;font-size:11px"></span>
                  &nbsp;
                  <span id="rl-hour-badge" style="color:#666;font-size:11px"></span>
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
                  <tr><td colspan="3" style="color:#444;padding:10px">No rate limit hits yet.</td></tr>
                </tbody>
              </table>
            </div>
          </div>

        </div><!-- /tab-overview -->

        <!-- ── Users ─────────────────────────────────────── -->
        <div id="tab-users" class="tab-panel">
          <div class="panel-header">
            <h2>Connected Clients</h2>
            <input class="search-box" id="userSearch" placeholder="Filter by username…" oninput="filterUsers()" title="Type to filter the list by username" />
          </div>
          <table class="user-table" id="userTable">
            <thead>
              <tr>
                <th>Username</th>
                <th title="Current presence status and whether the user is in a voice channel">Status</th>
                <th title="Server role — hover a badge for a full list of permissions. Use ▲/▼ to promote or demote.">Role</th>
                <th title="Active mute prevents the user from sending messages. Admins cannot be muted.">Mute</th>
                <th title="Whether the user has an active SignalR connection to this server">Connection</th>
                <th>Member Since</th>
                <th title="Kick Voice: disconnect from voice only. Kick: remove from server (rejoinable). Temp Ban: block entry for a chosen duration.">Actions</th>
              </tr>
            </thead>
            <tbody id="userTableBody">
              <tr><td colspan="6" style="color:#444;padding:20px 10px;">Loading…</td></tr>
            </tbody>
          </table>
        </div><!-- /tab-users -->

        <!-- ── Message Log ───────────────────────────────── -->
        <div id="tab-messages" class="tab-panel">
          <div class="panel-header">
            <h2>Message Log</h2>
            <div style="display:flex;gap:8px;align-items:center">
              <input class="search-box" id="msgAuthor"  placeholder="Author…"  oninput="loadMessages()" style="width:120px" title="Filter by the username who sent the message" />
              <input class="search-box" id="msgChannel" placeholder="Channel…" oninput="loadMessages()" style="width:120px" title="Filter by channel name (without #)" />
              <select id="msgSource" onchange="loadMessages()" title="Filter by message type: text chat, Whisper voice transcription, or stream event"
                style="background:#252525;border:1px solid #333;border-radius:4px;color:#d0d0d0;font-size:11px;padding:5px 8px;font-family:inherit;">
                <option value="">All sources</option>
                <option value="Text">Text</option>
                <option value="Voice">Voice</option>
                <option value="Stream">Stream</option>
              </select>
              <label style="font-size:11px;color:#666;display:flex;align-items:center;gap:5px;cursor:pointer" title="Also show messages that were soft-deleted by an admin — content is retained in the database">
                <input type="checkbox" id="msgShowDeleted" onchange="loadMessages()" style="accent-color:#8ab4d4" />
                Show deleted
              </label>
            </div>
          </div>
          <table class="user-table">
            <thead>
              <tr>
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
              <tr><td colspan="7" style="color:#444;padding:20px 10px;">Loading…</td></tr>
            </tbody>
          </table>
        </div><!-- /tab-messages -->

        <!-- ── Channels ──────────────────────────────────── -->
        <div id="tab-channels" class="tab-panel">
          <div class="panel-header">
            <h2>Channel Permissions</h2>
          </div>
          <div style="display:flex;gap:8px;align-items:center;margin-bottom:14px;flex-wrap:wrap">
            <input id="newChannelName" type="text" placeholder="channel-name" maxlength="64"
              title="Name for the new channel (lowercase, hyphens allowed)"
              style="background:#1a1a1a;border:1px solid #3a3a3a;border-radius:6px;color:#d0d0d0;font-size:13px;padding:7px 12px;font-family:inherit;outline:none;width:200px"
              onkeydown="if(event.key==='Enter')createChannel()" />
            <button class="btn-sm" onclick="createChannel()" title="Create this channel in the current server">Create Channel</button>
            <span id="createChannelStatus" style="font-size:12px;color:#555"></span>
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
              <tr><td colspan="6" style="color:#444;padding:20px 10px;">Loading…</td></tr>
            </tbody>
          </table>
        </div><!-- /tab-channels -->

        <!-- ── Word Filter ────────────────────────────────── -->
        <div id="tab-wordfilter" class="tab-panel">
          <div class="panel-header">
            <h2>Word / Phrase Filter</h2>
            <div style="display:flex;gap:8px;align-items:center">
              <input class="search-box" id="wfNewPattern" placeholder="New pattern…" style="width:180px"
                onkeydown="if(event.key==='Enter')addWordFilter()"
                title="Enter a word or phrase to block. Whole-word matching is used (&#34;ass&#34; won't match &#34;assassination&#34;). Leet-speak variants (e.g. b4d → bad) are also caught." />
              <button class="btn-sm" onclick="addWordFilter()" title="Add this word or phrase to the filter list (press Enter in the text box to do the same)">Add</button>
            </div>
          </div>
          <p style="font-size:11px;color:#555;margin-bottom:12px">
            New patterns use <strong style="color:#888">Delete</strong> severity — the message is blocked and the sender sees an error. Moderators and Admins bypass the filter. The API supports <em>Log</em> (replace with ***) and <em>Mute</em> (block + auto-mute 10 min) severities.
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
              <tr><td colspan="3" style="color:#444;padding:20px 10px;">Loading…</td></tr>
            </tbody>
          </table>
        </div><!-- /tab-wordfilter -->

        <!-- ── Audit Log ──────────────────────────────────── -->
        <div id="tab-audit" class="tab-panel">
          <div class="panel-header">
            <h2>Audit Log</h2>
            <div style="display:flex;gap:8px;align-items:center">
              <select id="auditActionFilter" onchange="loadAudit()" title="Filter the audit log to a specific type of moderation action"
                style="background:#252525;border:1px solid #333;border-radius:4px;color:#d0d0d0;font-size:11px;padding:5px 8px;font-family:inherit;">
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
              <tr><td colspan="5" style="color:#444;padding:20px 10px;">Loading…</td></tr>
            </tbody>
          </table>
        </div><!-- /tab-audit -->

        <div class="status-bar">
          <span id="serverUrl" title="The address this server is currently listening on">http://localhost:5238</span>
          <span id="refreshNote" title="Overview and rate-limit charts update every 2–5 s automatically; open tabs refresh every 5 s">auto-refreshes every 2s</span>
        </div>

        <script>
        function escapeHtml(s) {
          return String(s ?? '').replace(/&/g,'&amp;').replace(/</g,'&lt;').replace(/>/g,'&gt;').replace(/"/g,'&quot;').replace(/'/g,'&#39;');
        }
        // ── Tab switching ──────────────────────────────────
        function showTab(name) {
          document.querySelectorAll('.tab-panel').forEach(p => p.classList.remove('active'));
          document.querySelectorAll('.tab-btn').forEach(b => b.classList.remove('active'));
          document.getElementById('tab-' + name).classList.add('active');
          event.currentTarget.classList.add('active');
          if (name === 'users')      { loadUsers(); }
          if (name === 'messages')   { loadMessages(); }
          if (name === 'channels')   { loadChannels(); }
          if (name === 'wordfilter') { loadWordFilters(); }
          if (name === 'audit')      { loadAudit(); }
        }

        // ── Overview ──────────────────────────────────────
        const xLabels = Array.from({length:60}, (_,i) => {
          const age = 59 - i;
          if (age === 0)  return 'now';
          if (age % 10 === 0) return age + 'm';
          return '';
        });

        const throughput = new Chart(document.getElementById('chart'), {
          type: 'bar',
          data: {
            labels: xLabels,
            datasets: [{
              data: new Array(60).fill(0),
              backgroundColor: (ctx) => ctx.parsed.y > 20 ? '#ed4245' : '#2d5f9e',
              borderRadius: 2,
              borderSkipped: false,
              maxBarThickness: 14,
            }]
          },
          options: {
            responsive: true,
            maintainAspectRatio: false,
            animation: false,
            plugins: {
              legend: { display: false },
              tooltip: {
                backgroundColor: '#1e1e2e',
                titleColor: '#8ab4d4',
                bodyColor: '#d0d0d0',
                borderColor: '#333',
                borderWidth: 1,
                callbacks: {
                  title: (items) => {
                    const age = 59 - items[0].dataIndex;
                    return age === 0 ? 'This minute' : `${age} min ago`;
                  },
                  label: (ctx) => ` ${ctx.parsed.y} message${ctx.parsed.y !== 1 ? 's' : ''}`,
                }
              }
            },
            scales: {
              x: { grid: { color: '#222' }, ticks: { color: '#444', font: { size: 10 }, maxRotation: 0 } },
              y: { grid: { color: '#222' }, ticks: { color: '#444', font: { size: 10 }, stepSize: 1 }, beginAtZero: true }
            }
          }
        });

        const prev = {};
        function setCard(cardId, elId, rawVal, display) {
          const card = document.getElementById(cardId);
          const el   = document.getElementById(elId);
          if (!card || !el) return;
          if (prev[elId] !== undefined && prev[elId] !== String(rawVal)) {
            card.classList.add('flash');
            setTimeout(() => card.classList.remove('flash'), 700);
          }
          el.textContent = display ?? rawVal;
          prev[elId] = String(rawVal);
        }

        async function refreshMetrics() {
          try {
            const res = await fetch('/api/metrics');
            if (!res.ok) throw new Error(res.status);
            const d = await res.json();

            setCard('c-online',  'onlineUsers',       d.onlineUsers,        d.onlineUsers);
            setCard('c-voice',   'voiceParticipants', d.voiceParticipants,  d.voiceParticipants);
            setCard('c-streams', 'activeStreams',      d.activeStreams,       d.activeStreams);
            setCard('c-msgsmin', 'msgsPerMin',         d.messagesLastMinute, d.messagesLastMinute);
            setCard('c-total',   'totalMsgs',          d.totalMessages,      d.totalMessages.toLocaleString());
            setCard('c-mem',     'memoryMb',           d.memoryMb,           d.memoryMb + ' MB');
            setCard('c-cpu',     'cpuPct',             d.cpuPercent,         d.cpuPercent.toFixed(1) + '%');
            setCard('c-uptime',  'uptime',             d.uptimeFormatted,    d.uptimeFormatted);

            throughput.data.datasets[0].data = [...d.messageHistory].reverse();
            throughput.update('none');

            document.getElementById('lastUpdated').textContent = 'updated ' + new Date().toLocaleTimeString();
            document.getElementById('liveDot').style.background = '#44bb44';
            document.getElementById('serverUrl').textContent = window.location.origin;
          } catch {
            document.getElementById('lastUpdated').textContent = 'connection lost — retrying…';
            document.getElementById('liveDot').style.background = '#ed4245';
          }
        }

        // ── Users tab ─────────────────────────────────────
        let allUsers = [];
        let serverMembers = [];
        let currentServerId = null;
        let serverName = '';

        function statusBadge(u) {
          if (u.voiceChannelId !== null) return '<span class="badge badge-voice">🎙 In Voice</span>';
          if (!u.isOnline) return '<span class="badge badge-offline">Offline</span>';
          const s = u.status.toLowerCase();
          if (s === 'away') return '<span class="badge badge-away">Away</span>';
          if (s === 'donotdisturb') return '<span class="badge badge-dnd">DnD</span>';
          return '<span class="badge badge-online">Online</span>';
        }

        function connBadge(u) {
          return u.isOnline
            ? '<span class="badge badge-online">Connected</span>'
            : '<span class="badge badge-offline">Offline</span>';
        }

        const ROLE_STR = { 0: 'Member', 1: 'Moderator', 2: 'Admin' };
        const ROLE_NUM = { Member: 0, Moderator: 1, Admin: 2 };
        function toRoleStr(r) { return typeof r === 'string' ? r : (ROLE_STR[r] ?? 'Member'); }

        const ROLE_TIPS = {
          Member:    'Member\n• Send and read messages\n• Join voice channels\n• React to messages',
          Moderator: 'Moderator\n• Everything a Member can do\n• Delete any message in the server\n• Access moderator-restricted channels',
          Admin:     'Admin\n• Everything a Moderator can do\n• Create and delete channels\n• Set per-channel read/write permissions\n• Promote or demote members\n• Kick members from the server',
        };

        function roleBadge(role) {
          const map = { Admin: '#8ab4d4', Moderator: '#44bb44', Member: '#555' };
          const col = map[role] || '#555';
          const tip = (ROLE_TIPS[role] || role || '').replace(/&/g, '&amp;').replace(/"/g, '&quot;');
          return `<span class="badge role-tip" style="background:${col}22;color:${col};cursor:help" data-tip="${tip}">${role ?? '—'}</span>`;
        }

        function renderUsers(users) {
          const tbody = document.getElementById('userTableBody');
          if (!users.length) {
            tbody.innerHTML = '<tr><td colspan="7" style="color:#444;padding:20px 10px;">No users found.</td></tr>';
            return;
          }
          const now = Date.now();
          tbody.innerHTML = users.map(u => {
            const member = serverMembers.find(m => m.userId === u.id);
            const role = member?.role ?? null;
            const nextUp   = role === 'Member' ? 'Moderator' : 'Admin';
            const nextDown = role === 'Admin'  ? 'Moderator' : 'Member';
            const roleCell = role
              ? `${roleBadge(role)}
                 <button class="btn-sm" style="margin-left:4px" title="Promote to ${nextUp}" onclick="promoteUser(${u.id},this)" ${role==='Admin'?'disabled':''} aria-label="Promote to ${nextUp}">▲</button>
                 <button class="btn-sm danger" title="Demote to ${nextDown}" onclick="demoteUser(${u.id},this)" ${role==='Member'?'disabled':''} aria-label="Demote to ${nextDown}">▼</button>`
              : '<span style="color:#555">—</span>';
            const mutedUntil = member?.mutedUntil ? new Date(member.mutedUntil) : null;
            const isMuted = mutedUntil && mutedUntil.getTime() > now;
            const muteCell = member
              ? isMuted
                ? `<span style="color:#f0a030;font-size:10px">until ${mutedUntil.toLocaleTimeString()}</span>
                   <button class="btn-sm" style="margin-left:4px" title="Remove the active mute — user can send messages immediately" onclick="muteUser(${currentServerId},${u.id},0,this)">Unmute</button>`
                : `<button class="btn-sm" title="Temporarily prevent this user from sending messages" onclick="pickMute(${currentServerId},${u.id},this)" ${role==='Admin'?'disabled':''}>Mute…</button>`
              : '<span style="color:#555">—</span>';
            return `<tr>
              <td><strong style="color:#d0d0d0">${escapeHtml(u.username)}</strong></td>
              <td>${statusBadge(u)}</td>
              <td>${roleCell}</td>
              <td>${muteCell}</td>
              <td>${connBadge(u)}</td>
              <td style="color:#555">${new Date(u.createdAt).toLocaleDateString()}</td>
              <td>
                ${u.voiceChannelId !== null
                  ? `<button class="btn-sm danger" title="Disconnect this user from their current voice channel (they can rejoin)" onclick="kickVoice(${u.id}, this)">Kick Voice</button>`
                  : `<button class="btn-sm" title="User is not in a voice channel" disabled>Kick Voice</button>`}
                ${member && role !== 'Admin'
                  ? ` <button class="btn-sm danger" title="Remove this user from the server — they can rejoin via invite link" onclick="kickFromServer(${currentServerId},${u.id},this)">Kick</button>
                      <button class="btn-sm danger" title="Block this user from rejoining the server for a chosen duration" onclick="pickTempBan(${currentServerId},${u.id},this)">Temp Ban…</button>`
                  : ''}
              </td>
            </tr>`;
          }).join('');
        }

        function filterUsers() {
          const q = document.getElementById('userSearch').value.toLowerCase();
          renderUsers(q ? allUsers.filter(u => u.username.toLowerCase().includes(q)) : allUsers);
        }

        async function loadUsers() {
          try {
            const [usersRes, membersRes] = await Promise.all([
              fetch('/api/admin/users'),
              currentServerId ? fetch(`/api/admin/servers/${currentServerId}/members`) : Promise.resolve(null),
            ]);
            if (!usersRes.ok) throw new Error(usersRes.status);
            allUsers = await usersRes.json();
            serverMembers = [];
            if (membersRes?.ok) {
              const data = await membersRes.json();
              serverMembers = data.map(m => ({ ...m, role: toRoleStr(m.role) }));
            }
            filterUsers();
          } catch (e) {
            document.getElementById('userTableBody').innerHTML =
              `<tr><td colspan="7" style="color:#ed4245;padding:20px 10px;">Failed to load users: ${e.message}</td></tr>`;
          }
        }

        async function loadServerMembers() {
          if (!currentServerId) return;
          try {
            const res = await fetch(`/api/admin/servers/${currentServerId}/members`);
            if (res.ok) {
              const data = await res.json();
              serverMembers = data.map(m => ({ ...m, role: toRoleStr(m.role) }));
            }
          } catch {}
          filterUsers();
        }

        async function pickMute(serverId, userId, btn) {
          const options = ['5 minutes','30 minutes','1 hour','24 hours'];
          const choice = prompt('Mute duration:\n1: 5 minutes\n2: 30 minutes\n3: 1 hour\n4: 24 hours\n\nEnter 1–4:');
          const secs = [300, 1800, 3600, 86400][parseInt(choice) - 1];
          if (!secs) return;
          await muteUser(serverId, userId, secs, btn);
        }

        async function muteUser(serverId, userId, seconds, btn) {
          btn.disabled = true;
          try {
            const res = await fetch(`/api/admin/servers/${serverId}/members/${userId}/mute`, {
              method: 'PUT',
              headers: { 'Content-Type': 'application/json' },
              body: JSON.stringify({ seconds })
            });
            if (!res.ok) throw new Error(await res.text() || res.status);
            await loadServerMembers();
          } catch (e) {
            btn.disabled = false;
            alert('Mute failed: ' + e.message);
          }
        }

        async function pickTempBan(serverId, userId, btn) {
          const choice = prompt('Temp ban duration:\n1: 1 hour\n2: 24 hours\n3: 7 days\n4: 30 days\n\nEnter 1–4:');
          const secs = [3600, 86400, 604800, 2592000][parseInt(choice) - 1];
          if (!secs) return;
          if (!confirm(`Temp ban this user for ${['1 hour','24 hours','7 days','30 days'][parseInt(choice)-1]}?`)) return;
          await tempBanUser(serverId, userId, secs, btn);
        }

        async function tempBanUser(serverId, userId, seconds, btn) {
          btn.disabled = true;
          try {
            const res = await fetch(`/api/admin/servers/${serverId}/members/${userId}/tempban`, {
              method: 'PUT',
              headers: { 'Content-Type': 'application/json' },
              body: JSON.stringify({ seconds })
            });
            if (!res.ok) throw new Error(await res.text() || res.status);
            await loadServerMembers();
            await loadUsers();
          } catch (e) {
            btn.disabled = false;
            alert('Temp ban failed: ' + e.message);
          }
        }

        async function kickVoice(userId, btn) {
          btn.disabled = true;
          btn.textContent = 'Kicking…';
          try {
            const res = await fetch(`/api/admin/users/${userId}/kick-voice`, { method: 'POST' });
            if (!res.ok) throw new Error(res.status);
            btn.textContent = 'Kicked';
            setTimeout(() => loadUsers(), 1500);
          } catch (e) {
            btn.disabled = false;
            btn.textContent = 'Kick Voice';
            alert('Failed: ' + e.message);
          }
        }

        async function kickFromServer(serverId, userId, btn) {
          if (!confirm('Kick this member from the server?')) return;
          btn.disabled = true;
          try {
            const res = await fetch(`/api/admin/servers/${serverId}/members/${userId}`, { method: 'DELETE' });
            if (!res.ok) throw new Error(await res.text() || res.status);
            setTimeout(() => { loadUsers(); loadServerMembers(); }, 1000);
          } catch (e) {
            btn.disabled = false;
            alert('Kick failed: ' + e.message);
          }
        }

        async function promoteUser(userId, btn) {
          if (!currentServerId) return;
          const member = serverMembers.find(m => m.userId === userId);
          if (!member) return;
          const next = member.role === 'Member' ? 'Moderator' : 'Admin';
          await setRole(userId, next, btn);
        }

        async function demoteUser(userId, btn) {
          if (!currentServerId) return;
          const member = serverMembers.find(m => m.userId === userId);
          if (!member) return;
          const prev = member.role === 'Admin' ? 'Moderator' : 'Member';
          await setRole(userId, prev, btn);
        }

        async function setRole(userId, role, btn) {
          btn.disabled = true;
          try {
            const res = await fetch(`/api/admin/servers/${currentServerId}/members/${userId}/role`, {
              method: 'PUT',
              headers: { 'Content-Type': 'application/json' },
              body: JSON.stringify({ role: ROLE_NUM[role] ?? 0 })
            });
            if (!res.ok) throw new Error(await res.text() || res.status);
            await loadServerMembers();
          } catch (e) {
            btn.disabled = false;
            alert('Role change failed: ' + e.message);
          }
        }

        // ── Rate Limits ───────────────────────────────────
        const rlChart = new Chart(document.getElementById('rlChart'), {
          type: 'bar',
          data: {
            labels: xLabels,
            datasets: [{
              data: new Array(60).fill(0),
              backgroundColor: '#f0a03066',
              borderColor: '#f0a030',
              borderWidth: 1,
              borderRadius: 2,
              borderSkipped: false,
              maxBarThickness: 14,
            }]
          },
          options: {
            responsive: true,
            maintainAspectRatio: false,
            animation: false,
            plugins: {
              legend: { display: false },
              tooltip: {
                backgroundColor: '#1e1e2e',
                titleColor: '#f0a030',
                bodyColor: '#d0d0d0',
                borderColor: '#333',
                borderWidth: 1,
                callbacks: {
                  title: (items) => {
                    const age = 59 - items[0].dataIndex;
                    return age === 0 ? 'This minute' : `${age} min ago`;
                  },
                  label: (ctx) => ` ${ctx.parsed.y} hit${ctx.parsed.y !== 1 ? 's' : ''}`,
                }
              }
            },
            scales: {
              x: { grid: { color: '#222' }, ticks: { color: '#444', font: { size: 9 }, maxRotation: 0 } },
              y: { grid: { color: '#222' }, ticks: { color: '#444', font: { size: 9 }, stepSize: 1 }, beginAtZero: true }
            }
          }
        });

        async function refreshRateLimits() {
          try {
            const res = await fetch('/api/admin/rate-limits');
            if (!res.ok) return;
            const d = await res.json();

            document.getElementById('rl-min-badge').textContent  = d.hitsLastMinute  + ' hits/min';
            document.getElementById('rl-hour-badge').textContent = d.hitsLastHour + ' hits/hr';

            rlChart.data.datasets[0].data = [...d.hitHistory].reverse();
            rlChart.update('none');

            const tbody = document.getElementById('rlOffenders');
            if (!d.topOffenders.length) {
              tbody.innerHTML = '<tr><td colspan="3" style="color:#444;padding:10px">No rate limit hits yet.</td></tr>';
            } else {
              tbody.innerHTML = d.topOffenders.map(o => {
                const last = new Date(o.lastSeen).toLocaleTimeString();
                return `<tr>
                  <td style="color:#d0d0d0">${escapeHtml(o.who)}</td>
                  <td style="text-align:right;color:#f0a030;font-weight:600">${o.hits}</td>
                  <td style="color:#555">${last}</td>
                </tr>`;
              }).join('');
            }
          } catch {}
        }

        // ── Message Log ───────────────────────────────────
        let msgDebounce = null;

        async function loadMessages() {
          clearTimeout(msgDebounce);
          msgDebounce = setTimeout(async () => {
            const author  = document.getElementById('msgAuthor').value.trim();
            const channel = document.getElementById('msgChannel').value.trim();
            const source  = document.getElementById('msgSource').value;
            const showDel = document.getElementById('msgShowDeleted').checked;

            const params = new URLSearchParams({ limit: 200 });
            if (author)  params.set('author',  author);
            if (channel) params.set('channel', channel);
            if (source)  params.set('source',  source);

            try {
              const res = await fetch('/api/admin/messages?' + params);
              if (!res.ok) throw new Error(res.status);
              let msgs = await res.json();
              if (!showDel) msgs = msgs.filter(m => !m.isDeleted);
              renderMessages(msgs);
            } catch (e) {
              document.getElementById('msgTableBody').innerHTML =
                `<tr><td colspan="7" style="color:#ed4245;padding:20px 10px;">Failed: ${e.message}</td></tr>`;
            }
          }, 250);
        }

        function sourceBadge(s) {
          const map = { Text: '#2d5f9e', Voice: '#3a6a3a', Stream: '#5a2d8e' };
          const col = map[s] || '#333';
          return `<span class="badge" style="background:${col}20;color:${col === '#333' ? '#888' : col};border:1px solid ${col}40">${s}</span>`;
        }

        function renderMessages(msgs) {
          const tbody = document.getElementById('msgTableBody');
          if (!msgs.length) {
            tbody.innerHTML = '<tr><td colspan="7" style="color:#444;padding:20px 10px;">No messages found.</td></tr>';
            return;
          }
          tbody.innerHTML = msgs.map(m => {
            const ts = new Date(m.createdAt);
            const timeStr = ts.toLocaleDateString() + ' ' + ts.toLocaleTimeString();
            const deleted = m.isDeleted;
            const contentStyle = deleted ? 'color:#555;font-style:italic' : 'color:#c0c0c0';
            const content = (m.content || '').length > 120 ? m.content.slice(0, 120) + '…' : m.content;
            const escapedContent = content.replace(/</g,'&lt;').replace(/>/g,'&gt;');
            return `<tr style="${deleted ? 'opacity:.55' : ''}">
              <td style="color:#555;font-size:11px;white-space:nowrap">${timeStr}</td>
              <td style="color:#8ab4d4;font-weight:500">${escapeHtml(m.author)}</td>
              <td style="color:#666">#${escapeHtml(m.channel)}</td>
              <td style="color:#555;font-size:11px">${escapeHtml(m.server)}</td>
              <td>${sourceBadge(m.source)}</td>
              <td style="${contentStyle}">${escapedContent}</td>
              <td>${deleted
                ? '<span style="color:#444;font-size:10px">Deleted</span>'
                : `<button class="btn-sm danger" title="Soft-delete this message — it will be hidden from clients but remains in the database" onclick="adminDeleteMsg(${m.id},this)">Delete</button>`}</td>
            </tr>`;
          }).join('');
        }

        async function adminDeleteMsg(msgId, btn) {
          if (!confirm('Delete this message? It will be marked deleted for all users.')) return;
          btn.disabled = true;
          btn.textContent = '…';
          try {
            const res = await fetch(`/api/admin/messages/${msgId}`, { method: 'DELETE' });
            if (!res.ok) throw new Error(res.status);
            loadMessages();
          } catch (e) {
            btn.disabled = false;
            btn.textContent = 'Delete';
            alert('Failed: ' + e.message);
          }
        }

        // ── Server initialisation ─────────────────────────
        async function initServer() {
          try {
            const res = await fetch('/api/admin/servers');
            if (!res.ok) return;
            const servers = await res.json();
            if (!servers.length) return;
            currentServerId = servers[0].id;
            serverName = servers[0].name;
          } catch {}
        }

        // ── Audit Log ─────────────────────────────────────
        async function loadAudit() {
          const action = document.getElementById('auditActionFilter').value;
          const params = new URLSearchParams({ limit: 200 });
          if (currentServerId) params.set('serverId', currentServerId);
          if (action)          params.set('action', action);
          try {
            const res = await fetch('/api/admin/audit?' + params);
            if (!res.ok) throw new Error(res.status);
            const logs = await res.json();
            renderAudit(logs);
          } catch (e) {
            document.getElementById('auditTableBody').innerHTML =
              `<tr><td colspan="5" style="color:#ed4245;padding:20px 10px;">Failed: ${e.message}</td></tr>`;
          }
        }

        function actionBadge(action) {
          const map = {
            RoleChanged:              '#8ab4d4',
            MemberKicked:             '#ed4245',
            UserMuted:                '#f0a030',
            UserUnmuted:              '#44bb44',
            UserTempBanned:           '#ed4245',
            MessageDeleted:           '#f0a030',
            ChannelCreated:           '#44bb44',
            ChannelDeleted:           '#ed4245',
            ChannelPermissionsChanged:'#8ab4d4',
          };
          const col = map[action] || '#888';
          return `<span class="badge" style="background:${col}22;color:${col}">${action}</span>`;
        }

        function renderAudit(logs) {
          const tbody = document.getElementById('auditTableBody');
          if (!logs.length) {
            tbody.innerHTML = '<tr><td colspan="5" style="color:#444;padding:20px 10px;">No audit entries found.</td></tr>';
            return;
          }
          tbody.innerHTML = logs.map(a => {
            const ts = new Date(a.createdAt);
            return `<tr>
              <td style="color:#555;font-size:11px;white-space:nowrap">${ts.toLocaleDateString()} ${ts.toLocaleTimeString()}</td>
              <td style="color:#8ab4d4">${escapeHtml(a.actorUsername)}</td>
              <td>${actionBadge(a.action)}</td>
              <td style="color:#aaa">${escapeHtml(a.targetUsername ?? '—')}</td>
              <td style="color:#777;font-size:11px">${(a.detail ?? '').replace(/</g,'&lt;').replace(/>/g,'&gt;')}</td>
            </tr>`;
          }).join('');
        }

        // ── Channels ──────────────────────────────────────
        async function loadChannels() {
          const params = new URLSearchParams();
          if (currentServerId) params.set('serverId', currentServerId);
          try {
            const res = await fetch('/api/admin/channels?' + params);
            if (!res.ok) throw new Error(res.status);
            const channels = await res.json();
            renderChannels(channels, currentServerId);
          } catch (e) {
            document.getElementById('channelTableBody').innerHTML =
              `<tr><td colspan="6" style="color:#ed4245;padding:20px 10px;">Failed: ${e.message}</td></tr>`;
          }
        }

        const roles = ['Member','Moderator','Admin'];

        function renderChannels(channels, serverId) {
          const tbody = document.getElementById('channelTableBody');
          if (!channels.length) {
            tbody.innerHTML = '<tr><td colspan="6" style="color:#444;padding:20px 10px;">No channels found.</td></tr>';
            return;
          }
          tbody.innerHTML = channels.map(c => `
            <tr id="ch-row-${c.id}">
              <td style="color:#555">${escapeHtml(c.serverName)}</td>
              <td style="color:#d0d0d0">#${escapeHtml(c.name)}</td>
              <td style="color:#666">${c.type}</td>
              <td>
                <select onchange="saveChannelPerm(${c.serverId},${c.id})"
                  id="read-${c.id}"
                  title="Minimum role required to see and read this channel"
                  style="background:#252525;border:1px solid #333;border-radius:3px;color:#d0d0d0;font-size:11px;padding:3px 6px">
                  ${roles.map(r => `<option value="${r}" ${c.minRoleToRead===r?'selected':''}>${r}</option>`).join('')}
                </select>
              </td>
              <td>
                <select onchange="saveChannelPerm(${c.serverId},${c.id})"
                  id="write-${c.id}"
                  title="Minimum role required to send messages in this channel (must be ≥ Min Read Role)"
                  style="background:#252525;border:1px solid #333;border-radius:3px;color:#d0d0d0;font-size:11px;padding:3px 6px">
                  ${roles.map(r => `<option value="${r}" ${c.minRoleToWrite===r?'selected':''}>${r}</option>`).join('')}
                </select>
              </td>
              <td style="display:flex;align-items:center;gap:6px;flex-wrap:wrap">
                <input id="ch-rename-${c.id}" value="${escapeHtml(c.name)}"
                  style="background:#1a1a1a;border:1px solid #3a3a3a;border-radius:4px;color:#d0d0d0;font-size:11px;padding:3px 7px;font-family:inherit;outline:none;width:110px"
                  onkeydown="if(event.key==='Enter')renameChannel(${c.serverId},${c.id})" />
                <button class="btn-sm" style="padding:3px 8px;font-size:11px"
                  onclick="renameChannel(${c.serverId},${c.id})"
                  title="Rename this channel">Rename</button>
                <span id="ch-status-${c.id}" style="font-size:10px;color:#555"></span>
                <button class="btn-sm" style="background:#3a1a1a;color:#ed4245;border-color:#5a2a2a;padding:3px 8px;font-size:11px"
                  onclick="deleteChannel(${c.serverId},${c.id},'${escapeHtml(c.name)}')"
                  title="Permanently delete this channel and all its messages">Delete</button>
              </td>
            </tr>`).join('');
        }

        async function createChannel() {
          const nameEl   = document.getElementById('newChannelName');
          const statusEl = document.getElementById('createChannelStatus');
          const name = nameEl.value.trim().toLowerCase().replace(/\s+/g, '-');
          if (!name) { statusEl.textContent = 'Enter a channel name.'; statusEl.style.color = '#ed4245'; return; }
          if (!currentServerId) { statusEl.textContent = 'No server selected.'; statusEl.style.color = '#ed4245'; return; }
          statusEl.textContent = 'Creating…'; statusEl.style.color = '#555';
          try {
            const res = await fetch(`/api/admin/servers/${currentServerId}/channels`, {
              method: 'POST',
              headers: { 'Content-Type': 'application/json' },
              body: JSON.stringify({ name })
            });
            if (!res.ok) { const t = await res.text(); throw new Error(t || res.status); }
            nameEl.value = '';
            statusEl.textContent = `#${name} created.`;
            statusEl.style.color = '#57f287';
            setTimeout(() => { statusEl.textContent = ''; }, 3000);
            loadChannels();
          } catch (e) {
            statusEl.textContent = `Error: ${e.message}`;
            statusEl.style.color = '#ed4245';
          }
        }

        async function saveChannelPerm(serverId, channelId) {
          const minRoleToRead  = document.getElementById(`read-${channelId}`).value;
          const minRoleToWrite = document.getElementById(`write-${channelId}`).value;
          const status = document.getElementById(`ch-status-${channelId}`);
          status.textContent = 'Saving…';
          status.style.color = '#888';
          try {
            const res = await fetch(`/api/admin/servers/${serverId}/channels/${channelId}/permissions`, {
              method: 'PUT',
              headers: { 'Content-Type': 'application/json' },
              body: JSON.stringify({ minRoleToRead, minRoleToWrite })
            });
            if (!res.ok) throw new Error(await res.text() || res.status);
            status.textContent = 'Saved ✓';
            status.style.color = '#44bb44';
            setTimeout(() => { status.textContent = ''; }, 2000);
          } catch (e) {
            status.textContent = 'Failed';
            status.style.color = '#ed4245';
          }
        }

        async function renameChannel(serverId, channelId) {
          const input  = document.getElementById(`ch-rename-${channelId}`);
          const status = document.getElementById(`ch-status-${channelId}`);
          const name   = input.value.trim().toLowerCase().replace(/\s+/g, '-');
          if (!name) { status.textContent = 'Name required'; status.style.color = '#ed4245'; return; }
          status.textContent = 'Saving…'; status.style.color = '#888';
          try {
            const res = await fetch(`/api/admin/servers/${serverId}/channels/${channelId}/name`, {
              method: 'PATCH',
              headers: { 'Content-Type': 'application/json' },
              body: JSON.stringify({ name })
            });
            if (!res.ok) throw new Error(await res.text() || res.status);
            input.value = name;
            status.textContent = 'Renamed ✓'; status.style.color = '#44bb44';
            setTimeout(() => { status.textContent = ''; }, 2000);
          } catch (e) {
            status.textContent = `Error: ${e.message}`; status.style.color = '#ed4245';
          }
        }

        async function deleteChannel(serverId, channelId, channelName) {
          if (!confirm(`Delete #${channelName}? This cannot be undone.`)) return;
          const status = document.getElementById(`ch-status-${channelId}`);
          status.textContent = 'Deleting…'; status.style.color = '#888';
          try {
            const res = await fetch(`/api/admin/servers/${serverId}/channels/${channelId}`, { method: 'DELETE' });
            if (!res.ok) throw new Error(await res.text() || res.status);
            document.getElementById(`ch-row-${channelId}`)?.remove();
          } catch (e) {
            status.textContent = `Error: ${e.message}`; status.style.color = '#ed4245';
          }
        }

        // ── Word Filter ───────────────────────────────────
        async function loadWordFilters() {
          const tbody = document.getElementById('wfTableBody');
          if (!currentServerId) {
            tbody.innerHTML = '<tr><td colspan="3" style="color:#444;padding:20px 10px;">Loading…</td></tr>';
            return;
          }
          try {
            const res = await fetch(`/api/admin/servers/${currentServerId}/wordfilter`);
            if (!res.ok) throw new Error(res.status);
            const filters = await res.json();
            if (!filters.length) {
              tbody.innerHTML = '<tr><td colspan="3" style="color:#444;padding:20px 10px;">No patterns configured.</td></tr>';
              return;
            }
            tbody.innerHTML = filters.map(f => {
              const ts = new Date(f.createdAt).toLocaleDateString();
              return `<tr>
                <td style="color:#d0d0d0;font-family:monospace">${escapeHtml(f.pattern)}</td>
                <td style="color:#555;font-size:11px">${ts}</td>
                <td><button class="btn-sm danger" title="Remove this pattern — messages containing it will no longer be filtered" onclick="removeWordFilter(${currentServerId},${f.id},this)">Remove</button></td>
              </tr>`;
            }).join('');
          } catch (e) {
            tbody.innerHTML = `<tr><td colspan="3" style="color:#ed4245;padding:20px 10px;">Failed: ${e.message}</td></tr>`;
          }
        }

        async function addWordFilter() {
          const input = document.getElementById('wfNewPattern');
          const pattern = input.value.trim();
          if (!currentServerId || !pattern) return;
          try {
            const res = await fetch(`/api/admin/servers/${currentServerId}/wordfilter`, {
              method: 'POST',
              headers: { 'Content-Type': 'application/json' },
              body: JSON.stringify({ pattern })
            });
            if (!res.ok) throw new Error(await res.text() || res.status);
            input.value = '';
            await loadWordFilters();
          } catch (e) {
            alert('Failed to add pattern: ' + e.message);
          }
        }

        async function removeWordFilter(serverId, filterId, btn) {
          btn.disabled = true;
          try {
            const res = await fetch(`/api/admin/servers/${serverId}/wordfilter/${filterId}`, { method: 'DELETE' });
            if (!res.ok) throw new Error(res.status);
            await loadWordFilters();
          } catch (e) {
            btn.disabled = false;
            alert('Failed: ' + e.message);
          }
        }

        // ── Boot ──────────────────────────────────────────
        (async () => {
          await initServer();
          refreshMetrics();
          refreshRateLimits();
        })();
        setInterval(refreshMetrics, 2000);
        setInterval(refreshRateLimits, 5000);
        setInterval(() => {
          if (document.getElementById('tab-users').classList.contains('active')) loadUsers();
          if (document.getElementById('tab-messages').classList.contains('active')) loadMessages();
          if (document.getElementById('tab-wordfilter').classList.contains('active')) loadWordFilters();
          if (document.getElementById('tab-audit').classList.contains('active')) loadAudit();
        }, 5000);
        </script>
        </body>
        </html>
        """;
}
