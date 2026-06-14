using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.Text.RegularExpressions;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FatGuysSpeak.Client.Models;
using FatGuysSpeak.Client.Pages;
using FatGuysSpeak.Client.Services;
using FatGuysSpeak.Shared;

namespace FatGuysSpeak.Client.ViewModels;

public partial class MainViewModel(ApiService api, ChatHubService hub, AudioService audio, SpeechService speech, PttService ptt, ScreenStreamService screen, SettingsViewModel settings) : ObservableObject
{
    private bool _initialized;
    private static readonly ConcurrentDictionary<string, LinkPreviewDto?> PreviewCache = new();
    private static readonly Regex UrlRegex = new(@"https?://\S+", RegexOptions.Compiled);

    // Backing stores per source — Messages is whichever tab is active
    private readonly List<MessageViewItem> _textMsgs = [];
    private readonly List<MessageViewItem> _voiceMsgs = [];

    [ObservableProperty] private ObservableCollection<ServerDto> _servers = [];
    [ObservableProperty] private ObservableCollection<ChannelViewItem> _channels = [];
    [ObservableProperty] private ObservableCollection<MessageViewItem> _messages = [];
    [ObservableProperty] private ObservableCollection<UserDto> _members = [];
    [ObservableProperty] private string _selectedTab = "Text";
    [ObservableProperty] private ObservableCollection<VoiceStateDto> _voiceParticipants = [];

    [ObservableProperty] private ServerDto? _selectedServer;
    [ObservableProperty] private ChannelDto? _selectedChannel;
    [ObservableProperty] private string _messageInput = "";
    [ObservableProperty] private bool _isMuted;
    [ObservableProperty] private bool _isDeafened;
    [ObservableProperty] private bool _inVoice;
    [ObservableProperty] private bool _isTestMic;
    [ObservableProperty] private bool _isLoopbackActive;
    [ObservableProperty] private double _micLevel;
    [ObservableProperty] private string _hypothesisText = string.Empty;
    [ObservableProperty] private bool _isPttActive;
    [ObservableProperty] private bool _isSettingPttKey;
    [ObservableProperty] private string _pttKeyName = "(not set)";
    [ObservableProperty] private bool _isStreaming;
    [ObservableProperty] private bool _isFullScreen;
    [ObservableProperty] private string? _activeStreamerName;
    [ObservableProperty] private int _activeStreamerId;
    [ObservableProperty] private ImageSource? _streamFrame;

    private Window? _streamWindow;
    private Window? _settingsWindow;
    private int _streamSending; // Interlocked — drop frames rather than queue when link is busy
    private Timer? _latencyTimer;
    private int _streamChannelId; // which channel the active stream is in

    [ObservableProperty] private int _streamLatencyMs;
    [ObservableProperty] private string _streamQualityLabel = "1080p 20fps";

    public bool IsTextTab => SelectedTab == "Text";
    public bool IsVoiceTab => SelectedTab == "Voice";
    public bool IsStreamTab => SelectedTab == "Stream";
    public bool HasActiveStream => ActiveStreamerName is not null || IsStreaming;
    public bool HasStreamFrame => StreamFrame is not null;
    public bool IsWaitingForStream => HasActiveStream && !HasStreamFrame;
    public bool StreamViewerVisible => IsStreamTab && !IsFullScreen;
    public string StreamButtonLabel => IsStreaming ? "⏹ Stop Sharing" : "📺 Share Screen";
    public double StreamTabOpacity => HasActiveStream ? 1.0 : 0.5;
    public string StreamHeaderText => IsStreaming
        ? "🟢  You are sharing your screen"
        : HasActiveStream
            ? $"📺  {ActiveStreamerName} is sharing their screen"
            : "📺  No active stream";
    public Color StreamButtonColor => IsStreaming ? Color.FromArgb("#ed4245") : Color.FromArgb("#3a3a3a");
    public Color StreamLatencyColor => StreamLatencyMs <= 0 ? Color.FromArgb("#888888")
        : StreamLatencyMs < 20 ? Color.FromArgb("#23a55a")
        : StreamLatencyMs < 50 ? Color.FromArgb("#f0a030")
        : StreamLatencyMs < 100 ? Color.FromArgb("#ed8030")
        : Color.FromArgb("#ed4245");
    public string StreamLatencyText => StreamLatencyMs <= 0 ? "—" : $"{StreamLatencyMs}ms";

