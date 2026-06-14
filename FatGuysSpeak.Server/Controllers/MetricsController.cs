using FatGuysSpeak.Server.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace FatGuysSpeak.Server.Controllers;

[ApiController]
[AllowAnonymous]
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
            margin-bottom: 20px; padding-bottom: 14px;
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

          .status-bar {
            display: flex; justify-content: space-between; align-items: center;
            margin-top: 12px; font-size: 10px; color: #444;
          }
        </style>
        </head>
        <body>

        <header>
          <h1>🖥&nbsp; FatGuysSpeak &mdash; Server Dashboard</h1>
          <div class="live-badge">
            <div class="live-dot" id="liveDot"></div>
            <span class="live-text" id="lastUpdated">connecting…</span>
          </div>
        </header>

        <div class="cards">
          <div class="card" id="c-online">
            <span class="icon">👥</span>
            <div class="value" id="onlineUsers">—</div>
            <div class="label">Online Users</div>
          </div>
          <div class="card" id="c-voice">
            <span class="icon">🎙</span>
            <div class="value" id="voiceParticipants">—</div>
            <div class="label">Voice Sessions</div>
          </div>
          <div class="card" id="c-streams">
            <span class="icon">📺</span>
            <div class="value" id="activeStreams">—</div>
            <div class="label">Active Streams</div>
          </div>
          <div class="card" id="c-msgsmin">
            <span class="icon">💬</span>
            <div class="value" id="msgsPerMin">—</div>
            <div class="label">Msgs / Min</div>
          </div>
          <div class="card" id="c-total">
            <span class="icon">📨</span>
            <div class="value" id="totalMsgs">—</div>
            <div class="label">Total Messages</div>
          </div>
          <div class="card" id="c-mem">
            <span class="icon">🧠</span>
            <div class="value" id="memoryMb">—</div>
            <div class="label">Memory (MB)</div>
          </div>
          <div class="card" id="c-cpu">
            <span class="icon">⚡</span>
            <div class="value" id="cpuPct">—</div>
            <div class="label">CPU %</div>
          </div>
          <div class="card" id="c-uptime">
            <span class="icon">⏱</span>
            <div class="value" id="uptime">—</div>
            <div class="label">Uptime</div>
          </div>
        </div>

        <div class="chart-card">
          <div class="chart-header">
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

        <div class="status-bar">
          <span id="serverUrl">http://localhost:5238</span>
          <span id="refreshNote">auto-refreshes every 2s</span>
        </div>

        <script>
        // Build x-axis labels: '60m' ... '10m' ... 'now'
        const xLabels = Array.from({length:60}, (_,i) => {
          const age = 59 - i; // index 0 = 60min ago on left, index 59 = now on right
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
              x: {
                grid: { color: '#222' },
                ticks: { color: '#444', font: { size: 10 }, maxRotation: 0 }
              },
              y: {
                grid: { color: '#222' },
                ticks: { color: '#444', font: { size: 10 }, stepSize: 1 },
                beginAtZero: true
              }
            }
          }
        });

        const prev = {};

        function setCard(cardId, elId, rawVal, display) {
          const card = document.getElementById(cardId);
          const el   = document.getElementById(elId);
          if (!card || !el) return;
          const key = elId;
          if (prev[key] !== undefined && prev[key] !== String(rawVal)) {
            card.classList.add('flash');
            setTimeout(() => card.classList.remove('flash'), 700);
          }
          el.textContent = display ?? rawVal;
          prev[key] = String(rawVal);
        }

        async function refresh() {
          try {
            const res = await fetch('/api/metrics');
            if (!res.ok) throw new Error(res.status);
            const d = await res.json();

            setCard('c-online',  'onlineUsers',      d.onlineUsers,        d.onlineUsers);
            setCard('c-voice',   'voiceParticipants', d.voiceParticipants, d.voiceParticipants);
            setCard('c-streams', 'activeStreams',     d.activeStreams,      d.activeStreams);
            setCard('c-msgsmin', 'msgsPerMin',        d.messagesLastMinute, d.messagesLastMinute);
            setCard('c-total',   'totalMsgs',         d.totalMessages,      d.totalMessages.toLocaleString());
            setCard('c-mem',     'memoryMb',          d.memoryMb,           d.memoryMb + ' MB');
            setCard('c-cpu',     'cpuPct',            d.cpuPercent,         d.cpuPercent.toFixed(1) + '%');
            setCard('c-uptime',  'uptime',            d.uptimeFormatted,    d.uptimeFormatted);

            // history[0] = current minute (rightmost on chart), history[59] = 60min ago (leftmost)
            throughput.data.datasets[0].data = [...d.messageHistory].reverse();
            throughput.update('none');

            const now = new Date().toLocaleTimeString();
            document.getElementById('lastUpdated').textContent = 'updated ' + now;
            document.getElementById('liveDot').style.background = '#44bb44';
            document.getElementById('serverUrl').textContent = window.location.origin;
          } catch {
            document.getElementById('lastUpdated').textContent = 'connection lost — retrying…';
            document.getElementById('liveDot').style.background = '#ed4245';
          }
        }

        refresh();
        setInterval(refresh, 2000);
        </script>
        </body>
        </html>
        """;
}
