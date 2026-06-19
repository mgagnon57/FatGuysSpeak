using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.SignalR.Client;

// Verifies cross-channel presence: an observer sitting in channel A must receive
// UserJoinedChannel / UserLeftChannel for a DIFFERENT channel B (server-wide broadcast),
// so the sidebar shows who is in every channel — even channels you are not in.

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

var (obsTok, obsId) = await Reg("obs");
var (actTok, actId) = await Reg("act");
Console.WriteLine($"observer id={obsId}  actor id={actId}");

// actor creates a server -> becomes Admin
var srv = await Post("/api/servers", new { Name = $"Presence {sfx}", Description = (string?)null }, actTok);
var serverId = srv.GetProperty("id").GetInt32();

// two channels: A (observer sits here) and B (actor joins here)
var chanA = await Post($"/api/servers/{serverId}/channels", new { Name = "room-a", Type = 0 }, actTok);
var chanB = await Post($"/api/servers/{serverId}/channels", new { Name = "room-b", Type = 0 }, actTok);
int aId = chanA.GetProperty("id").GetInt32(), bId = chanB.GetProperty("id").GetInt32();
Console.WriteLine($"server id={serverId}  channel A={aId}  channel B={bId}");

// observer joins the server
var inv = await Get($"/api/servers/{serverId}/invite", actTok);
await Post($"/api/servers/by-invite/{inv.GetProperty("code").GetString()}/join", new { }, obsTok);

HubConnection Hub(string tok) => new HubConnectionBuilder().WithUrl($"{Base}/hubs/chat?access_token={tok}").Build();
var obsHub = Hub(obsTok);
var actHub = Hub(actTok);

var gotJoin = new TaskCompletionSource<int>();
var gotLeave = new TaskCompletionSource<int>();
// observer is in channel A; it must still hear about channel B
obsHub.On<int, JsonElement>("UserJoinedChannel", (chId, _) => { if (chId == bId) gotJoin.TrySetResult(chId); });
obsHub.On<int, JsonElement>("UserLeftChannel",   (chId, _) => { if (chId == bId) gotLeave.TrySetResult(chId); });

await obsHub.StartAsync();
await actHub.StartAsync();
Console.WriteLine($"hubs connected (obs={obsHub.State}, act={actHub.State})");

// observer sits in channel A
await obsHub.InvokeAsync("JoinChannel", aId);
Console.WriteLine($"observer joined channel A ({aId}); now watching for activity in channel B ({bId})...");

// actor joins channel B — observer (in A) should be notified
await actHub.InvokeAsync("JoinChannel", bId);

bool ok = true;
var joinDone = await Task.WhenAny(gotJoin.Task, Task.Delay(5000));
if (joinDone == gotJoin.Task) Console.WriteLine($"✅ observer (in A) received UserJoinedChannel({bId}) for actor in B");
else { ok = false; Console.WriteLine($"❌ observer NEVER received UserJoinedChannel({bId}) within 5s"); }

// actor leaves channel B -> observer should be notified of the leave too
await actHub.InvokeAsync("LeaveChannel", bId);
var leaveDone = await Task.WhenAny(gotLeave.Task, Task.Delay(5000));
if (leaveDone == gotLeave.Task) Console.WriteLine($"✅ observer (in A) received UserLeftChannel({bId})");
else { ok = false; Console.WriteLine($"❌ observer NEVER received UserLeftChannel({bId}) within 5s"); }

Console.WriteLine(ok
    ? "\n✅ VERIFIED: cross-channel presence works — sidebar will show users in channels you are not in."
    : "\n❌ FAIL: cross-channel presence broadcast is not reaching observers in other channels.");

await obsHub.DisposeAsync();
await actHub.DisposeAsync();
Environment.Exit(ok ? 0 : 1);
