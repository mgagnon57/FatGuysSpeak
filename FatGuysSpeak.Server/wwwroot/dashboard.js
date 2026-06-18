function escapeHtml(s) {
  return String(s ?? '').replace(/&/g,'&amp;').replace(/</g,'&lt;').replace(/>/g,'&gt;').replace(/"/g,'&quot;').replace(/'/g,'&#39;');
}

// ── User profile modal ────────────────────────────────────
function fmtDuration(secs) {
  secs = Math.max(0, Math.floor(secs || 0));
  const d = Math.floor(secs / 86400), h = Math.floor((secs % 86400) / 3600), m = Math.floor((secs % 3600) / 60);
  if (d) return `${d}d ${h}h ${m}m`;
  if (h) return `${h}h ${m}m`;
  if (m) return `${m}m`;
  return `${secs}s`;
}
function fmtWhen(iso) {
  if (!iso) return 'never';
  const t = new Date(iso);
  return t.toLocaleDateString() + ' ' + t.toLocaleTimeString();
}
function fmtAgo(iso) {
  if (!iso) return 'never';
  const s = Math.floor((Date.now() - new Date(iso).getTime()) / 1000);
  if (s < 60) return 'just now';
  if (s < 3600) return Math.floor(s / 60) + ' min ago';
  if (s < 86400) return Math.floor(s / 3600) + ' hr ago';
  return Math.floor(s / 86400) + ' days ago';
}
function closeProfile() {
  document.getElementById('profileModal')?.remove();
}
async function openProfile(userId) {
  closeProfile();
  let p = null;
  try {
    const q = currentServerId ? ('?serverId=' + currentServerId) : '';
    const res = await fetch('/api/admin/users/' + userId + '/profile' + q);
    if (res.ok) p = await res.json();
  } catch {}
  renderProfileModal(p);
}
function pfRow(label, value) {
  return `<div class="pf-row"><span class="pf-label">${label}</span><span class="pf-val">${value}</span></div>`;
}
function renderProfileModal(p) {
  const wrap = document.createElement('div');
  wrap.id = 'profileModal';
  wrap.className = 'modal-backdrop';

  let body;
  if (!p) {
    body = `<div class="pf-section">Couldn't load this profile.</div>`;
  } else {
    const muted = p.mutedUntil && new Date(p.mutedUntil) > new Date();
    const banned = p.tempBanExpiresAt && new Date(p.tempBanExpiresAt) > new Date();
    body = `
      <div class="pf-section">
        ${pfRow('Email', escapeHtml(p.email || '—'))}
        ${pfRow('Status', escapeHtml(p.status) + (p.inVoice ? ' · 🎙 in voice' : ''))}
        ${p.bio ? pfRow('Bio', escapeHtml(p.bio)) : ''}
      </div>
      <div class="pf-section">
        ${pfRow('Role', escapeHtml(p.role))}
        ${pfRow('Member since', fmtWhen(p.createdAt))}
        ${pfRow('Moderation', muted ? ('Muted until ' + fmtWhen(p.mutedUntil)) : (banned ? ('Temp-banned until ' + fmtWhen(p.tempBanExpiresAt)) : 'None'))}
      </div>
      <div class="pf-section">
        ${pfRow('Last login', p.lastLoginAt ? (fmtWhen(p.lastLoginAt) + (p.lastLoginIp ? ' · ' + escapeHtml(p.lastLoginIp) : '')) : 'never')}
        ${pfRow('Last device', p.lastLoginUserAgent ? escapeHtml(p.lastLoginUserAgent) : '—')}
        ${pfRow('Last seen', fmtAgo(p.lastSeenAt))}
        ${pfRow('Active sessions', p.activeSessionCount)}
      </div>
      <div class="pf-section">
        ${pfRow('Messages sent', p.messageCount)}
        ${pfRow('Most-used channel', p.topChannel ? '#' + escapeHtml(p.topChannel) : '—')}
        ${pfRow('Total time on server', fmtDuration(p.totalOnlineSeconds))}
      </div>`;
  }

  const title = p ? escapeHtml(p.username) : 'Profile';
  wrap.innerHTML = `
    <div class="modal-card">
      <div class="pf-head">
        <h2>${title}</h2>
        <button class="pf-close" data-click="closeProfile" title="Close" aria-label="Close">✕</button>
      </div>
      ${body}
    </div>`;
  document.body.appendChild(wrap);
  // Close only when the backdrop itself is clicked (not a click inside the card).
  wrap.addEventListener('click', (e) => { if (e.target === wrap) closeProfile(); });
}

// ── Tab switching ──────────────────────────────────
function showTab(name, btn) {
  document.querySelectorAll('.tab-panel').forEach(p => p.classList.remove('active'));
  document.querySelectorAll('.tab-btn').forEach(b => b.classList.remove('active'));
  document.getElementById('tab-' + name).classList.add('active');
  if (btn) btn.classList.add('active');
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
      backgroundColor: (ctx) => ctx.parsed.y > 20 ? '#ed4245' : '#d42d00',
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
        backgroundColor: '#1a1717',
        titleColor: '#f04010',
        bodyColor: '#e9e4d9',
        borderColor: '#292525',
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
      x: { grid: { color: '#1a1717' }, ticks: { color: '#322e28', font: { size: 10 }, maxRotation: 0 } },
      y: { grid: { color: '#1a1717' }, ticks: { color: '#322e28', font: { size: 10 }, stepSize: 1 }, beginAtZero: true }
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
    document.getElementById('liveDot').style.background = '#36b864';
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

function channelCell(u) {
  const t = u.textChannel, v = u.voiceChannel;
  // A channel here serves both text and voice, so a user's text and voice channel
  // are often the same — show it once (with a mic) instead of duplicating it.
  if (t && v && t === v) return '#' + escapeHtml(t) + ' 🎙';
  const parts = [];
  if (t) parts.push('#' + escapeHtml(t));
  if (v) parts.push('🎙 ' + escapeHtml(v));
  return parts.length ? parts.join(' · ') : '<span style="color:#4a443d">—</span>';
}

const AVATAR_COLORS = ['#d42d00','#e89000','#36b864','#f04010','#a060d0','#d04080'];
function avatarHtml(u) {
  if (u.avatarUrl) return `<img class="u-av" src="${escapeHtml(u.avatarUrl)}" alt="" loading="lazy" />`;
  const initial = ((u.username || '?').trim().charAt(0) || '?').toUpperCase();
  const col = AVATAR_COLORS[(u.id || 0) % AVATAR_COLORS.length];
  return `<span class="u-av u-av-fallback" style="background:${col}">${escapeHtml(initial)}</span>`;
}
function userCell(u) {
  const dot = `<span class="presence-dot" style="background:${u.isOnline ? '#36b864' : '#4a443d'}" title="${u.isOnline ? 'Online' : 'Offline'}"></span>`;
  return `<div class="u-cell">${avatarHtml(u)}${dot}<span class="user-link" data-click="profile" data-uid="${u.id}">${escapeHtml(u.username)}</span></div>`;
}
// Default ordering: online users first, then alphabetical. Column sort overrides this (set below).
let userSort = { key: 'online', dir: 'desc' };
function sortUsers(list) {
  const roleRank = { Admin: 3, Moderator: 2, Member: 1 };
  const roleOf = (u) => serverMembers.find(m => m.userId === u.id)?.role ?? null;
  const cmp = (a, b) => {
    let r = 0;
    switch (userSort.key) {
      case 'username':   r = a.username.localeCompare(b.username); break;
      case 'role':       r = (roleRank[roleOf(a)] || 0) - (roleRank[roleOf(b)] || 0); break;
      case 'created':    r = new Date(a.createdAt) - new Date(b.createdAt); break;
      case 'online':
      default:           r = (a.isOnline === b.isOnline) ? 0 : (a.isOnline ? 1 : -1); break;
    }
    r = userSort.dir === 'desc' ? -r : r;
    if (r === 0) r = a.username.localeCompare(b.username); // tiebreak always ascending
    return r;
  };
  return [...list].sort(cmp);
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
  const map = { Admin: '#f04010', Moderator: '#36b864', Member: '#4a443d' };
  const col = map[role] || '#4a443d';
  const tip = (ROLE_TIPS[role] || role || '').replace(/&/g, '&amp;').replace(/"/g, '&quot;');
  return `<span class="badge role-tip" style="background:${col}22;color:${col};cursor:help" data-tip="${tip}">${role ?? '—'}</span>`;
}

function renderUsers(users) {
  const tbody = document.getElementById('userTableBody');
  if (!users.length) {
    tbody.innerHTML = '<tr><td colspan="5" style="color:#322e28;padding:20px 10px;">No users found.</td></tr>';
    return;
  }
  const now = Date.now();
  tbody.innerHTML = users.map(u => {
    const member = serverMembers.find(m => m.userId === u.id);
    const role = member?.role ?? null;
    const nextUp   = role === 'Member' ? 'Moderator' : 'Admin';
    const nextDown = role === 'Admin'  ? 'Moderator' : 'Member';
    const roleCell = role
      ? `<div style="display:flex;align-items:center;gap:4px">${roleBadge(role)}
         <span class="role-arrows" style="margin-left:auto;display:inline-flex;gap:4px">
           <button class="btn-sm" title="Promote to ${nextUp}" data-click="promote" data-uid="${u.id}" ${role==='Admin'?'disabled':''} aria-label="Promote to ${nextUp}">▲</button>
           <button class="btn-sm danger" title="Demote to ${nextDown}" data-click="demote" data-uid="${u.id}" ${role==='Member'?'disabled':''} aria-label="Demote to ${nextDown}">▼</button>
         </span></div>`
      : '<span style="color:#4a443d">—</span>';
    const mutedUntil = member?.mutedUntil ? new Date(member.mutedUntil) : null;
    const isMuted = mutedUntil && mutedUntil.getTime() > now;
    const muteCell = member
      ? isMuted
        ? `<span style="color:#e89000;font-size:10px">until ${mutedUntil.toLocaleTimeString()}</span>
           <button class="btn-sm" style="margin-left:4px" title="Remove the active mute — user can send messages immediately" data-click="unmute" data-uid="${u.id}">Unmute</button>`
        : `<select class="btn-sm" data-change="mute" data-uid="${u.id}" ${role==='Admin'?'disabled':''} title="Temporarily prevent this user from sending messages"><option value="">Mute…</option><option value="300">5 minutes</option><option value="1800">30 minutes</option><option value="3600">1 hour</option><option value="86400">24 hours</option></select>`
      : '<span style="color:#4a443d">—</span>';
    return `<tr>
      <td>${userCell(u)}</td>
      <td>${channelCell(u)}</td>
      <td>${roleCell}</td>
      <td style="color:#4a443d">${new Date(u.createdAt).toLocaleDateString()}</td>
      <td class="actions-cell">
        ${u.voiceChannelId !== null
          ? `<button class="btn-sm danger" title="Disconnect this user from their current voice channel (they can rejoin)" data-click="kickVoice" data-uid="${u.id}">Kick Voice</button>`
          : `<button class="btn-sm" title="User is not in a voice channel" disabled>Kick Voice</button>`}
        ${member ? muteCell : ''}
        ${member && role !== 'Admin'
          ? `<button class="btn-sm danger" title="Remove this user from the server — they can rejoin via invite link" data-click="kick" data-uid="${u.id}">Kick</button>
              <select class="btn-sm danger" data-change="tempban" data-uid="${u.id}" title="Block this user from rejoining the server for a chosen duration"><option value="">Temp Ban…</option><option value="3600">1 hour</option><option value="86400">24 hours</option><option value="604800">7 days</option><option value="2592000">30 days</option></select>`
          : ''}
      </td>
    </tr>`;
  }).join('');
}

function filterUsers() {
  const q = (document.getElementById('userSearch')?.value || '').toLowerCase();
  const onlineOnly = document.getElementById('onlineOnly')?.checked;
  const roleF = document.getElementById('roleFilter')?.value || '';
  const roleOf = (u) => serverMembers.find(m => m.userId === u.id)?.role ?? null;

  let list = allUsers;
  if (q)          list = list.filter(u => u.username.toLowerCase().includes(q));
  if (onlineOnly) list = list.filter(u => u.isOnline);
  if (roleF)      list = list.filter(u => roleOf(u) === roleF);

  renderUsers(sortUsers(list));

  const online = allUsers.filter(u => u.isOnline).length;
  const cnt = document.getElementById('userCount');
  if (cnt) cnt.textContent = `${online} online · ${allUsers.length} total`;

  document.querySelectorAll('#userTable thead th[data-sortkey]').forEach(th => {
    const ind = th.querySelector('.sort-ind');
    if (ind) ind.textContent = th.dataset.sortkey === userSort.key ? (userSort.dir === 'asc' ? '▲' : '▼') : '';
  });
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
      `<tr><td colspan="5" style="color:#ed4245;padding:20px 10px;">Failed to load users: ${e.message}</td></tr>`;
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

async function muteFromSelect(sel) {
  const secs = parseInt(sel.value);
  const userId = +sel.dataset.uid;
  sel.selectedIndex = 0; // reset back to the "Mute…" placeholder
  if (!secs) return;
  await muteUser(currentServerId, userId, secs, sel);
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

async function tempBanFromSelect(sel) {
  const secs = parseInt(sel.value);
  const label = sel.options[sel.selectedIndex].text;
  const serverId = currentServerId;
  const userId = +sel.dataset.uid;
  sel.selectedIndex = 0; // reset back to the "Temp Ban…" placeholder
  if (!secs) return;
  if (!confirm(`Temp ban this user for ${label}?`)) return;
  await tempBanUser(serverId, userId, secs, sel);
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
      borderColor: '#e89000',
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
        backgroundColor: '#1a1717',
        titleColor: '#e89000',
        bodyColor: '#e9e4d9',
        borderColor: '#292525',
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
      x: { grid: { color: '#1a1717' }, ticks: { color: '#322e28', font: { size: 9 }, maxRotation: 0 } },
      y: { grid: { color: '#1a1717' }, ticks: { color: '#322e28', font: { size: 9 }, stepSize: 1 }, beginAtZero: true }
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
      tbody.innerHTML = '<tr><td colspan="3" style="color:#322e28;padding:10px">No rate limit hits yet.</td></tr>';
    } else {
      tbody.innerHTML = d.topOffenders.map(o => {
        const last = new Date(o.lastSeen).toLocaleTimeString();
        return `<tr>
          <td style="color:#e9e4d9">${escapeHtml(o.who)}</td>
          <td style="text-align:right;color:#e89000;font-weight:600">${o.hits}</td>
          <td style="color:#4a443d">${last}</td>
        </tr>`;
      }).join('');
    }
  } catch {}
}

// ── Message Log ───────────────────────────────────
let msgDebounce = null;
let oldestMsgId = null;     // cursor for "load more"
let allLoadedMsgs = [];     // accumulated across pages

function buildMsgParams(beforeId) {
  const author  = document.getElementById('msgAuthor').value.trim();
  const channel = document.getElementById('msgChannel').value.trim();
  const keyword = document.getElementById('msgKeyword').value.trim();
  const source  = document.getElementById('msgSource').value;
  const server  = document.getElementById('msgServer').value;
  const range   = document.getElementById('msgRange').value;
  const showDel = document.getElementById('msgShowDeleted').checked;
  const params = new URLSearchParams({ limit: 100 });
  if (author)  params.set('author',  author);
  if (channel) params.set('channel', channel);
  if (keyword) params.set('keyword', keyword);
  if (source)  params.set('source',  source);
  if (server)  params.set('serverId', server);
  if (range) {
    const from = new Date(Date.now() - (+range) * 86400000).toISOString();
    params.set('from', from);
  }
  params.set('showDeleted', showDel);   // server-side filter (keeps pages full)
  if (beforeId) params.set('beforeId', beforeId);
  return params;
}

async function loadMessages() {
  clearTimeout(msgDebounce);
  msgDebounce = setTimeout(async () => {
    try {
      const res = await fetch('/api/admin/messages?' + buildMsgParams(null));
      if (!res.ok) throw new Error(res.status);
      const msgs = await res.json();
      allLoadedMsgs = msgs;
      oldestMsgId = msgs.length ? msgs[msgs.length - 1].id : null;
      document.getElementById('msgLoadMore').style.display = (msgs.length >= 100) ? '' : 'none';
      renderMessages(allLoadedMsgs);
      reconcileSelection();
    } catch (e) {
      document.getElementById('msgTableBody').innerHTML =
        `<tr><td colspan="8" style="color:#ed4245;padding:20px 10px;">Failed: ${e.message}</td></tr>`;
    }
  }, 250);
}

async function loadMoreMsgs() {
  if (!oldestMsgId) return;
  try {
    const res = await fetch('/api/admin/messages?' + buildMsgParams(oldestMsgId));
    if (!res.ok) throw new Error(res.status);
    const msgs = await res.json();
    allLoadedMsgs = allLoadedMsgs.concat(msgs);
    oldestMsgId = msgs.length ? msgs[msgs.length - 1].id : oldestMsgId;
    document.getElementById('msgLoadMore').style.display = (msgs.length >= 100) ? '' : 'none';
    renderMessages(allLoadedMsgs);
    reconcileSelection();
  } catch (e) { alert('Load more failed: ' + e.message); }
}

async function loadServerOptions() {
  try {
    const res = await fetch('/api/admin/servers');
    if (!res.ok) return;
    const servers = await res.json();
    const sel = document.getElementById('msgServer');
    sel.innerHTML = '<option value="">All servers</option>' +
      servers.map(s => `<option value="${s.id}">${escapeHtml(s.name)}</option>`).join('');
  } catch { /* dropdown stays "All servers" */ }
}

function sourceBadge(s) {
  const map = { Text: '#d42d00', Voice: '#3a6a3a', Stream: '#5a2d8e' };
  const col = map[s] || '#292525';
  return `<span class="badge" style="background:${col}20;color:${col === '#292525' ? '#756e62' : col};border:1px solid ${col}40">${escapeHtml(s)}</span>`;
}

let lastRenderedMsgs = [];
const selectedMsgIds = new Set();
function renderMessages(msgs) {
  lastRenderedMsgs = msgs;
  const tbody = document.getElementById('msgTableBody');
  if (!msgs.length) {
    tbody.innerHTML = '<tr><td colspan="8" style="color:#322e28;padding:20px 10px;">No messages found.</td></tr>';
    return;
  }
  tbody.innerHTML = msgs.map(m => {
    const ts = new Date(m.createdAt);
    const timeStr = ts.toLocaleDateString() + ' ' + ts.toLocaleTimeString();
    const deleted = m.isDeleted;
    const contentStyle = deleted ? 'color:#4a443d;font-style:italic' : 'color:#c0c0c0';
    const full = m.content || '';
    const truncated = full.length > 120;
    const shown = truncated ? full.slice(0, 120) + '…' : full;
    const contentCell = truncated
      ? `<span class="msg-content" data-click="expandMsg" data-mid="${m.id}" style="cursor:pointer" title="Click to expand">${escapeHtml(shown)}</span>`
      : escapeHtml(shown);
    const action = deleted
      ? `<button class="btn-sm" title="Restore this message — un-hide it (content was preserved)" data-click="restoreMsg" data-mid="${m.id}">Restore</button>`
      : `<button class="btn-sm danger" title="Soft-delete this message — hidden from clients but kept in the database" data-click="delMsg" data-mid="${m.id}">Delete</button>`;
    return `<tr data-mid="${m.id}" style="${deleted ? 'opacity:.55' : ''}">
      <td><input type="checkbox" class="msg-sel" data-change="toggleMsgSel" data-mid="${m.id}" ${selectedMsgIds.has(m.id) ? 'checked' : ''} style="accent-color:#f04010" /></td>
      <td style="color:#4a443d;font-size:11px;white-space:nowrap">${timeStr}</td>
      <td><span class="user-link" data-click="profile" data-uid="${m.authorId}" style="color:#f04010;font-weight:500;cursor:pointer">${escapeHtml(m.author)}</span></td>
      <td style="color:#5a544a">#${escapeHtml(m.channel)}</td>
      <td style="color:#4a443d;font-size:11px">${escapeHtml(m.server)}</td>
      <td>${sourceBadge(m.source)}</td>
      <td style="${contentStyle}">${contentCell}</td>
      <td>${action}</td>
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

async function adminRestoreMsg(msgId, btn) {
  if (!confirm('Restore this message?')) return;
  btn.disabled = true; btn.textContent = '…';
  try {
    const res = await fetch('/api/admin/messages/restore', {
      method: 'POST', headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ ids: [msgId] })
    });
    if (!res.ok) throw new Error(res.status);
    loadMessages();
  } catch (e) { btn.disabled = false; btn.textContent = 'Restore'; alert('Restore failed: ' + e.message); }
}

function expandMsg(msgId, el) {
  const m = lastRenderedMsgs.find(x => x.id === msgId);
  if (!m) return;
  el.textContent = m.content || '';
  el.removeAttribute('data-click');
  el.style.cursor = 'default';
  el.title = '';
}

function exportMsgCsv() {
  if (!lastRenderedMsgs.length) { alert('Nothing to export.'); return; }
  const esc = v => {
    const s = String(v ?? '');
    const safe = /^[=+\-@]/.test(s) ? '\t' + s : s;
    return `"${safe.replace(/"/g, '""')}"`;
  };
  const header = ['Time', 'Author', 'Channel', 'Server', 'Source', 'Content', 'Deleted'];
  const rows = lastRenderedMsgs.map(m => [
    new Date(m.createdAt).toISOString(), m.author, m.channel, m.server,
    m.source, m.content, m.isDeleted ? 'yes' : 'no'
  ].map(esc).join(','));
  const blob = new Blob([header.join(',') + '\n' + rows.join('\n')], { type: 'text/csv' });
  const a = document.createElement('a');
  a.href = URL.createObjectURL(blob);
  a.download = `messages-${new Date().toISOString().slice(0, 10)}.csv`;
  a.click();
  setTimeout(() => URL.revokeObjectURL(a.href), 100);
}

function updateBulkBar() {
  const n = selectedMsgIds.size;
  document.getElementById('msgBulkBar').style.display = n ? 'flex' : 'none';
  document.getElementById('msgSelCount').textContent = `${n} selected`;
}

function reconcileSelection() {
  const visible = new Set(allLoadedMsgs.map(m => m.id));
  for (const id of [...selectedMsgIds]) if (!visible.has(id)) selectedMsgIds.delete(id);
  const selAll = document.getElementById('msgSelectAll');
  if (selAll) selAll.checked = allLoadedMsgs.length > 0 && allLoadedMsgs.every(m => selectedMsgIds.has(m.id));
  updateBulkBar();
}

function toggleMsgSel(el) {
  const id = +el.dataset.mid;
  if (el.checked) selectedMsgIds.add(id); else selectedMsgIds.delete(id);
  updateBulkBar();
}

function toggleSelectAll(el) {
  selectedMsgIds.clear();
  if (el.checked) allLoadedMsgs.forEach(m => selectedMsgIds.add(m.id));
  document.querySelectorAll('.msg-sel').forEach(cb => { cb.checked = el.checked; });
  updateBulkBar();
}

function bulkClearSel() {
  selectedMsgIds.clear();
  document.getElementById('msgSelectAll').checked = false;
  document.querySelectorAll('.msg-sel').forEach(cb => { cb.checked = false; });
  updateBulkBar();
}

async function postBulkDelete(body, label) {
  const hard = document.getElementById('msgHardDelete').checked;
  const payload = { ...body, mode: hard ? 'hard' : 'soft' };
  const warn = `${label}\n\nThis will ${hard ? 'PERMANENTLY delete' : 'soft-delete'} the matching messages.`;
  if (!confirm(warn)) return;
  if (hard && !confirm('Permanent delete is IRREVERSIBLE. Click OK only if you are sure.')) return;
  try {
    const res = await fetch('/api/admin/messages/delete', {
      method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(payload)
    });
    if (!res.ok) { alert('Delete failed: ' + (await res.text())); return; }
    const r = await res.json();
    alert(`Deleted ${r.affected} message(s).`);
    bulkClearSel();
    loadMessages();
  } catch (e) { alert('Delete failed: ' + e.message); }
}

function bulkDelSelected() {
  if (!selectedMsgIds.size) return;
  postBulkDelete({ ids: [...selectedMsgIds] }, `Delete ${selectedMsgIds.size} selected message(s)?`);
}

function currentFilterObj() {
  const f = {
    author:   document.getElementById('msgAuthor').value.trim() || null,
    channel:  document.getElementById('msgChannel').value.trim() || null,
    keyword:  document.getElementById('msgKeyword').value.trim() || null,
    source:   document.getElementById('msgSource').value || null,
    serverId: document.getElementById('msgServer').value ? +document.getElementById('msgServer').value : null,
  };
  const range = document.getElementById('msgRange').value;
  if (range) f.from = new Date(Date.now() - (+range) * 86400000).toISOString();
  return f;
}

function bulkDelFilter() {
  const f = currentFilterObj();
  const hasAny = Object.values(f).some(v => v !== null && v !== undefined);
  if (!hasAny && !confirm('No filters set — this will delete EVERY message. Continue?')) return;
  postBulkDelete({ filter: f }, 'Delete ALL messages matching the current filter?\n(Affects the entire matching set on the server, not just the rows loaded here.)');
}

async function bulkRestoreFilter() {
  const f = currentFilterObj();
  if (!confirm('Restore ALL soft-deleted messages matching the current filter?')) return;
  try {
    const res = await fetch('/api/admin/messages/restore', {
      method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ filter: f })
    });
    if (!res.ok) { alert('Restore failed: ' + (await res.text())); return; }
    const r = await res.json();
    alert(`Restored ${r.affected} message(s).`);
    loadMessages();
  } catch (e) { alert('Restore failed: ' + e.message); }
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
    RoleChanged:              '#f04010',
    MemberKicked:             '#ed4245',
    UserMuted:                '#e89000',
    UserUnmuted:              '#36b864',
    UserTempBanned:           '#ed4245',
    MessageDeleted:           '#e89000',
    ChannelCreated:           '#36b864',
    ChannelDeleted:           '#ed4245',
    ChannelPermissionsChanged:'#f04010',
  };
  const col = map[action] || '#756e62';
  return `<span class="badge" style="background:${col}22;color:${col}">${action}</span>`;
}

