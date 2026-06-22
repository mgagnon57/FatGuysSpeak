using FatGuysSpeak.Shared;
using Microsoft.AspNetCore.SignalR.Client;

namespace FatGuysSpeak.Client.Services;

public class ChatHubService
{
    private HubConnection? _connection;

    public event Action<MessageDto>? MessageReceived;
    public event Action<VoiceStateDto>? UserJoinedVoice;
    public event Action<VoiceStateDto>? UserLeftVoice;
    public event Action<UserDto>? UserConnected;
    public event Action<UserDto>? UserDisconnected;
    public event Action<UserDto>? UserJoinedServer;
    public event Action<ChannelDto>? ChannelCreated;
    public event Action<ChannelDto>? ChannelUpdated;
    public event Action<int>? ChannelDeleted;
    public event Action<int>? ForceJoinChannel;
    public event Action<int, UserDto>? UserJoinedChannel;
    public event Action<int, UserDto>? UserLeftChannel;
    public event Action<byte[]>? VoiceDataReceived;
    // PorkChop's spoken (TTS) audio comes on its own event so the client can drop it when the
    // user has muted PorkChop's voice — including server-initiated speech like idle/join roasts.
    public event Action<byte[]>? BotVoiceDataReceived;
    public event Action<int, string>? OfferReceived;
    public event Action<int, string>? AnswerReceived;
    public event Action<int, string>? IceCandidateReceived;
    public event Action<int, string, int>? StreamStarted;   // (streamerId, streamerName, channelId)
    public event Action<int, int>? StreamStopped;            // (streamerId, channelId)
    public event Action<byte[]>? StreamFrameReceived;
    public event Action<int, byte[]>? StreamAudioReceived;   // (streamerId, opusData)
    public event Action<int, string>? StreamNotification;   // (channelId, text)
    public event Action<int, string, int>? UserTyping;       // (userId, username, channelId)
    public event Action<int, int>? UserStoppedTyping;        // (userId, channelId)
    public event Action<MessageDto>? MessageEdited;
    public event Action<int, int>? MessageDeleted;           // (messageId, channelId)
    public event Action<MessageDto>? NewMessageNotification; // server-wide, for unread badge tracking
    public event Action<ReactionsUpdatedDto>? ReactionsUpdated;
    public event Action<int, UserStatus, string?>? UserStatusChanged;  // (userId, newStatus, statusText)
    public event Action? KickedFromVoice;
    public event Action<int>? KickedFromServer;  // serverId
    public event Action<int>? UserSpeaking;  // userId
    public event Action<int, string, int>? CameraStarted;   // (userId, username, channelId)
    public event Action<int, int>? CameraStopped;            // (userId, channelId)
    public event Action<int, byte[]>? CameraFrameReceived;  // (userId, frame)
    public event Action<int, string>? ControlRequested;   // (controllerId, controllerName) — to streamer
    public event Action<int, string>? ControlOffered;     // (streamerId, streamerName) — to viewer
    public event Action<int, string>? ControlActive;      // (controllerId, controllerName) — to streamer
    public event Action<int, string>? ControlGranted;     // (streamerId, streamerName) — to controller
    public event Action<int>? ControlDeclined;            // (byUserId)
    public event Action? ControlBusy;
    public event Action<int>? ControlEnded;               // (otherUserId)
    public event Action<RemoteInputDto>? RemoteInputReceived;
    public event Action<DirectMessageDto>? DirectMessageReceived;
    public event Action<int, int>? DirectMessageDeleted;    // (conversationId, messageId)
    public event Action<int, string, int>? DmUserTyping;    // (userId, username, conversationId)
    public event Action<int, int>? DmUserStoppedTyping;     // (userId, conversationId)
    public event Action<int, int, DateTime>? DmConversationRead; // (conversationId, readByUserId, readAt)
    public event Action<int, int>? MessagePinned;              // (messageId, channelId)
    public event Action<int, int>? MessageUnpinned;            // (messageId, channelId)
    public event Action<int, int>? DmMessagePinned;            // (messageId, conversationId)
    public event Action<int, int>? DmMessageUnpinned;          // (messageId, conversationId)
    public event Action<CategoryDto>? CategoryCreated;
    public event Action<int>? CategoryDeleted;                 // categoryId
    public event Action<int, string>? CategoryRenamed;         // (categoryId, name)
    public event Action<int, int?>? ChannelCategoryChanged;   // (channelId, categoryId?)
    public event Action<int, string>? MemberRoleChanged;  // (userId, roleName e.g. "Admin")
    public event Action<int, int>? ChannelSlowmodeUpdated; // (channelId, slowmodeSeconds)
    public event Action<MessageDto, int, int>? ThreadReplyReceived; // (reply, rootMessageId, newReplyCount)
    public event Action<PollDto>? PollUpdated; // shared poll tallies (no per-user vote)
    public event Action<int, DateTime?>? UserMuted;   // (userId, mutedUntil — null means unmuted)
    public event Action<int, DateTime>? UserTempBanned; // (userId, expiresAt)
    public event Action<Exception?>? Reconnecting;
    public event Action<string?>?    Reconnected;
    public event Action<Exception?>? Disconnected;