    public string PttButtonLabel => IsSettingPttKey ? "Press any key…"
        : ptt.PttKey == 0 ? "⚠ Push to Talk (not set)"
        : $"🎙 Push to Talk: {PttKeyName}";

    public Color PttButtonColor => IsSettingPttKey
        ? Color.FromArgb("#f0a030")
        : IsPttActive
            ? Color.FromArgb("#23a55a")
            : ptt.PttKey == 0
                ? Color.FromArgb("#c0392b")  // red — mandatory, not configured
                : Color.FromArgb("#4e5058");

    public string VoiceHintText => ptt.PttKey == 0
        ? "⚠  Push to Talk key not set — open mic is not allowed. Click ⚙ Settings to set one."
        : $"Hold {PttKeyName} to speak — transcription posts automatically";

    public Color VoiceHintColor => ptt.PttKey == 0 ? Color.FromArgb("#e67e22") : Color.FromArgb("#666666");

    partial void OnSelectedTabChanged(string value)
    {
        OnPropertyChanged(nameof(IsTextTab));
        OnPropertyChanged(nameof(IsVoiceTab));
        OnPropertyChanged(nameof(IsStreamTab));
        OnPropertyChanged(nameof(StreamViewerVisible));
    }

    partial void OnIsSettingPttKeyChanged(bool value)
    {
        OnPropertyChanged(nameof(PttButtonLabel));
        OnPropertyChanged(nameof(PttButtonColor));
    }

    partial void OnIsPttActiveChanged(bool value) => OnPropertyChanged(nameof(PttButtonColor));
    partial void OnPttKeyNameChanged(string value)
    {
        OnPropertyChanged(nameof(PttButtonLabel));
        OnPropertyChanged(nameof(PttButtonColor));
        OnPropertyChanged(nameof(VoiceHintText));
        OnPropertyChanged(nameof(VoiceHintColor));
    }
    partial void OnIsStreamingChanged(bool value)
    {
        OnPropertyChanged(nameof(StreamButtonLabel));
        OnPropertyChanged(nameof(StreamButtonColor));
        OnPropertyChanged(nameof(HasActiveStream));
        OnPropertyChanged(nameof(IsWaitingForStream));
        OnPropertyChanged(nameof(StreamHeaderText));
        OnPropertyChanged(nameof(StreamTabOpacity));
    }
    partial void OnIsFullScreenChanged(bool value) => OnPropertyChanged(nameof(StreamViewerVisible));
    partial void OnActiveStreamerNameChanged(string? value)
    {
        OnPropertyChanged(nameof(HasActiveStream));
        OnPropertyChanged(nameof(IsWaitingForStream));
        OnPropertyChanged(nameof(StreamTabOpacity));
        OnPropertyChanged(nameof(StreamHeaderText));
        if (value is not null)
            StartLatencyTimer();
        else
        {
            if (SelectedTab == "Stream") SelectedTab = "Text";
            StopLatencyTimer();
        }
    }
    partial void OnStreamFrameChanged(ImageSource? value)
    {
        OnPropertyChanged(nameof(HasStreamFrame));
        OnPropertyChanged(nameof(IsWaitingForStream));
    }
    partial void OnStreamLatencyMsChanged(int value)
    {
        OnPropertyChanged(nameof(StreamLatencyColor));
        OnPropertyChanged(nameof(StreamLatencyText));
    }