function renderAudit(logs) {
  const tbody = document.getElementById('auditTableBody');
  if (!logs.length) {
    tbody.innerHTML = '<tr><td colspan="5" style="color:#322e28;padding:20px 10px;">No audit entries found.</td></tr>';
    return;
  }
  tbody.innerHTML = logs.map(a => {
    const ts = new Date(a.createdAt);
    return `<tr>
      <td style="color:#4a443d;font-size:11px;white-space:nowrap">${ts.toLocaleDateString()} ${ts.toLocaleTimeString()}</td>
      <td style="color:#f04010">${escapeHtml(a.actorUsername)}</td>
      <td>${actionBadge(a.action)}</td>
      <td style="color:#9a9183">${escapeHtml(a.targetUsername ?? '—')}</td>
      <td style="color:#6a6358;font-size:11px">${(a.detail ?? '').replace(/</g,'&lt;').replace(/>/g,'&gt;')}</td>
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
    tbody.innerHTML = '<tr><td colspan="6" style="color:#322e28;padding:20px 10px;">No channels found.</td></tr>';
    return;
  }
  tbody.innerHTML = channels.map(c => `
    <tr id="ch-row-${c.id}">
      <td style="color:#4a443d">${escapeHtml(c.serverName)}</td>
      <td style="color:#e9e4d9">#${escapeHtml(c.name)}</td>
      <td style="color:#5a544a">${c.type}</td>
      <td>
        <select data-change="saveChannelPerm" data-sid="${c.serverId}" data-cid="${c.id}"
          id="read-${c.id}"
          title="Minimum role required to see and read this channel"
          style="background:#161414;border:1px solid #292525;border-radius:3px;color:#e9e4d9;font-size:11px;padding:3px 6px">
          ${roles.map(r => `<option value="${r}" ${c.minRoleToRead===r?'selected':''}>${r}</option>`).join('')}
        </select>
      </td>
      <td>
        <select data-change="saveChannelPerm" data-sid="${c.serverId}" data-cid="${c.id}"
          id="write-${c.id}"
          title="Minimum role required to send messages in this channel (must be ≥ Min Read Role)"
          style="background:#161414;border:1px solid #292525;border-radius:3px;color:#e9e4d9;font-size:11px;padding:3px 6px">
          ${roles.map(r => `<option value="${r}" ${c.minRoleToWrite===r?'selected':''}>${r}</option>`).join('')}
        </select>
      </td>
      <td style="display:flex;align-items:center;gap:6px;flex-wrap:wrap">
        <input id="ch-rename-${c.id}" value="${escapeHtml(c.name)}"
          style="background:#0c0b0b;border:1px solid #3a3a3a;border-radius:4px;color:#e9e4d9;font-size:11px;padding:3px 7px;font-family:inherit;outline:none;width:110px"
          data-enter="renameChannel" data-sid="${c.serverId}" data-cid="${c.id}" />
        <button class="btn-sm" style="padding:3px 8px;font-size:11px"
          data-click="renameChannel" data-sid="${c.serverId}" data-cid="${c.id}"
          title="Rename this channel">Rename</button>
        <span id="ch-status-${c.id}" style="font-size:10px;color:#4a443d"></span>
        ${c.isDefault
          ? `<span class="badge" style="background:#2d5f9e22;color:#f04010" title="The default channel can't be deleted — rename it instead">Default</span>`
          : `<button class="btn-sm" style="background:#3a1a1a;color:#ed4245;border-color:#5a2a2a;padding:3px 8px;font-size:11px"
          data-click="deleteChannel" data-sid="${c.serverId}" data-cid="${c.id}" data-cname="${escapeHtml(c.name)}"
          title="Permanently delete this channel and all its messages">Delete</button>`}
      </td>
    </tr>`).join('');
}

async function createChannel() {
  const nameEl   = document.getElementById('newChannelName');
  const statusEl = document.getElementById('createChannelStatus');
  const name = nameEl.value.trim().toLowerCase().replace(/\s+/g, '-');
  if (!name) { statusEl.textContent = 'Enter a channel name.'; statusEl.style.color = '#ed4245'; return; }
  if (!currentServerId) { statusEl.textContent = 'No server selected.'; statusEl.style.color = '#ed4245'; return; }
  statusEl.textContent = 'Creating…'; statusEl.style.color = '#4a443d';
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
  status.style.color = '#756e62';
  try {
    const res = await fetch(`/api/admin/servers/${serverId}/channels/${channelId}/permissions`, {
      method: 'PUT',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ minRoleToRead, minRoleToWrite })
    });
    if (!res.ok) throw new Error(await res.text() || res.status);
    status.textContent = 'Saved ✓';
    status.style.color = '#36b864';
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
  status.textContent = 'Saving…'; status.style.color = '#756e62';
  try {
    const res = await fetch(`/api/admin/servers/${serverId}/channels/${channelId}/name`, {
      method: 'PATCH',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ name })
    });
    if (!res.ok) throw new Error(await res.text() || res.status);
    input.value = name;
    status.textContent = 'Renamed ✓'; status.style.color = '#36b864';
    setTimeout(() => { status.textContent = ''; }, 2000);
  } catch (e) {
    status.textContent = `Error: ${e.message}`; status.style.color = '#ed4245';
  }
}

async function deleteChannel(serverId, channelId, channelName) {
  if (!confirm(`Delete #${channelName}? This cannot be undone.`)) return;
  const status = document.getElementById(`ch-status-${channelId}`);
  status.textContent = 'Deleting…'; status.style.color = '#756e62';
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
    tbody.innerHTML = '<tr><td colspan="3" style="color:#322e28;padding:20px 10px;">Loading…</td></tr>';
    return;
  }
  try {
    const res = await fetch(`/api/admin/servers/${currentServerId}/wordfilter`);
    if (!res.ok) throw new Error(res.status);
    const filters = await res.json();
    if (!filters.length) {
      tbody.innerHTML = '<tr><td colspan="3" style="color:#322e28;padding:20px 10px;">No patterns configured.</td></tr>';
      return;
    }
    tbody.innerHTML = filters.map(f => {
      const ts = new Date(f.createdAt).toLocaleDateString();
      return `<tr>
        <td style="color:#e9e4d9;font-family:monospace">${escapeHtml(f.pattern)}</td>
        <td style="color:#4a443d;font-size:11px">${ts}</td>
        <td><button class="btn-sm danger" title="Remove this pattern — messages containing it will no longer be filtered" data-click="rmWf" data-fid="${f.id}">Remove</button></td>
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
  loadServerOptions();
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

// ── CSP-safe event wiring (replaces inline on* handlers) ──────────
document.addEventListener('click', (e) => {
  const el = e.target.closest('[data-click]');
  if (!el || el.disabled) return;
  const d = el.dataset;
  switch (d.click) {
    case 'tab':           showTab(d.tab, el); break;
    case 'sort': {
      const k = d.sortkey;
      if (userSort.key === k) userSort.dir = userSort.dir === 'asc' ? 'desc' : 'asc';
      else userSort = { key: k, dir: (k === 'created') ? 'desc' : 'asc' };
      filterUsers();
      break;
    }
    case 'profile':       openProfile(+d.uid); break;
    case 'closeProfile':  closeProfile(); break;
    case 'promote':       promoteUser(+d.uid, el); break;
    case 'demote':        demoteUser(+d.uid, el); break;
    case 'unmute':        muteUser(currentServerId, +d.uid, 0, el); break;
    case 'kickVoice':     kickVoice(+d.uid, el); break;
    case 'kick':          kickFromServer(currentServerId, +d.uid, el); break;
    case 'loadMoreMsgs':  loadMoreMsgs(); break;
    case 'delMsg':        adminDeleteMsg(+d.mid, el); break;
    case 'restoreMsg':    adminRestoreMsg(+d.mid, el); break;
    case 'bulkDelSelected':   bulkDelSelected(); break;
    case 'bulkClearSel':      bulkClearSel(); break;
    case 'bulkDelFilter':     bulkDelFilter(); break;
    case 'bulkRestoreFilter': bulkRestoreFilter(); break;
    case 'expandMsg':     expandMsg(+d.mid, el); break;
    case 'exportMsgCsv':  exportMsgCsv(); break;
    case 'rmWf':          removeWordFilter(currentServerId, +d.fid, el); break;
    case 'createChannel': createChannel(); break;
    case 'addWf':         addWordFilter(); break;
    case 'renameChannel': renameChannel(+d.sid, +d.cid); break;
    case 'deleteChannel': deleteChannel(+d.sid, +d.cid, d.cname); break;
  }
});

document.addEventListener('change', (e) => {
  const el = e.target.closest('[data-change]');
  if (!el) return;
  const d = el.dataset;
  switch (d.change) {
    case 'loadMessages':    loadMessages(); break;
    case 'toggleMsgSel':    toggleMsgSel(el); break;
    case 'toggleSelectAll': toggleSelectAll(el); break;
    case 'loadAudit':       loadAudit(); break;
    case 'saveChannelPerm': saveChannelPerm(+d.sid, +d.cid); break;
    case 'filterUsers':     filterUsers(); break;
    case 'mute':            muteFromSelect(el); break;
    case 'tempban':         tempBanFromSelect(el); break;
  }
});

document.addEventListener('input', (e) => {
  const el = e.target.closest('[data-input]');
  if (!el) return;
  if (el.dataset.input === 'filterUsers') filterUsers();
  else if (el.dataset.input === 'loadMessages') loadMessages();
});

document.addEventListener('keydown', (e) => {
  if (e.key === 'Escape') { closeProfile(); return; }
  if (e.key !== 'Enter') return;
  const el = e.target.closest('[data-enter]');
  if (!el) return;
  const d = el.dataset;
  if (d.enter === 'createChannel') createChannel();
  else if (d.enter === 'addWf') addWordFilter();
  else if (d.enter === 'renameChannel') renameChannel(+d.sid, +d.cid);
});