    public bool IsConnected => _connection?.State == HubConnectionState.Connected;

    public async Task ConnectAsync(string token, string serverUrl)
    {
        _connection = new HubConnectionBuilder()
            .WithUrl($"{serverUrl.TrimEnd('/')}/hubs/chat?access_token={token}")
            .WithAutomaticReconnect()
            .Build();

        _connection.On<MessageDto>("ReceiveMessage", dto => MessageReceived?.Invoke(dto));
        _connection.On<VoiceStateDto>("UserJoinedVoice", dto => UserJoinedVoice?.Invoke(dto));
        _connection.On<VoiceStateDto>("UserLeftVoice", dto => UserLeftVoice?.Invoke(dto));
        _connection.On<UserDto>("UserConnected", dto => UserConnected?.Invoke(dto));
        _connection.On<UserDto>("UserDisconnected", dto => UserDisconnected?.Invoke(dto));
        _connection.On<UserDto>("UserJoinedServer", dto => UserJoinedServer?.Invoke(dto));
        _connection.On<ChannelDto>("ChannelCreated", dto => ChannelCreated?.Invoke(dto));
        _connection.On<ChannelDto>("ChannelUpdated", dto => ChannelUpdated?.Invoke(dto));
        _connection.On<int>("ChannelDeleted", id => ChannelDeleted?.Invoke(id));
        _connection.On<int>("ForceJoinChannel", id => ForceJoinChannel?.Invoke(id));
        _connection.On<int, UserDto>("UserJoinedChannel", (cid, dto) => UserJoinedChannel?.Invoke(cid, dto));
        _connection.On<int, UserDto>("UserLeftChannel", (cid, dto) => UserLeftChannel?.Invoke(cid, dto));
        _connection.On<byte[]>("ReceiveVoiceData", data => VoiceDataReceived?.Invoke(data));
        _connection.On<byte[]>("ReceiveBotVoice", data => BotVoiceDataReceived?.Invoke(data));
        _connection.On<int, string>("ReceiveOffer", (uid, sdp) => OfferReceived?.Invoke(uid, sdp));
        _connection.On<int, string>("ReceiveAnswer", (uid, sdp) => AnswerReceived?.Invoke(uid, sdp));
        _connection.On<int, string>("ReceiveIceCandidate", (uid, c) => IceCandidateReceived?.Invoke(uid, c));
        _connection.On<int, string, int>("StreamStarted", (uid, name, cid) => StreamStarted?.Invoke(uid, name, cid));
        _connection.On<int, int>("StreamStopped", (uid, cid) => StreamStopped?.Invoke(uid, cid));
        _connection.On<byte[]>("ReceiveStreamFrame", data => StreamFrameReceived?.Invoke(data));
        _connection.On<int, byte[]>("ReceiveStreamAudio", (uid, data) => StreamAudioReceived?.Invoke(uid, data));
        _connection.On<int, string>("StreamNotification", (cid, text) => StreamNotification?.Invoke(cid, text));
        _connection.On<int, string, int>("UserTyping", (uid, name, cid) => UserTyping?.Invoke(uid, name, cid));
        _connection.On<int, int>("UserStoppedTyping", (uid, cid) => UserStoppedTyping?.Invoke(uid, cid));
        _connection.On<MessageDto>("MessageEdited", dto => MessageEdited?.Invoke(dto));
        _connection.On<int, int>("MessageDeleted", (mid, cid) => MessageDeleted?.Invoke(mid, cid));
        _connection.On<MessageDto>("NewMessageNotification", dto => NewMessageNotification?.Invoke(dto));
        _connection.On<ReactionsUpdatedDto>("ReactionsUpdated", dto => ReactionsUpdated?.Invoke(dto));
        _connection.On<int, UserStatus, string?>("UserStatusChanged", (uid, s, txt) => UserStatusChanged?.Invoke(uid, s, txt));
        _connection.On("KickFromVoice", () => KickedFromVoice?.Invoke());
        _connection.On<int>("KickedFromServer", sid => KickedFromServer?.Invoke(sid));
        _connection.On<int>("UserSpeaking", uid => UserSpeaking?.Invoke(uid));
        _connection.On<int, string, int>("CameraStarted", (uid, name, cid) => CameraStarted?.Invoke(uid, name, cid));
        _connection.On<int, int>("CameraStopped", (uid, cid) => CameraStopped?.Invoke(uid, cid));
        _connection.On<int, byte[]>("ReceiveCameraFrame", (uid, frame) => CameraFrameReceived?.Invoke(uid, frame));
        _connection.On<DirectMessageDto>("ReceiveDirectMessage", dto => DirectMessageReceived?.Invoke(dto));
        _connection.On<int, int>("DirectMessageDeleted", (cid, mid) => DirectMessageDeleted?.Invoke(cid, mid));
        _connection.On<int, string, int>("DmUserTyping", (uid, name, cid) => DmUserTyping?.Invoke(uid, name, cid));
        _connection.On<int, int>("DmUserStoppedTyping", (uid, cid) => DmUserStoppedTyping?.Invoke(uid, cid));
        _connection.On<int, int, DateTime>("DmConversationRead", (cid, uid, at) => DmConversationRead?.Invoke(cid, uid, at));
        _connection.On<int, int>("MessagePinned",    (mid, cid) => MessagePinned?.Invoke(mid, cid));
        _connection.On<int, int>("MessageUnpinned",  (mid, cid) => MessageUnpinned?.Invoke(mid, cid));
        _connection.On<int, int>("DmMessagePinned",  (mid, cid) => DmMessagePinned?.Invoke(mid, cid));
        _connection.On<int, int>("DmMessageUnpinned",(mid, cid) => DmMessageUnpinned?.Invoke(mid, cid));
        _connection.On<CategoryDto>("CategoryCreated", dto => CategoryCreated?.Invoke(dto));
        _connection.On<int>("CategoryDeleted", id => CategoryDeleted?.Invoke(id));
        _connection.On<int, string>("CategoryRenamed", (id, name) => CategoryRenamed?.Invoke(id, name));
        _connection.On<int, int?>("ChannelCategoryChanged", (cid, catId) => ChannelCategoryChanged?.Invoke(cid, catId));
        _connection.On<int, string>("MemberRoleChanged",
            (uid, role) => MemberRoleChanged?.Invoke(uid, role));
        _connection.On<int, int>("ChannelSlowmodeUpdated",
            (cid, secs) => ChannelSlowmodeUpdated?.Invoke(cid, secs));
        _connection.On<MessageDto, int, int>("ThreadReplyReceived",
            (dto, rootId, count) => ThreadReplyReceived?.Invoke(dto, rootId, count));
        _connection.On<PollDto>("PollUpdated", dto => PollUpdated?.Invoke(dto));
        _connection.On<int, DateTime?>("UserMuted", (uid, until) => UserMuted?.Invoke(uid, until));
        _connection.On<int, DateTime>("UserTempBanned", (uid, exp) => UserTempBanned?.Invoke(uid, exp));
        _connection.On<int, string>("ControlRequested", (id, n) => ControlRequested?.Invoke(id, n));
        _connection.On<int, string>("ControlOffered",   (id, n) => ControlOffered?.Invoke(id, n));
        _connection.On<int, string>("ControlActive",    (id, n) => ControlActive?.Invoke(id, n));
        _connection.On<int, string>("ControlGranted",   (id, n) => ControlGranted?.Invoke(id, n));
        _connection.On<int>("ControlDeclined",          id => ControlDeclined?.Invoke(id));
        _connection.On("ControlBusy",                   () => ControlBusy?.Invoke());
        _connection.On<int>("ControlEnded",             id => ControlEnded?.Invoke(id));
        _connection.On<RemoteInputDto>("ReceiveRemoteInput", dto => RemoteInputReceived?.Invoke(dto));

        _connection.Reconnecting  += ex  => { Reconnecting?.Invoke(ex);  return Task.CompletedTask; };
        _connection.Reconnected   += cid => { Reconnected?.Invoke(cid);  return Task.CompletedTask; };
        _connection.Closed        += ex  => { Disconnected?.Invoke(ex);  return Task.CompletedTask; };

        await _connection.StartAsync();
    }