    public void Initialize()
    {
        if (_initialized) return;
        _initialized = true;
        hub.MessageReceived   += OnMessageReceived;
        hub.UserJoinedVoice   += OnUserJoinedVoice;
        hub.UserLeftVoice     += OnUserLeftVoice;
        hub.UserConnected     += OnUserConnected;
        hub.UserDisconnected  += OnUserDisconnected;
        hub.UserJoinedServer  += OnUserJoinedServer;
        hub.ChannelCreated    += OnChannelCreated;
        hub.UserJoinedChannel += OnUserJoinedChannel;
        hub.UserLeftChannel   += OnUserLeftChannel;
        hub.VoiceDataReceived += OnVoiceDataReceived;
        hub.StreamStarted        += OnStreamStarted;
        hub.StreamStopped        += OnStreamStopped;
        hub.StreamFrameReceived  += OnStreamFrameReceived;
        hub.StreamNotification   += OnStreamNotification;
        screen.FrameCaptured  += OnScreenFrameCaptured;
        audio.MicLevelChanged += level => MainThread.BeginInvokeOnMainThread(() => MicLevel = level);
        speech.TextRecognized  += OnSpeechRecognized;
        speech.HypothesisChanged += text => MainThread.BeginInvokeOnMainThread(() => HypothesisText = text);
        ptt.PttDown    += OnPttDown;
        ptt.PttUp      += OnPttUp;
        ptt.KeyLearned += name =>
        {
            PttKeyName = name;
            IsSettingPttKey = false;
            _ = Shell.Current.DisplayAlert("Push to Talk Key Set", $"Push to Talk key bound to: {name}\n\nHold that key while in a voice channel to transmit.", "OK");
        };
        PttKeyName = ptt.PttKeyName;
    }

    [RelayCommand]
    public async Task LoadServersAsync()
    {
        var list = await api.GetServersAsync();
        Servers = new ObservableCollection<ServerDto>(list ?? []);
    }

    [RelayCommand]
    public async Task SelectServerAsync(ServerDto server)
    {
        SelectedServer = server;
        var channelList = await api.GetChannelsAsync(server.Id);
        Channels = new ObservableCollection<ChannelViewItem>((channelList ?? []).Select(c => new ChannelViewItem(c)));
        var onlineList = hub.IsConnected ? await hub.GetOnlineUsersAsync(server.Id) : [];
        Members = new ObservableCollection<UserDto>(onlineList);
        SelectedChannel = null;
        Messages.Clear();
    }

    [RelayCommand]
    public async Task SelectChannelAsync(ChannelViewItem item)
    {
        // Stop watching any previous stream
        if (ActiveStreamerName is not null && SelectedChannel is not null && !IsStreaming)
            await hub.StopWatchingAsync(SelectedChannel.Id);
        ActiveStreamerName = null;
        ActiveStreamerId = 0;
        StreamFrame = null;

        if (InVoice)
            await LeaveVoiceAsync();

        foreach (var c in Channels) c.IsSelected = false;
        item.IsSelected = true;
        SelectedChannel = item.Channel;
        await hub.JoinChannelAsync(item.Channel.Id);

        var occupants = hub.IsConnected ? await hub.GetChannelOccupantsAsync(item.Channel.Id) : [];
        MainThread.BeginInvokeOnMainThread(() =>
        {
            item.Occupants.Clear();
            foreach (var u in occupants)
                item.Occupants.Add(u);
        });

        // If there's an active stream in this channel, start receiving frames now
        if (!IsStreaming && _streamChannelId == item.Channel.Id)
            _ = hub.WatchStreamAsync(item.Channel.Id);

        var msgs = await api.GetMessagesAsync(item.Channel.Id);
        _textMsgs.Clear();
        _voiceMsgs.Clear();
        foreach (var m in (msgs ?? []).OrderBy(m => m.CreatedAt))
        {
            var mi = new MessageViewItem(m);
            if (m.Source == MessageSource.Voice) _voiceMsgs.Add(mi);
            else _textMsgs.Add(mi);
        }
        SelectedTab = "Text";
        Messages = new ObservableCollection<MessageViewItem>(_textMsgs);
        foreach (var mi in _textMsgs.Concat(_voiceMsgs))
            _ = LoadPreviewAsync(mi);

        await JoinVoiceAsync(item.Channel);
    }

    [RelayCommand]
    public void SelectTab(string tab)
    {
        SelectedTab = tab;
        if (tab != "Stream")
            Messages = new ObservableCollection<MessageViewItem>(
                tab == "Voice" ? _voiceMsgs : _textMsgs);
    }

