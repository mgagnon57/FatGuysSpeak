using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.SignalR.Client;

// Reproduces the move bug from BOTH source channels: a user sitting in the LOBBY (default) and a
// user sitting in a NON-LOBBY channel, each moved into another non-lobby channel. Mimics the real
// client login (target lands on a channel via JoinChannel first), then the admin moves them and we
// confirm the admin's occupant view tracks the move.

const string Base = "http://localhost:5238";
var http = new HttpClient { BaseAddress = new Uri(Base) };
var sfx = DateTime.Now.Ticks.ToString()[^6..];

async Task<JsonElement> Post(string path, object body, string? token = null)
{
    using var req = new HttpRequestMessage(HttpMethod.Post, path) { Content = JsonContent.Create(body) };
    if (token is not null) req.Headers.Authorization = new("Bearer", token);
    var resp = await http.SendAsync(req);
    var txt = await resp.Content.ReadAsStringAsync();
    if (!resp.IsSuccessStatusCode) throw new Exception($"POST {path} -> {(int)resp.StatusCode}: {txt}");
    return string.IsNullOrWhiteSpace(txt) ? default : JsonDocument.Parse(txt).RootElement;
}
async Task<JsonElement> Get(string path, string token)
{
    using var req = new HttpRequestMessage(HttpMethod.Get, path);
    req.Headers.Authorization = new("Bearer", token);
    var resp = await http.SendAsync(req);
    var txt = await resp.Content.ReadAsStringAsync();
    if (!resp.IsSuccessStatusCode) throw new Exception($"GET {path} -> {(int)resp.StatusCode}: {txt}");
    return JsonDocument.Parse(txt).RootElement;
}
async Task<(string token, int id)> Reg(string who)
{
    var r = await Post("/api/auth/register", new { Username = $"{who}_{sfx}", Password = "Harness2026!", Email = $"{who}_{sfx}@t.com" });
    return (r.GetProperty("token").GetString()!, r.GetProperty("userId").GetInt32());
}

var (adminTok, adminId) = await Reg("madmin");
var (userTok, userId)   = await Reg("muser");
Console.WriteLine($"admin id={adminId}  user id={userId}");

var srv = await Post("/api/servers", new { Name = $"Move {sfx}", Description = (string?)null }, adminTok);
var serverId = srv.GetProperty("id").GetInt32();

var chans = await Get($"/api/servers/{serverId}/channels", adminTok);
int lobbyId = 0;
foreach (var c in chans.EnumerateArray())
    if (c.GetProperty("name").GetString() == "lobby") lobbyId = c.GetProperty("id").GetInt32();

var chanX = await Post($"/api/servers/{serverId}/channels", new { Name = "room-x", Type = 0 }, adminTok);
var chanY = await Post($"/api/servers/{serverId}/channels", new { Name = "room-y", Type = 0 }, adminTok);
int xId = chanX.GetProperty("id").GetInt32(), yId = chanY.GetProperty("id").GetInt32();
Console.WriteLine($"server id={serverId}  lobby={lobbyId}  X={xId}  Y={yId}");

var inv = await Get($"/api/servers/{serverId}/invite", adminTok);
await Post($"/api/servers/by-invite/{inv.GetProperty("code").GetString()}/join", new { }, userTok);

HubConnection Hub(string tok) => new HubConnectionBuilder().WithUrl($"{Base}/hubs/chat?access_token={tok}").Build();
var adminHub = Hub(adminTok);
var userHub  = Hub(userTok);

var forceJoins = new System.Collections.Concurrent.ConcurrentQueue<int>();
var signal = new SemaphoreSlim(0);
userHub.On<int>("ForceJoinChannel", id => { forceJoins.Enqueue(id); signal.Release(); });

await userHub.StartAsync();
await adminHub.StartAsync();

bool ok = true;

async Task<bool> MoveCase(string label, int from, int to)
{
    // target lands on the source channel like a real client would
    await userHub.InvokeAsync("JoinChannel", from);
    var fromOcc = await adminHub.InvokeAsync<List<JsonElement>>("GetChannelOccupants", from);
    bool seen = fromOcc.Any(u => u.GetProperty("id").GetInt32() == userId);
    Console.WriteLine($"[{label}] user in source {from}: {(seen ? "visible" : "MISSING")}");

    // admin moves the user to the destination
    await adminHub.InvokeAsync("MoveUserToChannel", userId, to);
    bool got = await signal.WaitAsync(5000) && forceJoins.TryDequeue(out var dest) && dest == to;
    Console.WriteLine($"[{label}] user received ForceJoinChannel({to}): {(got ? "YES" : "NO")}");

    if (got)
    {
        await userHub.InvokeAsync("JoinChannel", to);   // simulate the client reacting
        var toOcc   = await adminHub.InvokeAsync<List<JsonElement>>("GetChannelOccupants", to);
        var fromNow = await adminHub.InvokeAsync<List<JsonElement>>("GetChannelOccupants", from);
        bool inTo   = toOcc.Any(u => u.GetProperty("id").GetInt32() == userId);
        bool gone   = !fromNow.Any(u => u.GetProperty("id").GetInt32() == userId);
        Console.WriteLine($"[{label}] now in dest {to}: {(inTo ? "YES" : "NO")};  left source: {(gone ? "YES" : "NO")}");
        return seen && got && inTo && gone;
    }
    return false;
}

Console.WriteLine();
ok &= await MoveCase("lobby->Y", lobbyId, yId);
Console.WriteLine();
ok &= await MoveCase("X->Y (non-lobby to non-lobby)", xId, yId);

Console.WriteLine(ok
    ? "\n✅ VERIFIED: moves work from BOTH lobby and non-lobby sources at the server/relay level."
    : "\n❌ FAIL: a move case did not work server-side.");

await adminHub.DisposeAsync();
await userHub.DisposeAsync();
Environment.Exit(ok ? 0 : 1);