    public Task JoinChannelAsync(int channelId) =>
        _connection!.InvokeAsync("JoinChannel", channelId);

    public Task LeaveChannelAsync(int channelId) =>
        _connection!.InvokeAsync("LeaveChannel", channelId);

    public Task JoinVoiceChannelAsync(int channelId) =>
        _connection!.InvokeAsync("JoinVoiceChannel", channelId);

    public Task MoveUserToChannelAsync(int targetUserId, int channelId) =>
        _connection!.InvokeAsync("MoveUserToChannel", targetUserId, channelId);

    public Task SendVoiceDataAsync(byte[] data) =>
        _connection?.InvokeAsync("SendVoiceData", data) ?? Task.CompletedTask;

    public Task LeaveVoiceChannelAsync() =>
        _connection!.InvokeAsync("LeaveVoiceChannel");

    public Task SendOfferAsync(int targetUserId, string sdp) =>
        _connection!.InvokeAsync("SendOffer", targetUserId, sdp);

    public Task SendAnswerAsync(int targetUserId, string sdp) =>
        _connection!.InvokeAsync("SendAnswer", targetUserId, sdp);

    public Task SendIceCandidateAsync(int targetUserId, string candidate) =>
        _connection!.InvokeAsync("SendIceCandidate", targetUserId, candidate);