    [RelayCommand]
    public async Task SendMessageAsync()
    {
        if (SelectedChannel is null || string.IsNullOrWhiteSpace(MessageInput)) return;
        var text = MessageInput.Trim();
        MessageInput = "";
        await PostMessageAsync(text, MessageSource.Text);
    }

    private async Task PostMessageAsync(string text, MessageSource source)
    {
        if (SelectedChannel is null || string.IsNullOrWhiteSpace(text)) return;
        var msg = await api.SendMessageAsync(SelectedChannel.Id, text, source);
        if (msg is not null)
        {
            var item = new MessageViewItem(msg);
            AddToStore(item);
            _ = LoadPreviewAsync(item);
        }
    }

    private void AddToStore(MessageViewItem item)
    {
        var isVoice = item.Message.Source == MessageSource.Voice;
        var store = isVoice ? _voiceMsgs : _textMsgs;

        if (store.Any(m => m.Message.Id == item.Message.Id)) return;
        store.Add(item);

        if ((isVoice && SelectedTab == "Voice") || (!isVoice && SelectedTab == "Text"))
            MainThread.BeginInvokeOnMainThread(() =>
            {
                if (!Messages.Any(m => m.Message.Id == item.Message.Id))
                    Messages.Add(item);
            });
    }

    [RelayCommand]
    public async Task CreateServerAsync(string name)
    {
        var server = await api.CreateServerAsync(new CreateServerRequest(name, null));
        if (server is not null) Servers.Add(server);
    }

    [RelayCommand]
    public async Task JoinServerAsync(int serverId)
    {
        var ok = await api.JoinServerAsync(serverId);
        if (ok)
        {
            var list = await api.GetServersAsync();
            Servers = new ObservableCollection<ServerDto>(list ?? []);
        }
    }

    [RelayCommand]
    public void ToggleMute()
    {
        IsMuted = !IsMuted;
        audio.SetMuted(IsMuted);
    }

    [RelayCommand]
    public void ToggleDeafen()
    {
        IsDeafened = !IsDeafened;
        audio.SetDeafened(IsDeafened);
    }

    [RelayCommand]
    public async Task LeaveVoiceAsync()
    {
        if (IsStreaming) { screen.StopCapture(); await hub.StopStreamAsync(); IsStreaming = false; }
        audio.AudioCaptured -= OnAudioCaptured;
        await hub.LeaveVoiceChannelAsync();
        if (IsLoopbackActive) { audio.StopLoopback(); IsLoopbackActive = false; }
        audio.StopCapture();
        audio.StopPlayback();
        InVoice = false;
        IsTestMic = false;
        IsPttActive = false;
        VoiceParticipants.Clear();
    }

    [RelayCommand]
    public void ToggleTestMic()
    {
        if (!InVoice) return;
        IsTestMic = !IsTestMic;
        audio.StopCapture();
        if (IsTestMic) audio.StartCapture(testMic: true);
        // when off, PTT controls capture
    }

    [RelayCommand]
    public void ToggleMicLoopback()
    {
        if (IsLoopbackActive)
        {
            audio.StopLoopback(alsoStopCapture: !InVoice);
            IsLoopbackActive = false;
        }
        else
        {
            audio.StartLoopback();
            IsLoopbackActive = true;
        }
    }

    [RelayCommand]
    public async Task SetPttKey()
    {
        if (!ptt.IsHookInstalled)
        {
            await Shell.Current.DisplayAlert("Error", "Keyboard hook could not be installed. Try restarting the app.", "OK");
            return;
        }
        await Shell.Current.DisplayAlert("Set Push to Talk Key",
            "Click OK, then press any key you want to use for Push to Talk.", "OK");
        IsSettingPttKey = true;
        ptt.BeginLearning();
    }

    private void OnPttDown()
    {
        if (SelectedChannel is null) return;
        IsPttActive = true;
        audio.StartCapture();
        _ = speech.StartListeningAsync();
    }

    private void OnPttUp()
    {
        IsPttActive = false;
        audio.StopCapture();
        _ = speech.StopAndFlushAsync();
    }

