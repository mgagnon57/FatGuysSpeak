using System.Reflection;
using System.Security.Claims;
using FatGuysSpeak.Server.Hubs;
using FatGuysSpeak.Server.Models;
using FatGuysSpeak.Shared;
using FatGuysSpeak.Tests.Helpers;
using Microsoft.AspNetCore.SignalR;
using Moq;

namespace FatGuysSpeak.Tests.Server;

[Collection("HubTests")]
public class ChatHubVoiceActivityTests : IDisposable
{
    private readonly TestDb _testDb;
    private readonly Mock<IHubCallerClients> _mockClients;
    private readonly Mock<IGroupManager> _mockGroups;
    private readonly Dictionary<string, List<(string Method, object[] Args)>> _sent = new();

    public ChatHubVoiceActivityTests()
    {
        _testDb = new TestDb();

        _mockClients = new Mock<IHubCallerClients>();
        _mockClients.Setup(c => c.OthersInGroup(It.IsAny<string>()))
            .Returns<string>(g => TrackingProxy($"others:{g}"));
        _mockClients.Setup(c => c.Group(It.IsAny<string>()))
            .Returns<string>(g => TrackingProxy($"group:{g}"));
        _mockClients.Setup(c => c.Caller)
            .Returns(SingleTrackingProxy("caller"));

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

    // ── Helpers ───────────────────────────────────────────────────────────────

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

    private bool WasSentTo(string target, string method) =>
        _sent.TryGetValue(target, out var list) && list.Any(m => m.Method == method);

    private (string Method, object[] Args) GetLastSent(string target, string method) =>
        _sent[target].Last(m => m.Method == method);

    private static void ClearHubStaticState()
    {
        var flags = BindingFlags.Static | BindingFlags.NonPublic;
        foreach (var name in new[] { "ActiveStreamers", "VoiceChannelMap", "UserTextChannelMap", "ChannelOccupants", "OnlineUsers" })
        {
            var field = typeof(ChatHub).GetField(name, flags);
            (field?.GetValue(null) as System.Collections.IDictionary)?.Clear();
        }
    }

    // ── SendVoiceData — audio relay ───────────────────────────────────────────

    [Fact]
    public async Task SendVoiceData_WhenInVoiceChannel_ForwardsAudioToOthers()
    {
        var (server, user) = await TestHelpers.SeedServerAsync(_testDb.Db, "sender-audio");
        var voiceChannel = _testDb.Db.Channels.First(c => c.ServerId == server.Id && c.Type == ChannelType.Voice);
        var hub = CreateHub(user.Id, user.Username, "conn-sender");

        await hub.JoinVoiceChannel(voiceChannel.Id);
        _sent.Clear();
        await hub.SendVoiceData(new byte[512]);

        Assert.True(WasSentTo($"others:voice-{voiceChannel.Id}", "ReceiveVoiceData"),
            "Audio bytes must be relayed to others in the voice channel");
    }

    [Fact]
    public async Task SendVoiceData_WhenInVoiceChannel_AlsoBroadcastsUserSpeaking()
    {
        var (server, user) = await TestHelpers.SeedServerAsync(_testDb.Db, "sender-speaking");
        var voiceChannel = _testDb.Db.Channels.First(c => c.ServerId == server.Id && c.Type == ChannelType.Voice);
        var hub = CreateHub(user.Id, user.Username, "conn-speaking");

        await hub.JoinVoiceChannel(voiceChannel.Id);
        _sent.Clear();
        await hub.SendVoiceData(new byte[512]);

        Assert.True(WasSentTo($"others:voice-{voiceChannel.Id}", "UserSpeaking"),
            "UserSpeaking must be broadcast alongside audio so clients can show the speaking indicator");
    }

    [Fact]
    public async Task SendVoiceData_UserSpeaking_ContainsSendersUserId()
    {
        var (server, user) = await TestHelpers.SeedServerAsync(_testDb.Db, "sender-id");
        var voiceChannel = _testDb.Db.Channels.First(c => c.ServerId == server.Id && c.Type == ChannelType.Voice);
        var hub = CreateHub(user.Id, user.Username, "conn-id");

        await hub.JoinVoiceChannel(voiceChannel.Id);
        _sent.Clear();
        await hub.SendVoiceData(new byte[512]);

        var (_, args) = GetLastSent($"others:voice-{voiceChannel.Id}", "UserSpeaking");
        Assert.Equal(user.Id, (int)args[0]);
    }

    [Fact]
    public async Task SendVoiceData_BothEventsTarget_SameVoiceGroup()
    {
        var (server, user) = await TestHelpers.SeedServerAsync(_testDb.Db, "sender-group");
        var voiceChannel = _testDb.Db.Channels.First(c => c.ServerId == server.Id && c.Type == ChannelType.Voice);
        var hub = CreateHub(user.Id, user.Username, "conn-group");

        await hub.JoinVoiceChannel(voiceChannel.Id);
        _sent.Clear();
        await hub.SendVoiceData(new byte[256]);

        var target = $"others:voice-{voiceChannel.Id}";
        Assert.True(WasSentTo(target, "ReceiveVoiceData"), "ReceiveVoiceData must go to the voice group");
        Assert.True(WasSentTo(target, "UserSpeaking"), "UserSpeaking must go to the same voice group");
    }

    [Fact]
    public async Task SendVoiceData_WhenNotInVoiceChannel_SendsNothing()
    {
        var (_, user) = await TestHelpers.SeedServerAsync(_testDb.Db, "sender-none");

        await CreateHub(user.Id, user.Username).SendVoiceData(new byte[512]);

        Assert.False(_sent.Any(kv => kv.Value.Any()),
            "User not in any voice channel must not be able to broadcast audio or speaking events");
    }

    [Fact]
    public async Task SendVoiceData_OversizedPacket_DropsWithoutBroadcast()
    {
        var (server, user) = await TestHelpers.SeedServerAsync(_testDb.Db, "sender-big");
        var voiceChannel = _testDb.Db.Channels.First(c => c.ServerId == server.Id && c.Type == ChannelType.Voice);
        var hub = CreateHub(user.Id, user.Username, "conn-big");

        await hub.JoinVoiceChannel(voiceChannel.Id);
        _sent.Clear();
        await hub.SendVoiceData(new byte[9_000]); // > 8_192 limit

        Assert.False(_sent.Any(kv => kv.Value.Any()),
            "Packets exceeding 8 KB must be dropped — no ReceiveVoiceData or UserSpeaking sent");
    }

    [Fact]
    public async Task SendVoiceData_DoesNotSendUserSpeaking_ToSelf()
    {
        var (server, user) = await TestHelpers.SeedServerAsync(_testDb.Db, "sender-self");
        var voiceChannel = _testDb.Db.Channels.First(c => c.ServerId == server.Id && c.Type == ChannelType.Voice);
        var hub = CreateHub(user.Id, user.Username, "conn-self");

        await hub.JoinVoiceChannel(voiceChannel.Id);
        _sent.Clear();
        await hub.SendVoiceData(new byte[512]);

        // The caller target must not have received UserSpeaking — the server uses OthersInGroup
        Assert.False(WasSentTo("caller", "UserSpeaking"),
            "The speaking sender must not receive their own UserSpeaking event back");
    }

    // ── JoinVoiceChannel — ensures VoiceChannelMap is populated ──────────────

    [Fact]
    public async Task JoinVoiceChannel_BroadcastsUserJoinedVoice_ToVoiceGroup()
    {
        var (server, user) = await TestHelpers.SeedServerAsync(_testDb.Db, "joiner");
        var voiceChannel = _testDb.Db.Channels.First(c => c.ServerId == server.Id && c.Type == ChannelType.Voice);

        await CreateHub(user.Id, user.Username, "conn-joiner").JoinVoiceChannel(voiceChannel.Id);

        Assert.True(WasSentTo($"group:voice-{voiceChannel.Id}", "UserJoinedVoice"),
            "JoinVoiceChannel must broadcast so clients can add the user to the voice strip");
    }

    [Fact]
    public async Task JoinVoiceChannel_NonMember_DoesNotBroadcast()
    {
        var (server, _) = await TestHelpers.SeedServerAsync(_testDb.Db, "owner-voice");
        var voiceChannel = _testDb.Db.Channels.First(c => c.ServerId == server.Id && c.Type == ChannelType.Voice);

        var outsider = new User { Username = "outsider-v", Email = "out-v@test.com", PasswordHash = "*" };
        _testDb.Db.Users.Add(outsider);
        await _testDb.Db.SaveChangesAsync();

        await CreateHub(outsider.Id, outsider.Username).JoinVoiceChannel(voiceChannel.Id);

        Assert.False(_sent.Any(kv => kv.Value.Any(m => m.Method == "UserJoinedVoice")),
            "Non-member must not be able to join a voice channel");
    }
}
