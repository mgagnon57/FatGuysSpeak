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

public partial class MainViewModel(ApiService api, ChatHubService hub, AudioService audio, SpeechService speech, PttService ptt, ScreenStreamService screen, CameraService camera, SettingsViewModel settings, ToastNotificationService toast, GifService gif) : ObservableObject
{
    private bool _initialized;
    private static readonly ConcurrentDictionary<string, LinkPreviewDto?> PreviewCache = new();
    private static readonly Regex UrlRegex = new(@"https?://\S+", RegexOptions.Compiled);

    // Backing stores per source — Messages is whichever tab is active
    private readonly List<MessageViewItem> _textMsgs = [];
    private readonly List<MessageViewItem> _voiceMsgs = [];
    private readonly List<MessageViewItem> _streamMsgs = [];

    [ObservableProperty] private ObservableCollection<ServerDto> _servers = [];
    [ObservableProperty] private ObservableCollection<ChannelViewItem> _channels = [];
    [ObservableProperty] private ObservableCollection<MessageViewItem> _messages = [];
    [ObservableProperty] private ObservableCollection<UserDto> _members = [];
    [ObservableProperty] private string _selectedTab = "Text";
    [ObservableProperty] private ObservableCollection<VoiceParticipantViewModel> _voiceParticipants = [];

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
    [ObservableProperty] private string _hubConnectionState = "Connected";
    [ObservableProperty] private bool _hasUnreadBelow;
    [ObservableProperty] private int _unreadBelowCount;
    [ObservableProperty] private bool _isStreaming;
    [ObservableProperty] private bool _isCameraOn;

    public ObservableCollection<VideoTileViewModel> VideoTiles { get; } = [];
    public bool HasVideoTiles => VideoTiles.Count > 0;

    private bool _isAtBottom = true;
    private MessageViewItem? _dividerItem;

    public string UnreadPillText => _unreadBelowCount == 1 ? "↓  1 new message" : $"↓  {_unreadBelowCount} new messages";
    public event Action? ScrollToLatestRequested;
    [ObservableProperty] private bool _isFullScreen;
    [ObservableProperty] private string? _activeStreamerName;
    [ObservableProperty] private int _activeStreamerId;
    [ObservableProperty] private ImageSource? _streamFrame;
    [ObservableProperty] private string? _pendingAttachmentUrl;
    [ObservableProperty] private bool _isEmojiPickerOpen;
    [ObservableProperty] private bool _isGifPickerOpen;
    [ObservableProperty] private string _gifSearchQuery = "";
    [ObservableProperty] private bool _isSearchingGifs;
    [ObservableProperty] private string _gifErrorText = "";
    public ObservableCollection<GifResult> GifResults { get; } = [];
    [ObservableProperty] private bool _isSearchOpen;
    [ObservableProperty] private string _searchQuery = "";
    [ObservableProperty] private bool _isSearching;
    public ObservableCollection<MessageViewItem> SearchResults { get; } = [];
    private bool _searchExecuted;

    [ObservableProperty] private bool _isStreamChatOverlayVisible;
    public ObservableCollection<MessageViewItem> StreamOverlayMessages { get; } = [];

    // Current user info (for profile bar)
    [ObservableProperty] private string _currentUsername = api.CurrentUsername;
    [ObservableProperty] private string? _currentUserAvatarUrl = api.CurrentAvatarUrl;
    public bool HasCurrentUserAvatar => _currentUserAvatarUrl is not null;
    partial void OnCurrentUserAvatarUrlChanged(string? value) => OnPropertyChanged(nameof(HasCurrentUserAvatar));

    // Reply state
    [ObservableProperty] private MessageViewItem? _replyingToItem;
    [ObservableProperty] private string _replyingToUsername = "";
    [ObservableProperty] private string _replyingToPreview = "";
    public bool IsReplying => _replyingToItem is not null;
    partial void OnReplyingToItemChanged(MessageViewItem? value) => OnPropertyChanged(nameof(IsReplying));

    private Window? _streamWindow;
    private Window? _settingsWindow;
    private int _streamSending; // Interlocked — drop frames rather than queue when link is busy
    private int _cameraSending; // Interlocked — same drop pattern for camera frames
    private Timer? _latencyTimer;
    private int _streamChannelId; // which channel the active stream is in
    private volatile byte[]? _pendingFrame; // latest received frame waiting to be shown

    // Typing indicator state
    private bool _isTyping;
    private CancellationTokenSource? _typingCts;
    private readonly Dictionary<int, string> _typingUsersInChannel = new(); // userId -> username

    [ObservableProperty] private int _streamLatencyMs;
    [ObservableProperty] private string _streamQualityLabel = "1080p 20fps";