    [RelayCommand]
    public async Task LogoutAsync()
    {
        if (IsStreaming) { screen.StopCapture(); await hub.StopStreamAsync(); IsStreaming = false; }
        ptt.CancelLearning();
        ptt.ClearUser();
        speech.StopListening();
        if (InVoice) await LeaveVoiceAsync();
        await hub.DisconnectAsync();
        api.ClearToken();
        _initialized = false;
        await Shell.Current.GoToAsync("//login");
    }

    private async Task JoinVoiceAsync(ChannelDto channel)
    {
        await hub.JoinVoiceChannelAsync(channel.Id);
        audio.AudioCaptured += OnAudioCaptured;
        audio.StartPlayback(); // capture is PTT-controlled
        InVoice = true;
    }

    private void OnAudioCaptured(byte[] data) =>
        _ = hub.SendVoiceDataAsync(data);

    private void OnVoiceDataReceived(byte[] data) =>
        audio.PlayAudio(data);

    private void OnSpeechRecognized(string text)
    {
        var trimmed = text.Trim();
        if (string.IsNullOrEmpty(trimmed) || SelectedChannel is null) return;
        _ = PostMessageAsync(trimmed, MessageSource.Voice);
    }

    private void OnMessageReceived(MessageDto msg)
    {
        if (SelectedChannel?.Id != msg.ChannelId) return;
        var item = new MessageViewItem(msg);
        AddToStore(item);
        _ = LoadPreviewAsync(item);
    }

    private async Task LoadPreviewAsync(MessageViewItem item)
    {
        var match = UrlRegex.Match(item.Message.Content);
        if (!match.Success) return;

        var url = match.Value.TrimEnd('.', ',', ')', ']', '!', '?');

        if (PreviewCache.TryGetValue(url, out var cached))
        {
            item.Preview = cached;
            return;
        }

        var preview = await api.GetLinkPreviewAsync(url);
        PreviewCache[url] = preview;
        item.Preview = preview;
    }

    private void OnUserJoinedVoice(VoiceStateDto state)
    {
        MainThread.BeginInvokeOnMainThread(() =>
        {
            var existing = VoiceParticipants.FirstOrDefault(v => v.UserId == state.UserId);
            if (existing is not null) VoiceParticipants.Remove(existing);
            VoiceParticipants.Add(state);

            if (state.UserId != api.CurrentUserId && SelectedChannel is not null)
                AddToStore(MessageViewItem.CreateSystem($"🎙 {state.Username} joined voice", SelectedChannel.Id, MessageSource.Voice));
        });
    }

    private void OnUserLeftVoice(VoiceStateDto state)
    {
        MainThread.BeginInvokeOnMainThread(() =>
        {
            var existing = VoiceParticipants.FirstOrDefault(v => v.UserId == state.UserId);
            if (existing is not null) VoiceParticipants.Remove(existing);

            if (state.UserId != api.CurrentUserId && SelectedChannel is not null)
                AddToStore(MessageViewItem.CreateSystem($"🔇 {state.Username} left voice", SelectedChannel.Id, MessageSource.Voice));
        });
    }

    private void OnUserConnected(UserDto user)
    {
        MainThread.BeginInvokeOnMainThread(() =>
        {
            if (!Members.Any(m => m.Id == user.Id))
                Members.Add(user);
        });
    }

    private void OnUserDisconnected(UserDto user)
    {
        MainThread.BeginInvokeOnMainThread(() =>
        {
            var existing = Members.FirstOrDefault(m => m.Id == user.Id);
            if (existing is not null) Members.Remove(existing);
        });
    }

    private void OnUserJoinedServer(UserDto user)
    {
        MainThread.BeginInvokeOnMainThread(() =>
        {
            if (!Members.Any(m => m.Id == user.Id))
                Members.Add(user);
        });
    }

    private void OnChannelCreated(ChannelDto channel)
    {
        if (SelectedServer?.Id == channel.ServerId)
            MainThread.BeginInvokeOnMainThread(() =>
            {
                if (!Channels.Any(c => c.Channel.Id == channel.Id))
                    Channels.Add(new ChannelViewItem(channel));
            });
    }

