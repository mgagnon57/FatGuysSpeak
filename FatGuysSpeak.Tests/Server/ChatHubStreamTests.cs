using System.Reflection;
using System.Security.Claims;
using FatGuysSpeak.Server.Data;
using FatGuysSpeak.Server.Hubs;
using FatGuysSpeak.Server.Models;
using FatGuysSpeak.Shared;
using FatGuysSpeak.Tests.Helpers;
using Microsoft.AspNetCore.SignalR;
using Moq;

namespace FatGuysSpeak.Tests.Server;

[Collection("HubTests")]
public class ChatHubStreamTests : IDisposable
{
    private readonly TestDb _testDb;
    private readonly Mock<IHubCallerClients> _mockClients;
    private readonly Mock<IGroupManager> _mockGroups;
    // groupName -> list of (method, args) sent to that target
    private readonly Dictionary<string, List<(string Method, object[] Args)>> _sent = new();

    public ChatHubStreamTests()
    {
        _testDb = new TestDb();

        _mockClients = new Mock<IHubCallerClients>();
        _mockClients.Setup(c => c.Group(It.IsAny<string>()))
            .Returns<string>(g => TrackingProxy($"group:{g}"));
        _mockClients.Setup(c => c.Caller)
            .Returns(SingleTrackingProxy("caller"));
        _mockClients.Setup(c => c.OthersInGroup(It.IsAny<string>()))
            .Returns<string>(g => TrackingProxy($"others:{g}"));

        _mockGroups = new Mock<IGroupManager>();
        _mockGroups
            .Setup(g => g.AddToGroupAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        _mockGroups
            .Setup(g => g.RemoveFromGroupAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
    }

    public void Dispose()
    {
        ClearHubStaticState();
        _testDb.Dispose();
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private IClientProxy TrackingProxy(string target)
    {
        var proxy = new Mock<IClientProxy>();
        proxy
            .Setup(p => p.SendCoreAsync(It.IsAny<string>(), It.IsAny<object[]>(), It.IsAny<CancellationToken>()))
            .Callback<string, object[], CancellationToken>((method, args, _) => Track(target, method, args))
            .Returns(Task.CompletedTask);
        return proxy.Object;
    }

    private ISingleClientProxy SingleTrackingProxy(string target)
    {
        var proxy = new Mock<ISingleClientProxy>();
        proxy
            .Setup(p => p.SendCoreAsync(It.IsAny<string>(), It.IsAny<object[]>(), It.IsAny<CancellationToken>()))
            .Callback<string, object[], CancellationToken>((method, args, _) => Track(target, method, args))
            .Returns(Task.CompletedTask);
        return proxy.Object;
    }

    private void Track(string target, string method, object[] args)
    {
        if (!_sent.TryGetValue(target, out var list))
            _sent[target] = list = [];
        list.Add((method, args));
    }

    private ChatHub CreateHub(int userId, string username, string connectionId = "conn1")
    {
        var claims = new[]
        {
            new Claim(ClaimTypes.NameIdentifier, userId.ToString()),
            new Claim(ClaimTypes.Name, username)
        };
        var mockCtx = new Mock<HubCallerContext>();
        mockCtx.Setup(c => c.ConnectionId).Returns(connectionId);
        mockCtx.Setup(c => c.User).Returns(new ClaimsPrincipal(new ClaimsIdentity(claims, "test")));

        var hub = new ChatHub(_testDb.Db, new FatGuysSpeak.Server.Services.OnlineTimeTracker(), TestHelpers.NullBot());
        hub.Context = mockCtx.Object;
        hub.Clients = _mockClients.Object;
        hub.Groups = _mockGroups.Object;
        return hub;
    }

    private bool WasSentTo(string group, string method) =>
        _sent.TryGetValue(group, out var list) && list.Any(m => m.Method == method);

    private bool WasNeverSentTo(string group, string method) => !WasSentTo(group, method);

    private (string Method, object[] Args) GetLastSent(string group, string method) =>
        _sent[$"group:{group}"].Last(m => m.Method == method);

    // Clears static dictionaries in ChatHub between tests to prevent cross-test contamination
    private static void ClearHubStaticState()
    {
        var flags = BindingFlags.Static | BindingFlags.NonPublic;
        foreach (var name in new[] { "ActiveStreamers", "VoiceChannelMap", "UserTextChannelMap", "ChannelOccupants", "OnlineUsers" })
        {
            var field = typeof(ChatHub).GetField(name, flags);
            (field?.GetValue(null) as System.Collections.IDictionary)?.Clear();
        }
    }

    // ── StreamNotification ───────────────────────────────────────────────────

    [Fact]
    public async Task StartStream_BroadcastsStreamNotification_ToTextChannels()
    {
        var (server, user) = await TestHelpers.SeedServerAsync(_testDb.Db, "notif-start");
        var channel = _testDb.Db.Channels.First(c => c.ServerId == server.Id && c.Name == "general");

        await CreateHub(user.Id, user.Username, "conn-notif").StartStream(channel.Id);

        Assert.True(WasSentTo($"group:channel-{channel.Id}", "StreamNotification"),
            "StartStream must post a StreamNotification to every text channel so viewers see it in chat");
    }

    [Fact]
    public async Task StopStream_BroadcastsStreamNotification_ToTextChannels()
    {
        var (server, user) = await TestHelpers.SeedServerAsync(_testDb.Db, "notif-stop");
        var channel = _testDb.Db.Channels.First(c => c.ServerId == server.Id && c.Name == "general");
        var hub = CreateHub(user.Id, user.Username, "conn-notif-stop");

        await hub.StartStream(channel.Id);
        _sent.Clear();
        await hub.StopStream();

        Assert.True(WasSentTo($"group:channel-{channel.Id}", "StreamNotification"),
            "StopStream must post a StreamNotification so viewers know the stream ended");
    }

    [Fact]
    public async Task StartStream_StreamNotification_MentionsStreamerAndChannel()
    {
        var (server, user) = await TestHelpers.SeedServerAsync(_testDb.Db, "notif-name");
        var channel = _testDb.Db.Channels.First(c => c.ServerId == server.Id && c.Name == "general");

        await CreateHub(user.Id, user.Username, "conn-notif-name").StartStream(channel.Id);

        var (_, args) = GetLastSent($"channel-{channel.Id}", "StreamNotification");
        var text = (string)args[1];
        Assert.Contains("general", text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(user.Username, text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task StopStream_StreamNotification_MentionsStreamerAndChannel()
    {
        var (server, user) = await TestHelpers.SeedServerAsync(_testDb.Db, "notif-stop-name");
        var channel = _testDb.Db.Channels.First(c => c.ServerId == server.Id && c.Name == "general");
        var hub = CreateHub(user.Id, user.Username, "conn-notif-stop-name");

        await hub.StartStream(channel.Id);
        _sent.Clear();
        await hub.StopStream();

        var (_, args) = GetLastSent($"channel-{channel.Id}", "StreamNotification");
        var text = (string)args[1];
        Assert.Contains("general", text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(user.Username, text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task StartStream_NotifiesAllTextChannels_WhenMultipleExist()
    {
        var (server, user) = await TestHelpers.SeedServerAsync(_testDb.Db, "notif-multi");
        // SeedServerAsync creates "general" text + "General Voice" voice; add a second text channel
        var announcements = new Channel { Name = "announcements", Type = ChannelType.Text, ServerId = server.Id, Position = 2 };
        _testDb.Db.Channels.Add(announcements);
        await _testDb.Db.SaveChangesAsync();

        var general = _testDb.Db.Channels.First(c => c.ServerId == server.Id && c.Name == "general");
        await CreateHub(user.Id, user.Username, "conn-notif-multi").StartStream(general.Id);

        Assert.True(WasSentTo($"group:channel-{general.Id}", "StreamNotification"),
            "StreamNotification must reach the #general channel");
        Assert.True(WasSentTo($"group:channel-{announcements.Id}", "StreamNotification"),
            "StreamNotification must reach the #announcements channel too");
    }

    [Fact]
    public async Task StartStream_DoesNotNotifyVoiceChannels()
    {
        var (server, user) = await TestHelpers.SeedServerAsync(_testDb.Db, "notif-voice");
        var general = _testDb.Db.Channels.First(c => c.ServerId == server.Id && c.Name == "general");
        var voice = _testDb.Db.Channels.First(c => c.ServerId == server.Id && c.Type == ChannelType.Voice);

        await CreateHub(user.Id, user.Username, "conn-notif-voice").StartStream(general.Id);

        Assert.True(WasNeverSentTo($"group:channel-{voice.Id}", "StreamNotification"),
            "Voice channels must not receive StreamNotification — they have no chat feed");
    }

    [Fact]
    public async Task StreamNotification_Payload_ContainsChannelIdAndText()
    {
        var (server, user) = await TestHelpers.SeedServerAsync(_testDb.Db, "notif-payload");
        var channel = _testDb.Db.Channels.First(c => c.ServerId == server.Id && c.Name == "general");

        await CreateHub(user.Id, user.Username, "conn-notif-payload").StartStream(channel.Id);

        var (_, args) = GetLastSent($"channel-{channel.Id}", "StreamNotification");
        Assert.Equal(channel.Id, (int)args[0]);
        Assert.False(string.IsNullOrWhiteSpace((string)args[1]));
    }

    // ── StartStream ──────────────────────────────────────────────────────────

    [Fact]
    public async Task StartStream_BroadcastsStreamStarted_ToServerGroup()
    {
        var (server, user) = await TestHelpers.SeedServerAsync(_testDb.Db, "alice");
        var channel = _testDb.Db.Channels.First(c => c.ServerId == server.Id && c.Name == "general");

        await CreateHub(user.Id, user.Username, "conn-alice").StartStream(channel.Id);

        Assert.True(WasSentTo($"group:server-{server.Id}", "StreamStarted"),
            "StreamStarted must be broadcast to server group so all server members see it");
    }

    [Fact]
    public async Task StartStream_StreamStarted_ContainsCorrectPayload()
    {
        var (server, user) = await TestHelpers.SeedServerAsync(_testDb.Db, "bob");
        var channel = _testDb.Db.Channels.First(c => c.ServerId == server.Id && c.Name == "general");

        await CreateHub(user.Id, user.Username, "conn-bob").StartStream(channel.Id);

        var (_, args) = GetLastSent($"server-{server.Id}", "StreamStarted");
        Assert.Equal(user.Id, (int)args[0]);
        Assert.Equal(user.Username, (string)args[1]);
        Assert.Equal(channel.Id, (int)args[2]);
    }

    [Fact]
    public async Task StartStream_DoesNotBroadcastToChannelGroup()
    {
        var (server, user) = await TestHelpers.SeedServerAsync(_testDb.Db, "carol");
        var channel = _testDb.Db.Channels.First(c => c.ServerId == server.Id && c.Name == "general");

        await CreateHub(user.Id, user.Username).StartStream(channel.Id);

        Assert.True(WasNeverSentTo($"group:channel-{channel.Id}", "StreamStarted"),
            "StreamStarted must NOT go to channel group — old behavior that broke cross-channel visibility");
    }

    [Fact]
    public async Task StartStream_AddsStreamerConnectionToStreamGroup()
    {
        var (server, user) = await TestHelpers.SeedServerAsync(_testDb.Db, "dave");
        var channel = _testDb.Db.Channels.First(c => c.ServerId == server.Id && c.Name == "general");

        await CreateHub(user.Id, user.Username, "conn-dave").StartStream(channel.Id);

        _mockGroups.Verify(
            g => g.AddToGroupAsync("conn-dave", $"stream-{channel.Id}", default),
            Times.Once());
    }

    [Fact]
    public async Task StartStream_NonMember_DoesNotBroadcastStreamStarted()
    {
        var (server, _) = await TestHelpers.SeedServerAsync(_testDb.Db, "owner");
        var channel = _testDb.Db.Channels.First(c => c.ServerId == server.Id && c.Name == "general");

        var outsider = new User { Username = "outsider", Email = "out@test.com", PasswordHash = "*" };
        _testDb.Db.Users.Add(outsider);
        await _testDb.Db.SaveChangesAsync();

        await CreateHub(outsider.Id, outsider.Username).StartStream(channel.Id);

        Assert.True(WasNeverSentTo($"group:server-{server.Id}", "StreamStarted"),
            "Non-member must not be able to start a stream");
    }

    [Fact]
    public async Task StartStream_WhenAlreadyStreaming_SendsStoppedBeforeStarted()
    {
        var (server, user) = await TestHelpers.SeedServerAsync(_testDb.Db, "eve");
        var channel = _testDb.Db.Channels.First(c => c.ServerId == server.Id && c.Name == "general");
        var hub = CreateHub(user.Id, user.Username, "conn-eve");

        await hub.StartStream(channel.Id);
        _sent.Clear();
        await hub.StartStream(channel.Id);

        Assert.True(WasSentTo($"group:server-{server.Id}", "StreamStopped"),
            "Replacing an existing stream must send StreamStopped first");
        Assert.True(WasSentTo($"group:server-{server.Id}", "StreamStarted"),
            "Then must send StreamStarted for the new stream");
    }

    // ── StopStream ───────────────────────────────────────────────────────────

    [Fact]
    public async Task StopStream_BroadcastsStreamStopped_ToServerGroup()
    {
        var (server, user) = await TestHelpers.SeedServerAsync(_testDb.Db, "frank");
        var channel = _testDb.Db.Channels.First(c => c.ServerId == server.Id && c.Name == "general");
        var hub = CreateHub(user.Id, user.Username, "conn-frank");

        await hub.StartStream(channel.Id);
        _sent.Clear();
        await hub.StopStream();

        Assert.True(WasSentTo($"group:server-{server.Id}", "StreamStopped"),
            "StreamStopped must be broadcast to server group so all server members can clear stream tab");
    }

    [Fact]
    public async Task StopStream_StreamStopped_ContainsCorrectPayload()
    {
        var (server, user) = await TestHelpers.SeedServerAsync(_testDb.Db, "grace");
        var channel = _testDb.Db.Channels.First(c => c.ServerId == server.Id && c.Name == "general");
        var hub = CreateHub(user.Id, user.Username, "conn-grace");

        await hub.StartStream(channel.Id);
        _sent.Clear();
        await hub.StopStream();

        var (_, args) = GetLastSent($"server-{server.Id}", "StreamStopped");
        Assert.Equal(user.Id, (int)args[0]);
        Assert.Equal(channel.Id, (int)args[1]);
    }

    [Fact]
    public async Task StopStream_WhenNotStreaming_SendsNothing()
    {
        var (_, user) = await TestHelpers.SeedServerAsync(_testDb.Db, "henry");

        await CreateHub(user.Id, user.Username).StopStream();

        Assert.False(_sent.Any(kv => kv.Value.Any(m => m.Method == "StreamStopped")),
            "StopStream on a non-streaming user must be a no-op");
    }

    [Fact]
    public async Task StopStream_RemovesConnectionFromStreamGroup()
    {
        var (server, user) = await TestHelpers.SeedServerAsync(_testDb.Db, "iris");
        var channel = _testDb.Db.Channels.First(c => c.ServerId == server.Id && c.Name == "general");
        var hub = CreateHub(user.Id, user.Username, "conn-iris");

        await hub.StartStream(channel.Id);
        _mockGroups.Invocations.Clear();
        await hub.StopStream();

        _mockGroups.Verify(
            g => g.RemoveFromGroupAsync("conn-iris", $"stream-{channel.Id}", default),
            Times.Once());
    }

    // ── WatchStream / StopWatching ───────────────────────────────────────────

    [Fact]
    public async Task WatchStream_AddsViewerConnectionToStreamGroup()
    {
        var (server, user) = await TestHelpers.SeedServerAsync(_testDb.Db, "jack");
        var channel = _testDb.Db.Channels.First(c => c.ServerId == server.Id && c.Name == "general");

        await CreateHub(user.Id, user.Username, "conn-jack").WatchStream(channel.Id);

        _mockGroups.Verify(
            g => g.AddToGroupAsync("conn-jack", $"stream-{channel.Id}", default),
            Times.Once());
    }

    [Fact]
    public async Task StopWatching_RemovesViewerConnectionFromStreamGroup()
    {
        var (server, user) = await TestHelpers.SeedServerAsync(_testDb.Db, "kate");
        var channel = _testDb.Db.Channels.First(c => c.ServerId == server.Id && c.Name == "general");
        var hub = CreateHub(user.Id, user.Username, "conn-kate");

        await hub.WatchStream(channel.Id);
        _mockGroups.Invocations.Clear();
        await hub.StopWatching(channel.Id);

        _mockGroups.Verify(
            g => g.RemoveFromGroupAsync("conn-kate", $"stream-{channel.Id}", default),
            Times.Once());
    }

    // ── JoinChannel auto-stream ───────────────────────────────────────────────

    [Fact]
    public async Task JoinChannel_WhenStreamAlreadyActive_AutoJoinsStreamGroup()
    {
        var (server, streamer) = await TestHelpers.SeedServerAsync(_testDb.Db, "leo");
        var channel = _testDb.Db.Channels.First(c => c.ServerId == server.Id && c.Name == "general");

        var viewer = new User { Username = "viewer-l", Email = "viewer-l@test.com", PasswordHash = "*" };
        _testDb.Db.Users.Add(viewer);
        await _testDb.Db.SaveChangesAsync();
        _testDb.Db.ServerMembers.Add(new ServerMember { ServerId = server.Id, UserId = viewer.Id, Role = ServerRole.Member });
        await _testDb.Db.SaveChangesAsync();

        await CreateHub(streamer.Id, streamer.Username, "conn-leo").StartStream(channel.Id);
        _mockGroups.Invocations.Clear();

        await CreateHub(viewer.Id, viewer.Username, "conn-viewer-l").JoinChannel(channel.Id);

        _mockGroups.Verify(
            g => g.AddToGroupAsync("conn-viewer-l", $"stream-{channel.Id}", default),
            Times.Once(),
            "Viewer who joins the streaming channel should automatically be added to the stream group");
    }

    [Fact]
    public async Task JoinChannel_WhenStreamAlreadyActive_NotifiesCallerWithStreamStarted()
    {
        var (server, streamer) = await TestHelpers.SeedServerAsync(_testDb.Db, "mia");
        var channel = _testDb.Db.Channels.First(c => c.ServerId == server.Id && c.Name == "general");

        var viewer = new User { Username = "viewer-m", Email = "viewer-m@test.com", PasswordHash = "*" };
        _testDb.Db.Users.Add(viewer);
        await _testDb.Db.SaveChangesAsync();
        _testDb.Db.ServerMembers.Add(new ServerMember { ServerId = server.Id, UserId = viewer.Id, Role = ServerRole.Member });
        await _testDb.Db.SaveChangesAsync();

        await CreateHub(streamer.Id, streamer.Username, "conn-mia").StartStream(channel.Id);
        _sent.Clear();

        await CreateHub(viewer.Id, viewer.Username, "conn-viewer-m").JoinChannel(channel.Id);

        Assert.True(WasSentTo("caller", "StreamStarted"),
            "Viewer joining a channel with an active stream must receive StreamStarted from Caller");
    }

    // ── OnConnectedAsync ─────────────────────────────────────────────────────

    [Fact]
    public async Task OnConnected_AddsConnectionToServerGroup_SoItReceivesServerBroadcasts()
    {
        var (server, user) = await TestHelpers.SeedServerAsync(_testDb.Db, "nina");

        await CreateHub(user.Id, user.Username, "conn-nina").OnConnectedAsync();

        _mockGroups.Verify(
            g => g.AddToGroupAsync("conn-nina", $"server-{server.Id}", default),
            Times.Once(),
            "Connection must join server group on connect so it can receive StreamStarted/StreamStopped broadcasts");
    }

    [Fact]
    public async Task OnConnected_MultipleServers_AddsToAllServerGroups()
    {
        var (server1, user) = await TestHelpers.SeedServerAsync(_testDb.Db, "oscar");
        var server2 = new GuildServer { Name = "Second Server", OwnerId = user.Id };
        _testDb.Db.Servers.Add(server2);
        await _testDb.Db.SaveChangesAsync();
        _testDb.Db.ServerMembers.Add(new ServerMember { ServerId = server2.Id, UserId = user.Id, Role = ServerRole.Member });
        await _testDb.Db.SaveChangesAsync();

        await CreateHub(user.Id, user.Username, "conn-oscar").OnConnectedAsync();

        _mockGroups.Verify(g => g.AddToGroupAsync("conn-oscar", $"server-{server1.Id}", default), Times.Once());
        _mockGroups.Verify(g => g.AddToGroupAsync("conn-oscar", $"server-{server2.Id}", default), Times.Once());
    }

    // ── SendStreamFrame ───────────────────────────────────────────────────────

    [Fact]
    public async Task SendStreamFrame_WhenStreaming_ForwardsToStreamGroup()
    {
        var (server, user) = await TestHelpers.SeedServerAsync(_testDb.Db, "petra");
        var channel = _testDb.Db.Channels.First(c => c.ServerId == server.Id && c.Name == "general");
        var hub = CreateHub(user.Id, user.Username, "conn-petra");

        await hub.StartStream(channel.Id);
        _sent.Clear();
        await hub.SendStreamFrame(new byte[100]);

        Assert.True(WasSentTo($"others:stream-{channel.Id}", "ReceiveStreamFrame"),
            "Frame data must be forwarded to stream group viewers");
    }

    [Fact]
    public async Task SendStreamFrame_WhenNotStreaming_DropsSilently()
    {
        var (_, user) = await TestHelpers.SeedServerAsync(_testDb.Db, "quinn");

        await CreateHub(user.Id, user.Username).SendStreamFrame(new byte[100]);

        Assert.False(_sent.Any(kv => kv.Value.Any(m => m.Method == "ReceiveStreamFrame")),
            "Frame must be dropped when user is not actively streaming");
    }

    [Fact]
    public async Task SendStreamFrame_ExceedingMaxSize_DropsSilently()
    {
        var (server, user) = await TestHelpers.SeedServerAsync(_testDb.Db, "robin");
        var channel = _testDb.Db.Channels.First(c => c.ServerId == server.Id && c.Name == "general");
        var hub = CreateHub(user.Id, user.Username, "conn-robin");

        await hub.StartStream(channel.Id);
        _sent.Clear();
        await hub.SendStreamFrame(new byte[5 * 1024 * 1024]); // 5 MB > 4 MB limit

        Assert.False(_sent.Any(kv => kv.Value.Any(m => m.Method == "ReceiveStreamFrame")),
            "Oversized frames must be dropped to prevent DoS");
    }

    // ── SendStreamAudio ───────────────────────────────────────────────────────

    [Fact]
    public async Task SendStreamAudio_WhenStreaming_ForwardsToStreamGroupWithStreamerId()
    {
        var (server, user) = await TestHelpers.SeedServerAsync(_testDb.Db, "sam");
        var channel = _testDb.Db.Channels.First(c => c.ServerId == server.Id && c.Name == "general");
        var hub = CreateHub(user.Id, user.Username, "conn-sam");

        await hub.StartStream(channel.Id);
        _sent.Clear();
        var audioData = new byte[100];
        await hub.SendStreamAudio(audioData);

        Assert.True(WasSentTo($"others:stream-{channel.Id}", "ReceiveStreamAudio"),
            "Audio must be forwarded to stream group viewers");
        var (_, args) = _sent[$"others:stream-{channel.Id}"].Last(m => m.Method == "ReceiveStreamAudio");
        Assert.Equal(user.Id, (int)args[0]);
        Assert.Equal(audioData, (byte[])args[1]);
    }

    [Fact]
    public async Task SendStreamAudio_WhenNotStreaming_DropsSilently()
    {
        var (_, user) = await TestHelpers.SeedServerAsync(_testDb.Db, "tess");

        await CreateHub(user.Id, user.Username).SendStreamAudio(new byte[100]);

        Assert.False(_sent.Any(kv => kv.Value.Any(m => m.Method == "ReceiveStreamAudio")),
            "Audio must be dropped when user is not actively streaming");
    }

    [Fact]
    public async Task SendStreamAudio_OversizedPacket_DropsSilently()
    {
        var (server, user) = await TestHelpers.SeedServerAsync(_testDb.Db, "uma");
        var channel = _testDb.Db.Channels.First(c => c.ServerId == server.Id && c.Name == "general");
        var hub = CreateHub(user.Id, user.Username, "conn-uma");

        await hub.StartStream(channel.Id);
        _sent.Clear();
        await hub.SendStreamAudio(new byte[1276]); // 1276 > 1275 max Opus packet

        Assert.False(_sent.Any(kv => kv.Value.Any(m => m.Method == "ReceiveStreamAudio")),
            "Oversized audio packets must be dropped");
    }
}
