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
public class CameraRelayTests : IDisposable
{
    private readonly TestDb _testDb;
    private readonly Mock<IHubCallerClients> _mockClients;
    private readonly Mock<IGroupManager> _mockGroups;
    private readonly Dictionary<string, List<(string Method, object[] Args)>> _sent = new();

    public CameraRelayTests()
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
        foreach (var name in new[] { "ActiveStreamers", "VoiceChannelMap", "UserTextChannelMap",
                                     "ChannelOccupants", "OnlineUsers", "ActiveCameras" })
        {
            var field = typeof(ChatHub).GetField(name, flags);
            (field?.GetValue(null) as System.Collections.IDictionary)?.Clear();
        }
    }

    // ── StartCamera ───────────────────────────────────────────────────────────

    [Fact]
    public async Task StartCamera_WhenInVoiceChannel_BroadcastsCameraStartedToOthers()
    {
        var (server, user) = await TestHelpers.SeedServerAsync(_testDb.Db, "cam-start");
        var voiceChannel = _testDb.Db.Channels.First(c => c.ServerId == server.Id && c.Type == ChannelType.Voice);
        var hub = CreateHub(user.Id, user.Username, "conn-cam-start");

        await hub.JoinVoiceChannel(voiceChannel.Id);
        _sent.Clear();
        await hub.StartCamera(voiceChannel.Id);

        Assert.True(WasSentTo($"others:voice-{voiceChannel.Id}", "CameraStarted"),
            "StartCamera must notify others in the voice group so they can add a video tile");
    }

    [Fact]
    public async Task StartCamera_CameraStarted_ContainsCorrectUserIdAndChannelId()
    {
        var (server, user) = await TestHelpers.SeedServerAsync(_testDb.Db, "cam-args");
        var voiceChannel = _testDb.Db.Channels.First(c => c.ServerId == server.Id && c.Type == ChannelType.Voice);
        var hub = CreateHub(user.Id, user.Username, "conn-cam-args");

        await hub.JoinVoiceChannel(voiceChannel.Id);
        _sent.Clear();
        await hub.StartCamera(voiceChannel.Id);

        var (_, args) = GetLastSent($"others:voice-{voiceChannel.Id}", "CameraStarted");
        Assert.Equal(user.Id, (int)args[0]);
        Assert.Equal(user.Username, (string)args[1]);
        Assert.Equal(voiceChannel.Id, (int)args[2]);
    }

    [Fact]
    public async Task StartCamera_WhenNotInVoiceChannel_SendsNothing()
    {
        var (server, user) = await TestHelpers.SeedServerAsync(_testDb.Db, "cam-notinvoice");
        var voiceChannel = _testDb.Db.Channels.First(c => c.ServerId == server.Id && c.Type == ChannelType.Voice);

        await CreateHub(user.Id, user.Username).StartCamera(voiceChannel.Id);

        Assert.False(_sent.Any(kv => kv.Value.Any()),
            "StartCamera must be a no-op when the user is not in a voice channel");
    }

    [Fact]
    public async Task StartCamera_WrongChannel_SendsNothing()
    {
        var (server, user) = await TestHelpers.SeedServerAsync(_testDb.Db, "cam-wrongch");
        var voiceChannel = _testDb.Db.Channels.First(c => c.ServerId == server.Id && c.Type == ChannelType.Voice);
        var hub = CreateHub(user.Id, user.Username, "conn-wrongch");

        await hub.JoinVoiceChannel(voiceChannel.Id);
        _sent.Clear();
        await hub.StartCamera(voiceChannel.Id + 999); // wrong channel

        Assert.False(_sent.Any(kv => kv.Value.Any()),
            "StartCamera must reject a channelId that doesn't match the user's current voice channel");
    }

    // ── StopCamera ────────────────────────────────────────────────────────────

    [Fact]
    public async Task StopCamera_AfterStartCamera_BroadcastsCameraStoppedToOthers()
    {
        var (server, user) = await TestHelpers.SeedServerAsync(_testDb.Db, "cam-stop");
        var voiceChannel = _testDb.Db.Channels.First(c => c.ServerId == server.Id && c.Type == ChannelType.Voice);
        var hub = CreateHub(user.Id, user.Username, "conn-cam-stop");

        await hub.JoinVoiceChannel(voiceChannel.Id);
        await hub.StartCamera(voiceChannel.Id);
        _sent.Clear();
        await hub.StopCamera(voiceChannel.Id);

        Assert.True(WasSentTo($"others:voice-{voiceChannel.Id}", "CameraStopped"),
            "StopCamera must notify others so they remove the video tile");
    }

    [Fact]
    public async Task StopCamera_CameraStopped_ContainsCorrectUserIdAndChannelId()
    {
        var (server, user) = await TestHelpers.SeedServerAsync(_testDb.Db, "cam-stop-args");
        var voiceChannel = _testDb.Db.Channels.First(c => c.ServerId == server.Id && c.Type == ChannelType.Voice);
        var hub = CreateHub(user.Id, user.Username, "conn-stop-args");

        await hub.JoinVoiceChannel(voiceChannel.Id);
        await hub.StartCamera(voiceChannel.Id);
        _sent.Clear();
        await hub.StopCamera(voiceChannel.Id);

        var (_, args) = GetLastSent($"others:voice-{voiceChannel.Id}", "CameraStopped");
        Assert.Equal(user.Id, (int)args[0]);
        Assert.Equal(voiceChannel.Id, (int)args[1]);
    }

    // ── SendCameraFrame ───────────────────────────────────────────────────────

    [Fact]
    public async Task SendCameraFrame_WhenCameraActive_RelaysFrameToOthers()
    {
        var (server, user) = await TestHelpers.SeedServerAsync(_testDb.Db, "cam-frame");
        var voiceChannel = _testDb.Db.Channels.First(c => c.ServerId == server.Id && c.Type == ChannelType.Voice);
        var hub = CreateHub(user.Id, user.Username, "conn-cam-frame");

        await hub.JoinVoiceChannel(voiceChannel.Id);
        await hub.StartCamera(voiceChannel.Id);
        _sent.Clear();
        await hub.SendCameraFrame(new byte[1_024]);

        Assert.True(WasSentTo($"others:voice-{voiceChannel.Id}", "ReceiveCameraFrame"),
            "Camera frames must be relayed to others in the voice group");
    }

    [Fact]
    public async Task SendCameraFrame_Frame_ContainsSenderUserId()
    {
        var (server, user) = await TestHelpers.SeedServerAsync(_testDb.Db, "cam-frame-id");
        var voiceChannel = _testDb.Db.Channels.First(c => c.ServerId == server.Id && c.Type == ChannelType.Voice);
        var hub = CreateHub(user.Id, user.Username, "conn-frame-id");

        await hub.JoinVoiceChannel(voiceChannel.Id);
        await hub.StartCamera(voiceChannel.Id);
        _sent.Clear();
        await hub.SendCameraFrame(new byte[512]);

        var (_, args) = GetLastSent($"others:voice-{voiceChannel.Id}", "ReceiveCameraFrame");
        Assert.Equal(user.Id, (int)args[0]);
    }

    [Fact]
    public async Task SendCameraFrame_WhenCameraNotStarted_DropsFrame()
    {
        var (server, user) = await TestHelpers.SeedServerAsync(_testDb.Db, "cam-nocam");
        var voiceChannel = _testDb.Db.Channels.First(c => c.ServerId == server.Id && c.Type == ChannelType.Voice);
        var hub = CreateHub(user.Id, user.Username, "conn-nocam");

        await hub.JoinVoiceChannel(voiceChannel.Id);
        _sent.Clear();
        await hub.SendCameraFrame(new byte[512]); // camera not started

        Assert.False(_sent.Any(kv => kv.Value.Any(m => m.Method == "ReceiveCameraFrame")),
            "Frames must be dropped when the user hasn't called StartCamera");
    }

    [Fact]
    public async Task SendCameraFrame_OversizedFrame_DropsWithoutRelay()
    {
        var (server, user) = await TestHelpers.SeedServerAsync(_testDb.Db, "cam-bigframe");
        var voiceChannel = _testDb.Db.Channels.First(c => c.ServerId == server.Id && c.Type == ChannelType.Voice);
        var hub = CreateHub(user.Id, user.Username, "conn-bigframe");

        await hub.JoinVoiceChannel(voiceChannel.Id);
        await hub.StartCamera(voiceChannel.Id);
        _sent.Clear();
        await hub.SendCameraFrame(new byte[65 * 1024]); // > 64 KB limit

        Assert.False(_sent.Any(kv => kv.Value.Any(m => m.Method == "ReceiveCameraFrame")),
            "Frames exceeding 64 KB must be dropped to prevent abuse");
    }

    // ── Camera cleanup on leave ───────────────────────────────────────────────

    [Fact]
    public async Task LeaveVoiceChannel_WhileCameraOn_BroadcastsCameraStopped()
    {
        var (server, user) = await TestHelpers.SeedServerAsync(_testDb.Db, "cam-leave");
        var voiceChannel = _testDb.Db.Channels.First(c => c.ServerId == server.Id && c.Type == ChannelType.Voice);
        var hub = CreateHub(user.Id, user.Username, "conn-cam-leave");

        await hub.JoinVoiceChannel(voiceChannel.Id);
        await hub.StartCamera(voiceChannel.Id);
        _sent.Clear();
        await hub.LeaveVoiceChannel();

        Assert.True(WasSentTo($"others:voice-{voiceChannel.Id}", "CameraStopped"),
            "Leaving voice while camera is on must automatically stop the camera and notify others");
    }
}