    [RelayCommand]
    public async Task ToggleStreamAsync()
    {
        if (IsStreaming)
        {
            screen.StopCapture();
            await hub.StopStreamAsync();
            IsStreaming = false;
            ActiveStreamerName = null;
            ActiveStreamerId = 0;
            return;
        }

        if (SelectedServer is null)
        {
            await Shell.Current.DisplayAlert("Stream Error", "No server selected.", "OK");
            return;
        }

        if (!hub.IsConnected)
        {
            await Shell.Current.DisplayAlert("Stream Error", "Not connected to server. Please reconnect.", "OK");
            return;
        }

        var selected = await ShowWindowPickerAsync();
        if (selected is null) return; // user cancelled

        try
        {
            // Use first available text channel if none is selected
            var channelId = SelectedChannel?.Id
                ?? Channels.FirstOrDefault(c => c.Channel.Type == ChannelType.Text)?.Channel.Id;
            if (channelId is null)
            {
                await Shell.Current.DisplayAlert("Stream Error", "No text channel available on this server.", "OK");
                return;
            }

            await hub.StartStreamAsync(channelId.Value);
            StreamQualityLabel = "1080p 20fps";
            screen.StartCapture(selected.Handle, fps: 20);
            IsStreaming = true;
            ActiveStreamerName = api.CurrentUsername;
            ActiveStreamerId = api.CurrentUserId;
        }
        catch (Exception ex)
        {
            await Shell.Current.DisplayAlert("Stream Error", $"Failed to start stream: {ex.Message}", "OK");
        }
    }

    private Task<AppWindow?> ShowWindowPickerAsync()
    {
        var tcs = new TaskCompletionSource<AppWindow?>();
        var pickerVm = new WindowPickerViewModel();
        var pickerPage = new WindowPickerPage { BindingContext = pickerVm };
        var pickerWindow = new Window(pickerPage)
        {
            Title = "FatGuysSpeak — Choose what to share",
            Width = 640,
            Height = 520,
            MinimumWidth = 420,
            MinimumHeight = 360,
        };
        pickerVm.Completed += chosen =>
        {
            // Set result BEFORE closing — CloseWindow fires Destroying synchronously on WinUI,
            // which would otherwise set the TCS to null first and discard the chosen window.
            tcs.TrySetResult(chosen);
            Application.Current!.CloseWindow(pickerWindow);
        };
        pickerWindow.Destroying += (_, _) => tcs.TrySetResult(null); // handles direct X-close
        Application.Current!.OpenWindow(pickerWindow);
        return tcs.Task;
    }

    private void StartLatencyTimer()
    {
        _latencyTimer?.Dispose();
        _latencyTimer = new Timer(async _ => await CheckLatencyAsync(), null, 500, 3000);
    }

    private void StopLatencyTimer()
    {
        _latencyTimer?.Dispose();
        _latencyTimer = null;
        MainThread.BeginInvokeOnMainThread(() => StreamLatencyMs = 0);
    }

    private async Task CheckLatencyAsync()
    {
        var ms = await hub.MeasureLatencyAsync();
        if (ms < 0) return;
        MainThread.BeginInvokeOnMainThread(() => StreamLatencyMs = ms);
        if (IsStreaming && Preferences.Get("adaptive_quality", true))
            AdjustStreamQuality(ms);
    }

    [RelayCommand]
    public void OpenSettings()
    {
        if (_settingsWindow is not null)
        {
            // Bring existing window to front — just close and reopen isn't ideal,
            // so we activate it by doing nothing; user sees it already open
            return;
        }
        var page = new Pages.SettingsPage { BindingContext = settings };
        _settingsWindow = new Window(page)
        {
            Title = "FatGuysSpeak — Settings",
            Width = 560,
            Height = 640,
            MinimumWidth = 460,
            MinimumHeight = 500,
        };
        _settingsWindow.Destroying += (_, _) => _settingsWindow = null;
        Application.Current!.OpenWindow(_settingsWindow);
    }

    private void AdjustStreamQuality(int latencyMs)
    {
        var (fps, quality, maxWidth, label) = latencyMs switch
        {
            < 20  => (20, 75, 1920, "1080p 20fps"),
            < 50  => (15, 65, 1280, "720p 15fps"),
            < 100 => (10, 55, 1280, "720p 10fps"),
            _     => (5,  40,  960, "540p 5fps"),
        };
        screen.UpdateQuality(fps, quality, maxWidth);
        MainThread.BeginInvokeOnMainThread(() => StreamQualityLabel = label);
    }