    public ServerRole CurrentServerRole => SelectedServer?.MyRole ?? ServerRole.Member;
    public bool IsServerAdmin => CurrentServerRole >= ServerRole.Admin;
    public bool IsAdminOrModerator => CurrentServerRole >= ServerRole.Moderator;

    public bool IsTextTab => SelectedTab == "Text";
    public bool IsVoiceTab => SelectedTab == "Voice";
    public bool IsStreamTab => SelectedTab == "Stream";
    public bool IsTextOrStreamTab => IsTextTab || IsStreamTab;

    public string  ConnectionStatusText  => HubConnectionState switch { "Reconnecting" => "Reconnecting…", "Disconnected" => "Disconnected", _ => "Connected" };
    public Color   ConnectionDotColor    => HubConnectionState switch { "Reconnecting" => Color.FromArgb("#f0a030"), "Disconnected" => Color.FromArgb("#ed4245"), _ => Color.FromArgb("#44bb44") };
    public Color   ConnectionStatusColor => ConnectionDotColor;
    public bool    IsConnectionBannerVisible => HubConnectionState != "Connected";
    public bool ShowMessages => !IsSearchOpen;
    public string SearchEmptyText => _searchExecuted ? "No messages found." : "Type a search term and press Search.";
    public bool HasActiveStream => ActiveStreamerName is not null || IsStreaming;
    public bool HasStreamFrame => StreamFrame is not null;
    public bool IsWaitingForStream => HasActiveStream && !HasStreamFrame;
    public bool StreamViewerVisible => IsStreamTab && !IsFullScreen;
    public bool HasPendingAttachment => PendingAttachmentUrl is not null;
    public string CameraButtonLabel => IsCameraOn ? "📷 Stop Camera" : "📷 Camera";
    public Color  CameraButtonColor => IsCameraOn ? Color.FromArgb("#ed4245") : Color.FromArgb("#3a3a3a");
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

    public string TypingText
    {
        get
        {
            var names = _typingUsersInChannel.Values.ToList();
            return names.Count switch
            {
                0 => "",
                1 => $"{names[0]} is typing…",
                2 => $"{names[0]} and {names[1]} are typing…",
                _ => "Several people are typing…"
            };
        }
    }

    public string PttButtonLabel => IsSettingPttKey ? "Press any key…"
        : ptt.PttKey == 0 ? "⚠ Push to Talk (not set)"
        : $"🎙 Push to Talk: {PttKeyName}";

    public Color PttButtonColor => IsSettingPttKey
        ? Color.FromArgb("#f0a030")
        : IsPttActive
            ? Color.FromArgb("#23a55a")
            : ptt.PttKey == 0
                ? Color.FromArgb("#c0392b")
                : Color.FromArgb("#4e5058");

    public string VoiceHintText => ptt.PttKey == 0
        ? "⚠  Push to Talk key not set — open mic is not allowed. Click ⚙ Settings to set one."
        : $"Hold {PttKeyName} to speak — transcription posts automatically";

    public Color VoiceHintColor => ptt.PttKey == 0 ? Color.FromArgb("#e67e22") : Color.FromArgb("#666666");

    partial void OnUnreadBelowCountChanged(int value) => OnPropertyChanged(nameof(UnreadPillText));

    public void OnScrollPositionChanged(int lastVisibleIndex)
    {
        _isAtBottom = Messages.Count == 0 || lastVisibleIndex >= Messages.Count - 1;
        if (_isAtBottom)
            ClearUnreadIndicator();
    }

    [RelayCommand]
    public void ScrollToLatest()
    {
        ClearUnreadIndicator();
        ScrollToLatestRequested?.Invoke();
    }

    private void ClearUnreadIndicator()
    {
        if (_dividerItem is not null)
        {
            MainThread.BeginInvokeOnMainThread(() =>
            {
                Messages.Remove(_dividerItem!);
                _dividerItem = null;
            });
        }
        HasUnreadBelow = false;
        UnreadBelowCount = 0;
        _isAtBottom = true;
    }

    private void ResetUnreadState()
    {
        _dividerItem = null;
        HasUnreadBelow = false;
        UnreadBelowCount = 0;
        _isAtBottom = true;
    }

    partial void OnSelectedServerChanged(ServerDto? value)
    {
        OnPropertyChanged(nameof(CurrentServerRole));
        OnPropertyChanged(nameof(IsServerAdmin));
        OnPropertyChanged(nameof(IsAdminOrModerator));
    }

    partial void OnHubConnectionStateChanged(string value)
    {
        OnPropertyChanged(nameof(ConnectionStatusText));
        OnPropertyChanged(nameof(ConnectionDotColor));
        OnPropertyChanged(nameof(ConnectionStatusColor));
        OnPropertyChanged(nameof(IsConnectionBannerVisible));
    }

