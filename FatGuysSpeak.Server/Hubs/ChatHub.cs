using System.Collections.Concurrent;
using System.Security.Claims;
using FatGuysSpeak.Server.Data;
using FatGuysSpeak.Shared;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;

namespace FatGuysSpeak.Server.Hubs;

[Authorize]
public class ChatHub(AppDbContext db) : Hub
{
    // userId -> voice channelId
    private static readonly ConcurrentDictionary<int, int> VoiceChannelMap = new();
    // userId -> text channelId
    private static readonly ConcurrentDictionary<int, int> UserTextChannelMap = new();
    // text channelId -> (userId -> username)
    private static readonly ConcurrentDictionary<int, ConcurrentDictionary<int, string>> ChannelOccupants = new();
    // userId -> username (currently connected users)
    private static readonly ConcurrentDictionary<int, string> OnlineUsers = new();
    // userId -> (channelId, serverId, username) for active screen shares
    private static readonly ConcurrentDictionary<int, (int ChannelId, int ServerId, string Username)> ActiveStreamers = new();
    // userId -> (channelId, username) for active webcam feeds
    private static readonly ConcurrentDictionary<int, (int ChannelId, string Username)> ActiveCameras = new();

    // Exposed for the metrics dashboard — read-only snapshots of live state
    internal static int OnlineUserCount       => OnlineUsers.Count;
    internal static int VoiceParticipantCount => VoiceChannelMap.Count;
    internal static int ActiveStreamCount     => ActiveStreamers.Count;
    internal static IReadOnlyDictionary<int, string> OnlineUserSnapshot => OnlineUsers;
    internal static IReadOnlyDictionary<int, int>    VoiceChannelSnapshot => VoiceChannelMap;

    private int UserId => int.Parse(Context.User!.FindFirstValue(ClaimTypes.NameIdentifier)!);
    private string Username => Context.User!.FindFirstValue(ClaimTypes.Name)!;

    public async Task JoinChannel(int channelId)
    {
        var channel = await db.Channels
            .Include(c => c.Server).ThenInclude(s => s.Members)
            .FirstOrDefaultAsync(c => c.Id == channelId);
        if (channel is null || !channel.Server.Members.Any(m => m.UserId == UserId))
            return;

        // Enforce channel read permission
        var perm = await db.ChannelPermissions.FindAsync(channelId);
        if (perm is not null && perm.MinRoleToRead > ServerRole.Member)
        {
            var member = channel.Server.Members.FirstOrDefault(m => m.UserId == UserId);
            if (member is null || member.Role < perm.MinRoleToRead) return;
        }

        // Atomically leave previous channel so a user can only be in one at a time
        if (UserTextChannelMap.TryRemove(UserId, out var oldChannelId))
        {
            if (ChannelOccupants.TryGetValue(oldChannelId, out var oldOccupants))
                oldOccupants.TryRemove(UserId, out _);
            // Broadcast before removing so the leaving user also receives and updates their own sidebar
            await Clients.Group($"channel-{oldChannelId}").SendAsync("UserLeftChannel", oldChannelId, new UserDto(UserId, Username, UserStatus.Online));
            await Groups.RemoveFromGroupAsync(Context.ConnectionId, $"channel-{oldChannelId}");
        }

        await Groups.AddToGroupAsync(Context.ConnectionId, $"channel-{channelId}");
        UserTextChannelMap[UserId] = channelId;
        ChannelOccupants.GetOrAdd(channelId, _ => new ConcurrentDictionary<int, string>())[UserId] = Username;
        await Clients.OthersInGroup($"channel-{channelId}").SendAsync("UserJoinedChannel", channelId, new UserDto(UserId, Username, UserStatus.Online));

        // If someone is already streaming this channel, auto-join the stream group and notify caller
        var streamer = ActiveStreamers.FirstOrDefault(kv => kv.Value.ChannelId == channelId);
        if (streamer.Key != 0)
        {
            await Groups.AddToGroupAsync(Context.ConnectionId, $"stream-{channelId}");
            await Clients.Caller.SendAsync("StreamStarted", streamer.Key, streamer.Value.Username, channelId);
        }
    }

