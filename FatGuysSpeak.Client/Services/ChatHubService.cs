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
    public Task SendStreamFrameAsync(byte[] data) => _connection?.InvokeAsync("SendStreamFrame", data) ?? Task.CompletedTask;
    public Task WatchStreamAsync(int channelId) => _connection!.InvokeAsync("WatchStream", channelId);
    public Task StopWatchingAsync(int channelId) => _connection!.InvokeAsync("StopWatching", channelId);

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
