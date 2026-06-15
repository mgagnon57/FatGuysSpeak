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
    public event Action<int, UserDto>? UserJoinedChannel;
    public event Action<int, UserDto>? UserLeftChannel;
    public event Action<byte[]>? VoiceDataReceived;
    public event Action<int, string>? OfferReceived;
    public event Action<int, string>? AnswerReceived;
    public event Action<int, string>? IceCandidateReceived;
    public event Action<int, string, int>? StreamStarted;   // (streamerId, streamerName, channelId)
    public event Action<int, int>? StreamStopped;            // (streamerId, channelId)
    public event Action<byte[]>? StreamFrameReceived;
    public event Action<int, string>? StreamNotification;   // (channelId, text)
    public event Action<int, string, int>? UserTyping;       // (userId, username, channelId)
    public event Action<int, int>? UserStoppedTyping;        // (userId, channelId)
    public event Action<MessageDto>? MessageEdited;
    public event Action<int, int>? MessageDeleted;           // (messageId, channelId)
    public event Action<MessageDto>? NewMessageNotification; // server-wide, for unread badge tracking
    public event Action<ReactionsUpdatedDto>? ReactionsUpdated;
    public event Action<int, UserStatus>? UserStatusChanged;  // (userId, newStatus)
    public event Action? KickedFromVoice;
    public event Action<int>? UserSpeaking;  // userId
    public event Action<int, string, int>? CameraStarted;   // (userId, username, channelId)
    public event Action<int, int>? CameraStopped;            // (userId, channelId)
    public event Action<int, byte[]>? CameraFrameReceived;  // (userId, frame)
    public event Action<DirectMessageDto>? DirectMessageReceived;
    public event Action<int, int>? DirectMessageDeleted;    // (conversationId, messageId)
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
        _connection.On<int, UserDto>("UserJoinedChannel", (cid, dto) => UserJoinedChannel?.Invoke(cid, dto));
        _connection.On<int, UserDto>("UserLeftChannel", (cid, dto) => UserLeftChannel?.Invoke(cid, dto));
        _connection.On<byte[]>("ReceiveVoiceData", data => VoiceDataReceived?.Invoke(data));
        _connection.On<int, string>("ReceiveOffer", (uid, sdp) => OfferReceived?.Invoke(uid, sdp));
        _connection.On<int, string>("ReceiveAnswer", (uid, sdp) => AnswerReceived?.Invoke(uid, sdp));
        _connection.On<int, string>("ReceiveIceCandidate", (uid, c) => IceCandidateReceived?.Invoke(uid, c));
        _connection.On<int, string, int>("StreamStarted", (uid, name, cid) => StreamStarted?.Invoke(uid, name, cid));
        _connection.On<int, int>("StreamStopped", (uid, cid) => StreamStopped?.Invoke(uid, cid));
        _connection.On<byte[]>("ReceiveStreamFrame", data => StreamFrameReceived?.Invoke(data));
        _connection.On<int, string>("StreamNotification", (cid, text) => StreamNotification?.Invoke(cid, text));
        _connection.On<int, string, int>("UserTyping", (uid, name, cid) => UserTyping?.Invoke(uid, name, cid));
        _connection.On<int, int>("UserStoppedTyping", (uid, cid) => UserStoppedTyping?.Invoke(uid, cid));
        _connection.On<MessageDto>("MessageEdited", dto => MessageEdited?.Invoke(dto));
        _connection.On<int, int>("MessageDeleted", (mid, cid) => MessageDeleted?.Invoke(mid, cid));
        _connection.On<MessageDto>("NewMessageNotification", dto => NewMessageNotification?.Invoke(dto));
        _connection.On<ReactionsUpdatedDto>("ReactionsUpdated", dto => ReactionsUpdated?.Invoke(dto));
        _connection.On<int, UserStatus>("UserStatusChanged", (uid, s) => UserStatusChanged?.Invoke(uid, s));
        _connection.On("KickFromVoice", () => KickedFromVoice?.Invoke());
        _connection.On<int>("UserSpeaking", uid => UserSpeaking?.Invoke(uid));
        _connection.On<int, string, int>("CameraStarted", (uid, name, cid) => CameraStarted?.Invoke(uid, name, cid));
        _connection.On<int, int>("CameraStopped", (uid, cid) => CameraStopped?.Invoke(uid, cid));
        _connection.On<int, byte[]>("ReceiveCameraFrame", (uid, frame) => CameraFrameReceived?.Invoke(uid, frame));
        _connection.On<DirectMessageDto>("ReceiveDirectMessage", dto => DirectMessageReceived?.Invoke(dto));
        _connection.On<int, int>("DirectMessageDeleted", (cid, mid) => DirectMessageDeleted?.Invoke(cid, mid));

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
    public Task WatchStreamAsync(int channelId) => _connection!.InvokeAsync("WatchStream", channelId);
    public Task StopWatchingAsync(int channelId) => _connection!.InvokeAsync("StopWatching", channelId);

    public Task StartCameraAsync(int channelId) => _connection!.InvokeAsync("StartCamera", channelId);
    public Task StopCameraAsync(int channelId) => _connection!.InvokeAsync("StopCamera", channelId);
    public Task SendCameraFrameAsync(byte[] frame) =>
        _connection?.SendAsync("SendCameraFrame", frame) ?? Task.CompletedTask;

    public Task StartTypingAsync(int channelId) =>
        _connection?.SendAsync("StartTyping", channelId) ?? Task.CompletedTask;

    public Task StopTypingAsync(int channelId) =>
        _connection?.SendAsync("StopTyping", channelId) ?? Task.CompletedTask;

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