    public async Task LeaveChannel(int channelId)
    {
        UserTextChannelMap.TryRemove(UserId, out _);
        if (ChannelOccupants.TryGetValue(channelId, out var occupants))
        {
            occupants.TryRemove(UserId, out _);
            // Broadcast before removing so the leaving user receives and updates their own sidebar
            await Clients.Group($"channel-{channelId}").SendAsync("UserLeftChannel", channelId, new UserDto(UserId, Username, UserStatus.Online));
        }
        await Groups.RemoveFromGroupAsync(Context.ConnectionId, $"channel-{channelId}");
    }

    public Task<List<UserDto>> GetChannelOccupants(int channelId)
    {
        if (!ChannelOccupants.TryGetValue(channelId, out var occupants))
            return Task.FromResult(new List<UserDto>());
        return Task.FromResult(occupants.Select(kv => new UserDto(kv.Key, kv.Value, UserStatus.Online)).ToList());
    }

    public async Task JoinVoiceChannel(int channelId)
    {
        var channel = await db.Channels
            .Include(c => c.Server).ThenInclude(s => s.Members)
            .FirstOrDefaultAsync(c => c.Id == channelId);

        if (channel is null || !channel.Server.Members.Any(m => m.UserId == UserId))
            return;

        if (VoiceChannelMap.TryGetValue(UserId, out var oldChannel))
        {
            await Groups.RemoveFromGroupAsync(Context.ConnectionId, $"voice-{oldChannel}");
            await Clients.Group($"voice-{oldChannel}").SendAsync("UserLeftVoice",
                new VoiceStateDto(UserId, Username, null, false, false));
        }

        VoiceChannelMap[UserId] = channelId;
        await Groups.AddToGroupAsync(Context.ConnectionId, $"voice-{channelId}");

        var state = new VoiceStateDto(UserId, Username, channelId, false, false);
        await Clients.Group($"voice-{channelId}").SendAsync("UserJoinedVoice", state);

        // Send existing voice participants to the new joiner
        var existing = VoiceChannelMap
            .Where(kv => kv.Value == channelId && kv.Key != UserId)
            .Select(kv => kv.Key)
            .ToList();
        await Clients.Caller.SendAsync("VoiceParticipants", existing);
    }

    private const int MaxVoicePacketBytes = 8_192;

    public async Task SendVoiceData(byte[] data)
    {
        if (data.Length > MaxVoicePacketBytes) return;
        if (!VoiceChannelMap.TryGetValue(UserId, out var channelId)) return;
        var group = Clients.OthersInGroup($"voice-{channelId}");
        await group.SendAsync("ReceiveVoiceData", data);
        await group.SendAsync("UserSpeaking", UserId);
    }

    public async Task LeaveVoiceChannel()
    {
        if (!VoiceChannelMap.TryRemove(UserId, out var channelId))
            return;

        if (ActiveCameras.TryRemove(UserId, out var cam) && cam.ChannelId == channelId)
            await Clients.OthersInGroup($"voice-{channelId}").SendAsync("CameraStopped", UserId, channelId);

        await Groups.RemoveFromGroupAsync(Context.ConnectionId, $"voice-{channelId}");
        await Clients.Group($"voice-{channelId}").SendAsync("UserLeftVoice",
            new VoiceStateDto(UserId, Username, null, false, false));
    }

    // ─── Webcam Video ─────────────────────────────────────────────────────────

    private const int MaxCameraFrameBytes = 64 * 1024; // 64 KB — 320×240 JPEG

    public async Task StartCamera(int channelId)
    {
        if (!VoiceChannelMap.TryGetValue(UserId, out var voiceCh) || voiceCh != channelId) return;
        ActiveCameras[UserId] = (channelId, Username);
        await Clients.OthersInGroup($"voice-{channelId}")
            .SendAsync("CameraStarted", UserId, Username, channelId);
    }

    public async Task StopCamera(int channelId)
    {
        ActiveCameras.TryRemove(UserId, out _);
        await Clients.OthersInGroup($"voice-{channelId}")
            .SendAsync("CameraStopped", UserId, channelId);
    }

    public async Task SendCameraFrame(byte[] frame)
    {
        if (frame.Length > MaxCameraFrameBytes) return;
        if (!ActiveCameras.TryGetValue(UserId, out var info)) return;
        await Clients.OthersInGroup($"voice-{info.ChannelId}")
            .SendAsync("ReceiveCameraFrame", UserId, frame);
    }