    partial void OnSelectedTabChanged(string value)
    {
        ResetUnreadState();
        OnPropertyChanged(nameof(IsTextTab));
        OnPropertyChanged(nameof(IsVoiceTab));
        OnPropertyChanged(nameof(IsStreamTab));
        OnPropertyChanged(nameof(IsTextOrStreamTab));
        OnPropertyChanged(nameof(StreamViewerVisible));
        OnPropertyChanged(nameof(ShowMessages));
        if (value == "Stream")
        {
            Messages = new ObservableCollection<MessageViewItem>(_streamMsgs);
            if (IsStreamChatOverlayVisible) SeedOverlayMessages();
        }
        else if (value == "Voice")
            Messages = new ObservableCollection<MessageViewItem>(_voiceMsgs);
        else
            Messages = new ObservableCollection<MessageViewItem>(_textMsgs);
    }

    partial void OnIsSearchOpenChanged(bool value)
    {
        OnPropertyChanged(nameof(ShowMessages));
        if (!value)
        {
            SearchQuery = "";
            SearchResults.Clear();
            _searchExecuted = false;
        }
    }

    partial void OnSearchQueryChanged(string value)
    {
        if (_searchExecuted)
        {
            _searchExecuted = false;
            OnPropertyChanged(nameof(SearchEmptyText));
        }
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
    partial void OnIsCameraOnChanged(bool value)
    {
        OnPropertyChanged(nameof(CameraButtonLabel));
        OnPropertyChanged(nameof(CameraButtonColor));
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
    partial void OnPendingAttachmentUrlChanged(string? value) =>
        OnPropertyChanged(nameof(HasPendingAttachment));

    partial void OnMessageInputChanged(string value)
    {
        if (SelectedChannel is null || !hub.IsConnected) return;

        if (string.IsNullOrEmpty(value))
        {
            StopTypingDebounced();
            return;
        }

        if (!_isTyping)
        {
            _isTyping = true;
            _ = hub.StartTypingAsync(SelectedChannel.Id);
        }

        _typingCts?.Cancel();
        _typingCts = new CancellationTokenSource();
        var token = _typingCts.Token;
        _ = Task.Delay(2000, token).ContinueWith(t =>
        {
            if (!t.IsCanceled) StopTypingDebounced();
        }, TaskScheduler.Default);
    }

    private void StopTypingDebounced()
    {
        if (!_isTyping) return;
        _isTyping = false;
        _typingCts?.Cancel();
        _typingCts = null;
        if (hub.IsConnected && SelectedChannel is not null)
            _ = hub.StopTypingAsync(SelectedChannel.Id);
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
        hub.UserTyping              += OnUserTyping;
        hub.UserStoppedTyping       += OnUserStoppedTyping;
        hub.MessageEdited           += OnMessageEdited;
        hub.MessageDeleted          += OnMessageDeleted;
        hub.NewMessageNotification  += OnNewMessageNotification;
        hub.ReactionsUpdated        += OnReactionsUpdated;
        hub.UserStatusChanged       += OnUserStatusChanged;
        hub.KickedFromVoice         += OnKickedFromVoice;
        hub.UserSpeaking            += OnUserSpeaking;
        hub.CameraStarted           += OnCameraStarted;
        hub.CameraStopped           += OnCameraStopped;
        hub.CameraFrameReceived     += OnCameraFrameReceived;
        hub.Reconnecting += _ => MainThread.BeginInvokeOnMainThread(() => HubConnectionState = "Reconnecting");
        hub.Reconnected  += _ => MainThread.BeginInvokeOnMainThread(async () =>
        {
            HubConnectionState = "Connected";
            await OnReconnectedAsync();
        });
        hub.Disconnected += _ => MainThread.BeginInvokeOnMainThread(() => HubConnectionState = "Disconnected");
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
        IsStreamChatOverlayVisible = Preferences.Get("stream_chat_overlay", false);
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

        // Clear search, overlay and unread state from previous channel
        IsSearchOpen = false;
        StreamOverlayMessages.Clear();
        ResetUnreadState();

        // Clear typing state from previous channel
        StopTypingDebounced();
        _typingUsersInChannel.Clear();
        OnPropertyChanged(nameof(TypingText));

        if (InVoice)
            await LeaveVoiceAsync();

        foreach (var c in Channels) c.IsSelected = false;
        item.IsSelected = true;
        item.UnreadCount = 0;
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
        _streamMsgs.Clear();
        foreach (var m in (msgs ?? []).OrderBy(m => m.CreatedAt))
        {
            var mi = Wire(new MessageViewItem(m, api.CurrentUsername, api.CurrentUserId));
            if (m.Source == MessageSource.Voice) _voiceMsgs.Add(mi);
            else if (m.Source == MessageSource.Stream) _streamMsgs.Add(mi);
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
    public async Task ViewProfileAsync(int userId)
    {
        if (userId == 0 || SelectedServer is null) return;
        var vm = new UserProfileViewModel(api, SelectedServer.Id);
        var page = new Pages.UserProfilePage { BindingContext = vm };
        var window = new Window(page)
        {
            Title = "User Profile",
            Width = 320,
            Height = 440,
            MinimumWidth = 280,
            MinimumHeight = 340,
        };
        Application.Current!.OpenWindow(window);
        await vm.LoadAsync(userId);
    }

    [RelayCommand]
    public void ToggleStreamChatOverlay()
    {
        IsStreamChatOverlayVisible = !IsStreamChatOverlayVisible;
        Preferences.Set("stream_chat_overlay", IsStreamChatOverlayVisible);

        if (IsStreamChatOverlayVisible)
            SeedOverlayMessages();
    }

    private void SeedOverlayMessages()
    {
        StreamOverlayMessages.Clear();
        foreach (var m in _streamMsgs.Where(m => !m.Message.IsDeleted).TakeLast(8))
            StreamOverlayMessages.Add(m);
    }

    private void PushOverlayMessage(MessageViewItem item)
    {
        if (item.Message.IsDeleted) return;
        MainThread.BeginInvokeOnMainThread(() =>
        {
            StreamOverlayMessages.Add(item);
            while (StreamOverlayMessages.Count > 8)
                StreamOverlayMessages.RemoveAt(0);
        });
    }

    [RelayCommand]
    public void ToggleSearch() => IsSearchOpen = !IsSearchOpen;

    [RelayCommand]
    public async Task SearchAsync()
    {
        if (SelectedChannel is null || SearchQuery.Length < 2) return;
        IsSearching = true;
        SearchResults.Clear();
        _searchExecuted = false;
        var results = await api.SearchMessagesAsync(SelectedChannel.Id, SearchQuery);
        foreach (var m in results ?? [])
            SearchResults.Add(Wire(new MessageViewItem(m, api.CurrentUsername, api.CurrentUserId)));
        _searchExecuted = true;
        OnPropertyChanged(nameof(SearchEmptyText));
        IsSearching = false;
    }

    [RelayCommand]
    public void ClearSearch()
    {
        SearchQuery = "";
        SearchResults.Clear();
        _searchExecuted = false;
        OnPropertyChanged(nameof(SearchEmptyText));
    }

    [RelayCommand]
    public void ToggleEmojiPicker()
    {
        IsEmojiPickerOpen = !IsEmojiPickerOpen;
        if (IsEmojiPickerOpen) IsGifPickerOpen = false;
    }

    [RelayCommand]
    public void InsertEmoji(string emoji) => MessageInput += emoji;

    [RelayCommand]
    public async Task ToggleGifPicker()
    {
        IsGifPickerOpen = !IsGifPickerOpen;
        if (IsGifPickerOpen)
        {
            IsEmojiPickerOpen = false;
            if (GifResults.Count == 0)
                await LoadGifsAsync();
        }
    }

    [RelayCommand]
    public async Task SearchGifs()
    {
        await LoadGifsAsync();
    }

    private async Task LoadGifsAsync()
    {
        var apiKey = Preferences.Get("giphy_api_key", "");
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            GifErrorText = "No Giphy API key set. Enter your key in Settings → Giphy API Key.";
            GifResults.Clear();
            return;
        }

        IsSearchingGifs = true;
        GifErrorText = "";
        try
        {
            var results = string.IsNullOrWhiteSpace(GifSearchQuery)
                ? await gif.GetTrendingAsync(apiKey)
                : await gif.SearchAsync(GifSearchQuery, apiKey);
            GifResults.Clear();
            foreach (var r in results) GifResults.Add(r);
            if (GifResults.Count == 0)
                GifErrorText = "No GIFs found.";
        }
        catch (Exception ex)
        {
            GifErrorText = $"Error: {ex.Message}";
            GifResults.Clear();
        }
        finally { IsSearchingGifs = false; }
    }

    [RelayCommand]
    public async Task SendGif(GifResult gifResult)
    {
        if (SelectedChannel is null) return;
        IsGifPickerOpen = false;
        var source = IsStreamTab ? MessageSource.Stream : MessageSource.Text;
        await PostMessageAsync("", source, gifResult.Url);
    }

    [RelayCommand]
    public async Task SendMessageAsync()
    {
        if (SelectedChannel is null) return;
        if (string.IsNullOrWhiteSpace(MessageInput) && PendingAttachmentUrl is null) return;

        StopTypingDebounced();
        IsEmojiPickerOpen = false;

        var text = MessageInput.Trim();
        var attachmentUrl = PendingAttachmentUrl;
        MessageInput = "";
        PendingAttachmentUrl = null;

        var source = IsStreamTab ? MessageSource.Stream : MessageSource.Text;
        await PostMessageAsync(text, source, attachmentUrl);
    }

    private async Task PostMessageAsync(string text, MessageSource source, string? attachmentUrl = null)
    {
        if (SelectedChannel is null) return;
        if (string.IsNullOrWhiteSpace(text) && attachmentUrl is null) return;
        var replyToId = _replyingToItem?.Message.Id;
        CancelReply();
        var msg = await api.SendMessageAsync(SelectedChannel.Id, text, source, attachmentUrl, replyToId);
        if (msg is not null)
        {
            var item = Wire(new MessageViewItem(msg, api.CurrentUsername, api.CurrentUserId));
            AddToStore(item);
            _ = LoadPreviewAsync(item);
        }
    }

    [RelayCommand]
    public async Task PickAttachmentAsync()
    {
        try
        {
            var result = await FilePicker.PickAsync(new PickOptions
            {
                PickerTitle = "Choose an image to share",
                FileTypes = FilePickerFileType.Images,
            });
            if (result is null) return;

            using var stream = await result.OpenReadAsync();
            var contentType = result.ContentType ?? "image/jpeg";
            var url = await api.UploadAttachmentAsync(stream, result.FileName, contentType);
            if (url is not null)
                PendingAttachmentUrl = url;
            else
                await Shell.Current.DisplayAlert("Upload Failed", "Could not upload the file.", "OK");
        }
        catch (Exception ex)
        {
            await Shell.Current.DisplayAlert("Upload Error", ex.Message, "OK");
        }
    }

    [RelayCommand]
    public void ClearAttachment() => PendingAttachmentUrl = null;

    [RelayCommand]
    public void Reply(MessageViewItem item)
    {
        if (item.IsSystemMessage || item.Message.IsDeleted) return;
        ReplyingToItem = item;
        ReplyingToUsername = item.Message.AuthorUsername;
        ReplyingToPreview = item.Message.Content.Length > 100
            ? item.Message.Content[..100] + "…"
            : item.Message.Content;
    }

    [RelayCommand]
    public void CancelReply()
    {
        ReplyingToItem = null;
        ReplyingToUsername = "";
        ReplyingToPreview = "";
    }

    [RelayCommand]
    public async Task UploadAvatarAsync()
    {
        try
        {
            var result = await FilePicker.PickAsync(new PickOptions
            {
                PickerTitle = "Choose your profile picture",
                FileTypes = FilePickerFileType.Images,
            });
            if (result is null) return;

            using var stream = await result.OpenReadAsync();
            var contentType = result.ContentType ?? "image/jpeg";
            var url = await api.UploadAvatarAsync(stream, result.FileName, contentType);
            if (url is not null)
            {
                CurrentUserAvatarUrl = url;
                api.UpdateAvatarUrl(url);
            }
            else
                await Shell.Current.DisplayAlert("Upload Failed", "Could not upload the file.", "OK");
        }
        catch (Exception ex)
        {
            await Shell.Current.DisplayAlert("Upload Error", ex.Message, "OK");
        }
    }

    [RelayCommand]
    public async Task EditMessageAsync(MessageViewItem item)
    {
        if (item.IsSystemMessage || item.Message.AuthorId != api.CurrentUserId) return;
        if (item.Message.IsDeleted) return;
        var result = await Shell.Current.DisplayPromptAsync(
            "Edit Message", "Edit your message:",
            initialValue: item.Message.Content, maxLength: 2000);
        if (result is null || result.Trim() == item.Message.Content) return;
        var updated = await api.EditMessageAsync(item.Message.ChannelId, item.Message.Id, result.Trim());
        if (updated is not null)
            item.ApplyEdit(updated);
    }

    [RelayCommand]
    public async Task DeleteMessageAsync(MessageViewItem item)
    {
        if (item.IsSystemMessage) return;
        if (!item.CanDelete) return;
        var confirm = await Shell.Current.DisplayAlert(
            "Delete Message", "Delete this message? This cannot be undone.", "Delete", "Cancel");
        if (!confirm) return;
        var ok = await api.DeleteMessageAsync(item.Message.ChannelId, item.Message.Id);
        if (ok) item.ApplyDelete();
    }

    // Must be called on MainThread
    private void AppendToMessages(MessageViewItem item)
    {
        if (_isAtBottom)
        {
            Messages.Add(item);
            ScrollToLatestRequested?.Invoke();
        }
        else
        {
            if (_dividerItem is null && SelectedChannel is not null)
            {
                _dividerItem = MessageViewItem.CreateNewMessagesDivider(SelectedChannel.Id);
                Messages.Add(_dividerItem);
            }
            Messages.Add(item);
            UnreadBelowCount++;
            HasUnreadBelow = true;
        }
    }

    private void AddToStore(MessageViewItem item)
    {
        var source = item.Message.Source;
        if (source == MessageSource.Stream)
        {
            if (_streamMsgs.Any(m => m.Message.Id == item.Message.Id)) return;
            _streamMsgs.Add(item);
            PushOverlayMessage(item);
            if (SelectedTab == "Stream")
                MainThread.BeginInvokeOnMainThread(() =>
                {
                    if (!Messages.Any(m => m.Message.Id == item.Message.Id))
                        AppendToMessages(item);
                });
            return;
        }

        var isVoice = source == MessageSource.Voice;
        var store = isVoice ? _voiceMsgs : _textMsgs;

        if (store.Any(m => m.Message.Id == item.Message.Id)) return;
        store.Add(item);

        if ((isVoice && SelectedTab == "Voice") || (!isVoice && SelectedTab == "Text"))
            MainThread.BeginInvokeOnMainThread(() =>
            {
                if (!Messages.Any(m => m.Message.Id == item.Message.Id))
                    AppendToMessages(item);
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
    public async Task CopyInviteLinkAsync()
    {
        if (SelectedServer is null) return;
        try
        {
            var invite = await api.GetInviteAsync(SelectedServer.Id);
            if (invite is null) return;
            var text = $"{api.ServerUrl}/invite/{invite.Code}";
            await Clipboard.SetTextAsync(text);
            await Shell.Current.DisplayAlert("Invite Link Copied",
                $"Share this link:\n\n{text}\n\nAnyone with it can join {invite.ServerName}.", "OK");
        }
        catch (Exception ex)
        {
            await Shell.Current.DisplayAlert("Error", ex.Message, "OK");
        }
    }

    [RelayCommand]
    public async Task ResetInviteLinkAsync()
    {
        if (SelectedServer is null) return;
        var confirm = await Shell.Current.DisplayAlert("Reset Invite Link",
            "The old link will stop working. Generate a new one?", "Reset", "Cancel");
        if (!confirm) return;
        try
        {
            var invite = await api.ResetInviteAsync(SelectedServer.Id);
            if (invite is null) return;
            var text = $"{api.ServerUrl}/invite/{invite.Code}";
            await Clipboard.SetTextAsync(text);
            await Shell.Current.DisplayAlert("New Invite Link Copied",
                $"New link copied to clipboard:\n\n{text}", "OK");
        }
        catch (Exception ex)
        {
            await Shell.Current.DisplayAlert("Error", ex.Message, "OK");
        }
    }

    [RelayCommand]
    public async Task JoinByInviteAsync()
    {
        var code = await Shell.Current.DisplayPromptAsync(
            "Join by Invite",
            "Paste an invite link or just the code:",
            placeholder: "e.g. abc1234ef0");
        if (string.IsNullOrWhiteSpace(code)) return;

        // Strip URL prefix if the user pasted the full link
        var lastSlash = code.LastIndexOf('/');
        if (lastSlash >= 0) code = code[(lastSlash + 1)..];
        code = code.Trim();

        try
        {
            var server = await api.JoinByInviteAsync(code);
            if (server is null)
            {
                await Shell.Current.DisplayAlert("Invalid Code", "That invite link is invalid or has expired.", "OK");
                return;
            }
            Servers.Add(server);
            await SelectServerAsync(server);
        }
        catch (Exception ex)
        {
            await Shell.Current.DisplayAlert("Error", ex.Message, "OK");
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

    private async Task OnReconnectedAsync()
    {
        if (SelectedChannel is null)
        {
            toast.Show("FatGuysSpeak", "Reconnected to server.");
            return;
        }

        // SignalR group memberships are lost on reconnect — re-join channel group
        await hub.JoinChannelAsync(SelectedChannel.Id);

        // Find the highest message ID we already have across all stores
        var lastId = _textMsgs.Concat(_voiceMsgs).Concat(_streamMsgs)
            .Select(m => m.Message.Id)
            .DefaultIfEmpty(0)
            .Max();

        if (lastId == 0)
        {
            toast.Show("FatGuysSpeak", "Reconnected to server.");
            return;
        }

        var missed = await api.GetMessagesAfterAsync(SelectedChannel.Id, lastId);
        if (missed is null || missed.Count == 0)
        {
            toast.Show("FatGuysSpeak", "Reconnected to server.");
            return;
        }

        foreach (var dto in missed)
        {
            var item = Wire(new MessageViewItem(dto, api.CurrentUsername, api.CurrentUserId));
            AddToStore(item);
            _ = LoadPreviewAsync(item);
        }

        var n = missed.Count;
        toast.Show("FatGuysSpeak — Reconnected",
            $"{n} missed message{(n == 1 ? "" : "s")} in #{SelectedChannel.Name}");
    }

    private void OnKickedFromVoice()
    {
        if (!InVoice) return;
        MainThread.BeginInvokeOnMainThread(async () =>
        {
            await LeaveVoiceAsync();
            await Shell.Current.DisplayAlert("Removed from Voice", "You were removed from the voice channel by an admin.", "OK");
        });
    }

    [RelayCommand]
    public async Task LeaveVoiceAsync()
    {
        if (IsStreaming) { screen.StopCapture(); await hub.StopStreamAsync(); IsStreaming = false; }
        if (IsCameraOn) await StopCameraAsync();
        audio.AudioCaptured -= OnAudioCaptured;
        await hub.LeaveVoiceChannelAsync();
        if (IsLoopbackActive) { audio.StopLoopback(); IsLoopbackActive = false; }
        audio.StopCapture();
        audio.StopPlayback();
        InVoice = false;
        IsTestMic = false;
        IsPttActive = false;
        VoiceParticipants.Clear();
        MainThread.BeginInvokeOnMainThread(() => VideoTiles.Clear());
    }

    [RelayCommand]
    public void ToggleTestMic()
    {
        if (!InVoice) return;
        IsTestMic = !IsTestMic;
        audio.StopCapture();
        if (IsTestMic) audio.StartCapture(testMic: true);
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
        StopTypingDebounced();
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
        audio.StartPlayback();
        InVoice = true;
    }

    private void OnAudioCaptured(byte[] data)
    {
        _ = hub.SendVoiceDataAsync(data);
        var self = VoiceParticipants.FirstOrDefault(p => p.UserId == api.CurrentUserId);
        self?.SetSpeaking();
    }

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
        // Only fires for the channel the user is currently in (server sends to channel-{id} group).
        // Unread tracking for background channels is handled by OnNewMessageNotification.
        if (SelectedChannel?.Id != msg.ChannelId) return;

        var item = Wire(new MessageViewItem(msg, api.CurrentUsername, api.CurrentUserId));
        AddToStore(item);
        _ = LoadPreviewAsync(item);

        if (item.IsMention)
        {
            var preview = msg.Content.Length > 80 ? msg.Content[..80] + "…" : msg.Content;
            toast.Show($"@Mention from {msg.AuthorUsername}", preview);
        }
    }

    private void OnNewMessageNotification(MessageDto msg)
    {
        // Server broadcasts this to server-{id} group so every connected client hears about
        // every new message, regardless of which channel they are currently viewing.
        // We use it exclusively for unread badge tracking and background toasts.
        if (SelectedChannel?.Id == msg.ChannelId) return; // already shown via ReceiveMessage

        var channelItem = Channels.FirstOrDefault(c => c.Channel.Id == msg.ChannelId);
        if (channelItem is not null)
            MainThread.BeginInvokeOnMainThread(() => channelItem.UnreadCount++);

        if (msg.AuthorId != api.CurrentUserId)
        {
            var preview = msg.Content.Length > 80 ? msg.Content[..80] + "…" : msg.Content;
            toast.Show(msg.AuthorUsername, preview);
        }
    }

    private void OnMessageEdited(MessageDto updated)
    {
        MainThread.BeginInvokeOnMainThread(() =>
        {
            var item = _textMsgs.Concat(_voiceMsgs).FirstOrDefault(m => m.Message.Id == updated.Id);
            item?.ApplyEdit(updated);
        });
    }

    private void OnMessageDeleted(int messageId, int channelId)
    {
        MainThread.BeginInvokeOnMainThread(() =>
        {
            var item = _textMsgs.Concat(_voiceMsgs).FirstOrDefault(m => m.Message.Id == messageId);
            item?.ApplyDelete();
        });
    }

    private void OnUserStatusChanged(int userId, UserStatus status)
    {
        MainThread.BeginInvokeOnMainThread(() =>
        {
            var existing = Members.FirstOrDefault(m => m.Id == userId);
            if (existing is null) return;
            Members[Members.IndexOf(existing)] = existing with { Status = status };
        });
    }

    private void OnReactionsUpdated(ReactionsUpdatedDto dto)
    {
        MainThread.BeginInvokeOnMainThread(() =>
        {
            var item = _textMsgs.Concat(_voiceMsgs).FirstOrDefault(m => m.Message.Id == dto.MessageId);
            item?.ApplyReactions(dto.Reactions);
        });
    }

    private MessageViewItem Wire(MessageViewItem item)
    {
        item.ReactionRequested = (msg, emoji) => _ = ToggleReactionAsync(msg, emoji);
        item.CanModerate = IsAdminOrModerator;
        return item;
    }

    private async Task ToggleReactionAsync(MessageViewItem item, string emoji)
    {
        if (string.IsNullOrEmpty(emoji)) return;
        await api.ToggleReactionAsync(item.Message.ChannelId, item.Message.Id, emoji);
        // Server broadcasts ReactionsUpdated to channel — OnReactionsUpdated handles the UI update
    }

    private void OnUserTyping(int userId, string username, int channelId)
    {
        if (SelectedChannel?.Id != channelId || userId == api.CurrentUserId) return;
        MainThread.BeginInvokeOnMainThread(() =>
        {
            _typingUsersInChannel[userId] = username;
            OnPropertyChanged(nameof(TypingText));
        });
    }

    private void OnUserStoppedTyping(int userId, int channelId)
    {
        if (SelectedChannel?.Id != channelId) return;
        MainThread.BeginInvokeOnMainThread(() =>
        {
            _typingUsersInChannel.Remove(userId);
            OnPropertyChanged(nameof(TypingText));
        });
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
            VoiceParticipants.Add(new VoiceParticipantViewModel(state));

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

    private void OnUserSpeaking(int userId)
    {
        var participant = VoiceParticipants.FirstOrDefault(p => p.UserId == userId);
        participant?.SetSpeaking();
    }

    [RelayCommand]
    public async Task ToggleCameraAsync()
    {
        if (!InVoice || SelectedChannel is null) return;
        if (IsCameraOn)
            await StopCameraAsync();
        else
            await StartCameraAsync();
    }

    private async Task StartCameraAsync()
    {
        try
        {
            camera.FrameCaptured += OnCameraFrameCaptured;
            await camera.StartAsync();
            await hub.StartCameraAsync(SelectedChannel!.Id);

            var localTile = new VideoTileViewModel(api.CurrentUserId, api.CurrentUsername, isLocal: true);
            MainThread.BeginInvokeOnMainThread(() =>
            {
                VideoTiles.Add(localTile);
                OnPropertyChanged(nameof(HasVideoTiles));
            });
            IsCameraOn = true;
        }
        catch (Exception ex)
        {
            camera.FrameCaptured -= OnCameraFrameCaptured;
            await Shell.Current.DisplayAlert("Camera Error", ex.Message, "OK");
        }
    }

    private async Task StopCameraAsync()
    {
        camera.FrameCaptured -= OnCameraFrameCaptured;
        await camera.StopAsync();
        if (SelectedChannel is not null)
            await hub.StopCameraAsync(SelectedChannel.Id);
        IsCameraOn = false;
        MainThread.BeginInvokeOnMainThread(() =>
        {
            var local = VideoTiles.FirstOrDefault(t => t.IsLocal);
            if (local is not null) VideoTiles.Remove(local);
            OnPropertyChanged(nameof(HasVideoTiles));
        });
    }

    private void OnCameraFrameCaptured(byte[] jpeg)
    {
        if (Interlocked.CompareExchange(ref _cameraSending, 1, 0) != 0) return;
        _ = hub.SendCameraFrameAsync(jpeg)
              .ContinueWith(_ => Interlocked.Exchange(ref _cameraSending, 0));

        // Update local tile preview
        var local = VideoTiles.FirstOrDefault(t => t.IsLocal);
        local?.UpdateFrame(jpeg);
    }

    private void OnCameraStarted(int userId, string username, int channelId)
    {
        if (SelectedChannel?.Id != channelId) return;
        MainThread.BeginInvokeOnMainThread(() =>
        {
            if (VideoTiles.Any(t => t.UserId == userId)) return;
            VideoTiles.Add(new VideoTileViewModel(userId, username));
            OnPropertyChanged(nameof(HasVideoTiles));
        });
    }

    private void OnCameraStopped(int userId, int channelId)
    {
        MainThread.BeginInvokeOnMainThread(() =>
        {
            var tile = VideoTiles.FirstOrDefault(t => t.UserId == userId && !t.IsLocal);
            if (tile is not null)
            {
                VideoTiles.Remove(tile);
                OnPropertyChanged(nameof(HasVideoTiles));
            }
        });
    }

    private void OnCameraFrameReceived(int userId, byte[] jpeg)
    {
        var tile = VideoTiles.FirstOrDefault(t => t.UserId == userId && !t.IsLocal);
        tile?.UpdateFrame(jpeg);
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
        if (selected is null) return;

        try
        {
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
            // Set result BEFORE closing — CloseWindow fires Destroying synchronously on WinUI
            tcs.TrySetResult(chosen);
            Application.Current!.CloseWindow(pickerWindow);
        };
        pickerWindow.Destroying += (_, _) => tcs.TrySetResult(null);
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
        if (_settingsWindow is not null) return;
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
        Interlocked.Exchange(ref _pendingFrame, data);
        MainThread.BeginInvokeOnMainThread(() =>
        {
            var latest = Interlocked.Exchange(ref _pendingFrame, null);
            if (latest is null) return;
            StreamFrame = ImageSource.FromStream(() => new MemoryStream(latest));
        });
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