    private void OnStreamStarted(int userId, string username, int channelId)
    {
        MainThread.BeginInvokeOnMainThread(() =>
        {
            ActiveStreamerId = userId;
            ActiveStreamerName = username;
            _streamChannelId = channelId;
            if (!IsStreaming)
                _ = hub.WatchStreamAsync(channelId);
        });
    }

    private void OnStreamNotification(int channelId, string text)
    {
        MainThread.BeginInvokeOnMainThread(() =>
        {
            var item = MessageViewItem.CreateSystem(text, channelId);
            // Add to text store so it appears regardless of which text channel is active
            _textMsgs.Add(item);
            if (SelectedTab == "Text")
                Messages.Add(item);
        });
    }

    [RelayCommand]
    public void ToggleFullScreen()
    {
        if (!HasActiveStream) return;
        if (IsFullScreen) CloseStreamWindow();
        else OpenStreamWindow();
    }

    private void OpenStreamWindow()
    {
        var page = new StreamViewPage { BindingContext = this };
        _streamWindow = new Window(page)
        {
            Title = "FatGuysSpeak — Screen Share",
            Width = 1600,
            Height = 900,
            MinimumWidth = 800,
            MinimumHeight = 450,
        };
        _streamWindow.Destroying += (_, _) =>
        {
            _streamWindow = null;
            MainThread.BeginInvokeOnMainThread(() => IsFullScreen = false);
        };
        Application.Current!.OpenWindow(_streamWindow);
        IsFullScreen = true;
    }

    private void CloseStreamWindow()
    {
        if (_streamWindow is not null)
            Application.Current!.CloseWindow(_streamWindow);
        // IsFullScreen = false is handled by the Destroying event
    }

    private void OnStreamStopped(int userId, int channelId)
    {
        MainThread.BeginInvokeOnMainThread(() =>
        {
            if (ActiveStreamerId != userId) return;
            if (IsFullScreen) CloseStreamWindow();
            if (!IsStreaming)
                _ = hub.StopWatchingAsync(channelId);
            ActiveStreamerName = null;
            ActiveStreamerId = 0;
            _streamChannelId = 0;
            StreamFrame = null;
            IsStreaming = false;
        });
    }

    private void OnStreamFrameReceived(byte[] data)
    {
        var ms = new MemoryStream(data);
        var source = ImageSource.FromStream(() => ms);
        MainThread.BeginInvokeOnMainThread(() => StreamFrame = source);
    }

    private void OnScreenFrameCaptured(byte[] data)
    {
        if (Interlocked.CompareExchange(ref _streamSending, 1, 0) != 0) return;
        _ = hub.SendStreamFrameAsync(data)
              .ContinueWith(_ => Interlocked.Exchange(ref _streamSending, 0));
    }

    private void OnUserJoinedChannel(int channelId, UserDto user)
    {
        var item = Channels.FirstOrDefault(c => c.Channel.Id == channelId);
        if (item is null) return;
        MainThread.BeginInvokeOnMainThread(() =>
        {
            if (!item.Occupants.Any(u => u.Id == user.Id))
                item.Occupants.Add(user);
            if (SelectedChannel?.Id == channelId)
                AddToStore(MessageViewItem.CreateSystem($"→ {user.Username} joined the channel", channelId, MessageSource.Text));
        });
    }

    private void OnUserLeftChannel(int channelId, UserDto user)
    {
        var item = Channels.FirstOrDefault(c => c.Channel.Id == channelId);
        if (item is null) return;
        MainThread.BeginInvokeOnMainThread(() =>
        {
            var existing = item.Occupants.FirstOrDefault(u => u.Id == user.Id);
            if (existing is not null) item.Occupants.Remove(existing);
            if (SelectedChannel?.Id == channelId)
                AddToStore(MessageViewItem.CreateSystem($"← {user.Username} left the channel", channelId, MessageSource.Text));
        });
    }
}
