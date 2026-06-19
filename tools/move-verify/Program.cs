using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.SignalR.Client;

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

var (moverTok, moverId) = await Reg("mover");
var (targetTok, targetId) = await Reg("target");
Console.WriteLine($"mover id={moverId}  target id={targetId}");

// mover creates a server -> mover becomes Admin on it
var srv = await Post("/api/servers", new { Name = $"Harness {sfx}", Description = (string?)null }, moverTok);
var serverId = srv.GetProperty("id").GetInt32();
Console.WriteLine($"server id={serverId}  moverRole={srv.GetProperty("myRole")}");

// ensure a destination channel exists
var chans = await Get($"/api/servers/{serverId}/channels", moverTok);
int destId;
if (chans.GetArrayLength() > 0) destId = chans[0].GetProperty("id").GetInt32();
else { var c = await Post($"/api/servers/{serverId}/channels", new { Name = "move-dest", Type = 0 }, moverTok); destId = c.GetProperty("id").GetInt32(); }
Console.WriteLine($"dest channel id={destId}");

// target joins the server (as Member)
var inv = await Get($"/api/servers/{serverId}/invite", moverTok);
await Post($"/api/servers/by-invite/{inv.GetProperty("code").GetString()}/join", new { }, targetTok);
Console.WriteLine("target joined server");

// connect both over real SignalR
HubConnection Hub(string tok) => new HubConnectionBuilder().WithUrl($"{Base}/hubs/chat?access_token={tok}").Build();
var moverHub = Hub(moverTok);
var targetHub = Hub(targetTok);

var gotForceJoin = new TaskCompletionSource<int>();
targetHub.On<int>("ForceJoinChannel", id => gotForceJoin.TrySetResult(id));

await targetHub.StartAsync();
await moverHub.StartAsync();
Console.WriteLine($"both hubs connected (target={targetHub.State}, mover={moverHub.State})");

// THE ACTUAL FEATURE CALL: mover (Admin) moves target into the dest channel
await moverHub.InvokeAsync("MoveUserToChannel", targetId, destId);
Console.WriteLine("invoked MoveUserToChannel; waiting for target to receive ForceJoinChannel...");

var done = await Task.WhenAny(gotForceJoin.Task, Task.Delay(5000));
if (done == gotForceJoin.Task && gotForceJoin.Task.Result == destId)
    Console.WriteLine($"\n✅ VERIFIED: target received ForceJoinChannel({gotForceJoin.Task.Result}) == dest {destId}");
else if (done == gotForceJoin.Task)
    Console.WriteLine($"\n❌ FAIL: target got ForceJoinChannel({gotForceJoin.Task.Result}) but expected {destId}");
else
    Console.WriteLine($"\n❌ FAIL: target NEVER received ForceJoinChannel within 5s");

await moverHub.DisposeAsync();
await targetHub.DisposeAsync();