    public Task<List<UserDto>> GetOnlineUsersAsync(int serverId) =>
        _connection!.InvokeAsync<List<UserDto>>("GetOnlineUsersForServer", serverId);

    public Task<List<UserDto>> GetChannelOccupantsAsync(int channelId) =>
        _connection!.InvokeAsync<List<UserDto>>("GetChannelOccupants", channelId);

    public Task StartStreamAsync(int channelId) => _connection!.InvokeAsync("StartStream", channelId);
    public Task StopStreamAsync() => _connection!.InvokeAsync("StopStream");
    public Task SendStreamFrameAsync(byte[] data) => _connection?.SendAsync("SendStreamFrame", data) ?? Task.CompletedTask;
    public Task SendStreamAudioAsync(byte[] data) => _connection?.SendAsync("SendStreamAudio", data) ?? Task.CompletedTask;
    public Task WatchStreamAsync(int channelId) => _connection!.InvokeAsync("WatchStream", channelId);
    public Task StopWatchingAsync(int channelId) => _connection!.InvokeAsync("StopWatching", channelId);

    public Task RequestControl(int streamerId) => _connection!.InvokeAsync("RequestControl", streamerId);
    public Task OfferControl(int viewerId) => _connection!.InvokeAsync("OfferControl", viewerId);
    public Task GrantControl(int controllerId) => _connection!.InvokeAsync("GrantControl", controllerId);
    public Task AcceptControl(int streamerId) => _connection!.InvokeAsync("AcceptControl", streamerId);
    public Task DenyControl(int otherUserId) => _connection!.InvokeAsync("DenyControl", otherUserId);
    public Task StopControl() => _connection!.InvokeAsync("StopControl");
    public Task ReleaseControl() => _connection!.InvokeAsync("ReleaseControl");
    public Task SendRemoteInput(RemoteInputDto dto) => _connection!.SendAsync("SendRemoteInput", dto);

    public Task StartCameraAsync(int channelId) => _connection!.InvokeAsync("StartCamera", channelId);
    public Task StopCameraAsync(int channelId) => _connection!.InvokeAsync("StopCamera", channelId);
    public Task SendCameraFrameAsync(byte[] frame) =>
        _connection?.SendAsync("SendCameraFrame", frame) ?? Task.CompletedTask;

    public Task StartTypingAsync(int channelId) =>
        _connection?.SendAsync("StartTyping", channelId) ?? Task.CompletedTask;

    public Task StopTypingAsync(int channelId) =>
        _connection?.SendAsync("StopTyping", channelId) ?? Task.CompletedTask;

    public Task StartDmTypingAsync(int conversationId) =>
        _connection?.SendAsync("StartDmTyping", conversationId) ?? Task.CompletedTask;

    public Task StopDmTypingAsync(int conversationId) =>
        _connection?.SendAsync("StopDmTyping", conversationId) ?? Task.CompletedTask;

    public async Task<int> MeasureLatencyAsync()
    {
        if (_connection?.State != HubConnectionState.Connected) return -1;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        await _connection.InvokeAsync("Ping");
        return (int)sw.ElapsedMilliseconds;
    }

    public async Task DisconnectAsync()
    {
        if (_connection is not null)
            await _connection.DisposeAsync();
    }
}