    public async Task<List<UserDto>> GetOnlineUsersForServer(int serverId)
    {
        if (!await db.ServerMembers.AnyAsync(sm => sm.ServerId == serverId && sm.UserId == UserId))
            return [];

        var memberIds = await db.ServerMembers
            .Where(sm => sm.ServerId == serverId)
            .Select(sm => sm.UserId)
            .ToListAsync();

        var onlineIds = memberIds.Where(id => OnlineUsers.ContainsKey(id)).ToList();
        var statusMap = await db.Users
            .Where(u => onlineIds.Contains(u.Id))
            .ToDictionaryAsync(u => u.Id, u => u.Status);

        return onlineIds
            .Select(id => new UserDto(id, OnlineUsers[id], statusMap.GetValueOrDefault(id, UserStatus.Online)))
            .ToList();
    }

    // WebRTC signaling relay — both parties must be in the same voice channel
    public async Task SendOffer(int targetUserId, string sdpOffer)
    {
        if (!VoiceChannelMap.TryGetValue(UserId, out var ch)) return;
        if (!VoiceChannelMap.TryGetValue(targetUserId, out var targetCh) || targetCh != ch) return;
        await Clients.User(targetUserId.ToString()).SendAsync("ReceiveOffer", UserId, sdpOffer);
    }

    public async Task SendAnswer(int targetUserId, string sdpAnswer)
    {
        if (!VoiceChannelMap.TryGetValue(UserId, out var ch)) return;
        if (!VoiceChannelMap.TryGetValue(targetUserId, out var targetCh) || targetCh != ch) return;
        await Clients.User(targetUserId.ToString()).SendAsync("ReceiveAnswer", UserId, sdpAnswer);
    }

    public async Task SendIceCandidate(int targetUserId, string candidate)
    {
        if (!VoiceChannelMap.TryGetValue(UserId, out var ch)) return;
        if (!VoiceChannelMap.TryGetValue(targetUserId, out var targetCh) || targetCh != ch) return;
        await Clients.User(targetUserId.ToString()).SendAsync("ReceiveIceCandidate", UserId, candidate);
    }

    public override async Task OnConnectedAsync()
    {
        OnlineUsers[UserId] = Username;

        var user = await db.Users.FindAsync(UserId);
        if (user is not null)
        {
            user.Status = UserStatus.Online;
            await db.SaveChangesAsync();
        }

        var serverIds = await db.ServerMembers
            .Where(sm => sm.UserId == UserId)
            .Select(sm => sm.ServerId)
            .ToListAsync();

        var userDto = new UserDto(UserId, Username, user?.Status ?? UserStatus.Online);
        foreach (var sid in serverIds)
        {
            await Groups.AddToGroupAsync(Context.ConnectionId, $"server-{sid}");
            await Clients.OthersInGroup($"server-{sid}").SendAsync("UserConnected", userDto);
        }

        // Personal group so DM notifications can be pushed to this user
        await Groups.AddToGroupAsync(Context.ConnectionId, $"user-{UserId}");

        await base.OnConnectedAsync();
    }

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        OnlineUsers.TryRemove(UserId, out _);

        if (UserTextChannelMap.TryRemove(UserId, out var textChannelId))
        {
            if (ChannelOccupants.TryGetValue(textChannelId, out var occ))
                occ.TryRemove(UserId, out _);
            await Clients.Group($"channel-{textChannelId}").SendAsync("UserLeftChannel", textChannelId, new UserDto(UserId, Username, UserStatus.Offline));
        }

        var serverIds = await db.ServerMembers
            .Where(sm => sm.UserId == UserId)
            .Select(sm => sm.ServerId)
            .ToListAsync();

        var userDto = new UserDto(UserId, Username, UserStatus.Offline);
        foreach (var sid in serverIds)
            await Clients.OthersInGroup($"server-{sid}").SendAsync("UserDisconnected", userDto);

        await LeaveVoiceChannel();
        await StopStream();
        if (ActiveCameras.TryRemove(UserId, out var camInfo))
            await Clients.OthersInGroup($"voice-{camInfo.ChannelId}")
                .SendAsync("CameraStopped", UserId, camInfo.ChannelId);
        await base.OnDisconnectedAsync(exception);
    }

    // ─── Screen Sharing ───────────────────────────────────────────────────────

    private const int MaxStreamFrameBytes = 4 * 1024 * 1024;

    public async Task StartStream(int channelId)
    {
        var channel = await db.Channels
            .Include(c => c.Server).ThenInclude(s => s.Members)
            .FirstOrDefaultAsync(c => c.Id == channelId);
        if (channel is null || !channel.Server.Members.Any(m => m.UserId == UserId))
            return;

        // Replace any previous stream from this user
        if (ActiveStreamers.TryRemove(UserId, out var prev))
        {
            await Groups.RemoveFromGroupAsync(Context.ConnectionId, $"stream-{prev.ChannelId}");
            await Clients.Group($"server-{prev.ServerId}").SendAsync("StreamStopped", UserId, prev.ChannelId);
        }

        ActiveStreamers[UserId] = (channelId, channel.ServerId, Username);
        await Groups.AddToGroupAsync(Context.ConnectionId, $"stream-{channelId}");
        // Notify all server members so clients on any channel see the stream tab light up
        await Clients.Group($"server-{channel.ServerId}").SendAsync("StreamStarted", UserId, Username, channelId);
        await BroadcastStreamNotificationAsync(channel.ServerId,
            $"📺 {Username} started streaming in #{channel.Name}");
    }

    public async Task SendStreamFrame(byte[] data)
    {
        if (data.Length > MaxStreamFrameBytes) return;
        if (!ActiveStreamers.TryGetValue(UserId, out var info)) return;
        await Clients.OthersInGroup($"stream-{info.ChannelId}").SendAsync("ReceiveStreamFrame", data);
    }

    public async Task StopStream()
    {
        if (!ActiveStreamers.TryRemove(UserId, out var info)) return;
        await Groups.RemoveFromGroupAsync(Context.ConnectionId, $"stream-{info.ChannelId}");
        await Clients.Group($"server-{info.ServerId}").SendAsync("StreamStopped", UserId, info.ChannelId);
        var channelName = (await db.Channels.FindAsync(info.ChannelId))?.Name ?? "";
        await BroadcastStreamNotificationAsync(info.ServerId,
            $"⏹ {Username} ended screen share" + (channelName.Length > 0 ? $" in #{channelName}" : ""));
    }

    public Task WatchStream(int channelId) =>
        Groups.AddToGroupAsync(Context.ConnectionId, $"stream-{channelId}");

    public Task StopWatching(int channelId) =>
        Groups.RemoveFromGroupAsync(Context.ConnectionId, $"stream-{channelId}");

    // ─── Typing Indicators ────────────────────────────────────────────────────

    public async Task StartTyping(int channelId)
    {
        if (!UserTextChannelMap.TryGetValue(UserId, out var current) || current != channelId) return;
        await Clients.OthersInGroup($"channel-{channelId}")
            .SendAsync("UserTyping", UserId, Username, channelId);
    }

    public async Task StopTyping(int channelId)
    {
        await Clients.OthersInGroup($"channel-{channelId}")
            .SendAsync("UserStoppedTyping", UserId, channelId);
    }

    public async Task StartDmTyping(int conversationId)
    {
        var convo = await db.DirectConversations.FindAsync(conversationId);
        if (convo is null) return;
        if (convo.User1Id != UserId && convo.User2Id != UserId) return;
        int recipientId = convo.User1Id == UserId ? convo.User2Id : convo.User1Id;
        await Clients.Group($"user-{recipientId}")
            .SendAsync("DmUserTyping", UserId, Username, conversationId);
    }

    public async Task StopDmTyping(int conversationId)
    {
        var convo = await db.DirectConversations.FindAsync(conversationId);
        if (convo is null) return;
        if (convo.User1Id != UserId && convo.User2Id != UserId) return;
        int recipientId = convo.User1Id == UserId ? convo.User2Id : convo.User1Id;
        await Clients.Group($"user-{recipientId}")
            .SendAsync("DmUserStoppedTyping", UserId, conversationId);
    }

    // Echo back immediately so the client can measure round-trip time
    public Task Ping() => Task.CompletedTask;

    // Sends a system notification message to every text channel on the server
    private async Task BroadcastStreamNotificationAsync(int serverId, string text)
    {
        var textChannels = await db.Channels
            .Where(c => c.ServerId == serverId && c.Type == ChannelType.Text)
            .ToListAsync();

        foreach (var ch in textChannels)
            await Clients.Group($"channel-{ch.Id}").SendAsync("StreamNotification", ch.Id, text);
    }
}
