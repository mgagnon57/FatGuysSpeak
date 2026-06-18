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

public partial class MainViewModel(ApiService api, ChatHubService hub, AudioService audio, SpeechService speech, PttService ptt, ScreenStreamService screen, RemoteInputService remoteInput, CameraService camera, SettingsViewModel settings, ToastNotificationService toast, UpdateService updateService) : ObservableObject
{
    private bool _initialized;
    private string? _versionSyncCheckedFor;   // server version last handled this session
    private static readonly ConcurrentDictionary<string, LinkPreviewDto?> PreviewCache = new();
    private static readonly Regex UrlRegex = new(@"https?://\S+", RegexOptions.Compiled);

    // Backing stores per source — Messages is whichever tab is active
    private readonly List<MessageViewItem> _textMsgs = [];
    private readonly List<MessageViewItem> _voiceMsgs = [];
    private readonly List<MessageViewItem> _streamMsgs = [];

    [ObservableProperty] private ObservableCollection<ServerViewItem> _servers = [];
    [ObservableProperty] private ObservableCollection<ChannelViewItem> _channels = [];
    private List<CategoryDto> _serverCategories = [];
    public ObservableCollection<CategoryViewItem> CategorizedChannels { get; } = [];
    [ObservableProperty] private ObservableCollection<MessageViewItem> _messages = [];
    [ObservableProperty] private ObservableCollection<MemberViewItem> _members = [];
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
    [ObservableProperty] private bool _isDmMode;
    [ObservableProperty] private DmConversationItem? _selectedDmConversation;
    [ObservableProperty] private bool _isSidebarOpen = Environment.GetEnvironmentVariable("FATGUYS_PHONE_MODE") != "1";

    public ObservableCollection<VideoTileViewModel> VideoTiles { get; } = [];
    public ObservableCollection<DmConversationItem> DmConversations { get; } = [];
    public bool HasVideoTiles => VideoTiles.Count > 0;

    private bool _isAtBottom = true;
    private MessageViewItem? _dividerItem;

    public string UnreadPillText => _unreadBelowCount == 1 ? "↓  1 new message" : $"↓  {_unreadBelowCount} new messages";
    public event Action? ScrollToLatestRequested;
    [ObservableProperty] private bool _isFullScreen;

    // Remote control state — sharer side
    [ObservableProperty] private bool _isBeingControlled;
    [ObservableProperty] private string? _controllerName;

    // Remote control state — controller side
    [ObservableProperty] private bool _isControlling;
    [ObservableProperty] private string? _controlledName;

    // Version sync state
    [ObservableProperty] private bool _versionSyncInProgress;   // blocking overlay visible
    [ObservableProperty] private string? _versionSyncTitle;     // "Updating to v3.0.0…"
    [ObservableProperty] private string? _versionSyncStage;     // "Downloading…" / "Installing…" / countdown
    [ObservableProperty] private double _versionSyncProgress;   // 0.0–1.0 for ProgressBar
    [ObservableProperty] private bool _versionMismatch;
    [ObservableProperty] private string? _versionMismatchText;

    public bool CanOfferControl => IsStreaming && DeviceInfo.Platform == DevicePlatform.WinUI;
    public bool CanRequestControl => ActiveStreamerId > 0 && !IsStreaming && DeviceInfo.Platform == DevicePlatform.WinUI;

    /// <summary>Exposes hub for page code-behind to call SendRemoteInput.</summary>
    public void SendRemoteInput(RemoteInputDto dto) => _ = hub.SendRemoteInput(dto);

    [ObservableProperty] private string? _activeStreamerName;
    [ObservableProperty] private int _activeStreamerId;
    [ObservableProperty] private ImageSource? _streamFrame;
    [ObservableProperty] private string? _pendingAttachmentUrl;
    [ObservableProperty] private string? _pendingAttachmentFileName;
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
    [ObservableProperty] private bool _isPinsOpen;
    public ObservableCollection<MessageViewItem> PinnedMessages { get; } = [];

    [ObservableProperty] private bool _isStreamChatOverlayVisible;
    public ObservableCollection<MessageViewItem> StreamOverlayMessages { get; } = [];

    // Current user info (for profile bar)
    [ObservableProperty] private string _currentUsername = api.CurrentUsername;
    [ObservableProperty] private string? _currentUserAvatarUrl = api.CurrentAvatarUrl;
    [ObservableProperty] private string _currentServerUrl = api.ServerUrl;
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
    private readonly HashSet<int> _blockedUserIds = [];
    private readonly Dictionary<int, ServerRole> _memberRoles = new();

    // Stored so they can be removed in UnsubscribeEvents on logout
    private Action<Exception?>? _hubReconnectingHandler;
    private Action<string?>? _hubReconnectedHandler;
    private Action<Exception?>? _hubDisconnectedHandler;
    private Action<double>? _micLevelHandler;
    private Action<string>? _hypothesisChangedHandler;
    private Action<string>? _keyLearnedHandler;

    [ObservableProperty] private int _streamLatencyMs;
    [ObservableProperty] private string _streamQualityLabel = "1080p 20fps";

    public ServerRole CurrentServerRole => SelectedServer?.MyRole ?? ServerRole.Member;
    public bool IsServerAdmin => CurrentServerRole >= ServerRole.Admin;
    public bool IsAdminOrModerator => CurrentServerRole >= ServerRole.Moderator;

    public bool IsTextTab => SelectedTab == "Text";
    public bool IsVoiceTab => SelectedTab == "Voice";
    public bool IsStreamTab => SelectedTab == "Stream";
    public bool IsTextOrStreamTab => IsTextTab || IsStreamTab;
    public bool ShowMessageInput => IsTextOrStreamTab || IsDmMode;

    public string  ConnectionStatusText  => HubConnectionState switch { "Reconnecting" => "Reconnecting…", "Disconnected" => "Disconnected", _ => "Connected" };
    public Color   ConnectionDotColor    => HubConnectionState switch { "Reconnecting" => Color.FromArgb("#f0a030"), "Disconnected" => Color.FromArgb("#ed4245"), _ => Color.FromArgb("#44bb44") };
    public Color   ConnectionStatusColor => ConnectionDotColor;
    public bool    IsConnectionBannerVisible => HubConnectionState != "Connected";
    public bool ShowMessages => !IsSearchOpen && !IsPinsOpen;
    public string SearchEmptyText => _searchExecuted ? "No messages found." : "Type a search term and press Search.";
    public bool HasActiveStream => ActiveStreamerName is not null || IsStreaming;
    public bool HasStreamFrame => StreamFrame is not null;
    public bool IsWaitingForStream => HasActiveStream && !HasStreamFrame;
    public bool StreamViewerVisible => IsStreamTab && !IsFullScreen;
    public bool HasPendingAttachment => PendingAttachmentUrl is not null;
    public bool PendingIsImage => PendingAttachmentFileName is null ||
        new[] { ".jpg", ".jpeg", ".png", ".gif", ".webp" }
            .Contains(Path.GetExtension(PendingAttachmentFileName).ToLowerInvariant());
    public string CameraButtonLabel => IsCameraOn ? "📷 Stop Camera" : "📷 Camera";
    public Color  CameraButtonColor => IsCameraOn ? Color.FromArgb("#ed4245") : Color.FromArgb("#3a3a3a");
    public bool IsNotDmMode => !IsDmMode;
    public bool ShowChannelUiElements => !IsDmMode && SelectedChannel is not null;
    public string CurrentChannelSlowmodeLabel =>
        Channels.FirstOrDefault(c => c.Channel.Id == SelectedChannel?.Id)?.SlowmodeLabel ?? "";
    public bool CurrentChannelHasSlowmode => !string.IsNullOrEmpty(CurrentChannelSlowmodeLabel);
    public string DmHeaderTitle => SelectedDmConversation?.OtherUsername ?? "Direct Messages";
    public string MessagePlaceholderText => IsDmMode && SelectedDmConversation is not null
        ? $"Message @{SelectedDmConversation.OtherUsername}"
        : SelectedChannel is not null ? $"Message #{SelectedChannel.Name}" : "Select a channel";
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
        OnPropertyChanged(nameof(ShowMessageInput));
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
        if (value) IsPinsOpen = false;
        if (!value)
        {
            SearchQuery = "";
            SearchResults.Clear();
            _searchExecuted = false;
        }
    }

    partial void OnIsPinsOpenChanged(bool value)
    {
        OnPropertyChanged(nameof(ShowMessages));
        if (value) IsSearchOpen = false;
        if (!value) PinnedMessages.Clear();
        else _ = LoadPinnedMessagesAsync();
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

    partial void OnIsDmModeChanged(bool value)
    {
        OnPropertyChanged(nameof(IsNotDmMode));
        OnPropertyChanged(nameof(ShowChannelUiElements));
        OnPropertyChanged(nameof(DmHeaderTitle));
        OnPropertyChanged(nameof(MessagePlaceholderText));
        OnPropertyChanged(nameof(ShowMessageInput));
        if (value)
        {
            _ = LoadDmConversationsAsync();
        }
        else
        {
            SelectedDmConversation = null;
            // Restore channel messages if a channel is selected
            if (SelectedChannel is not null)
                Messages = new ObservableCollection<MessageViewItem>(_textMsgs);
            else
                Messages.Clear();
        }
    }

    partial void OnSelectedDmConversationChanged(DmConversationItem? value)
    {
        foreach (var dc in DmConversations) dc.IsSelected = false;
        if (value is not null) value.IsSelected = true;
        OnPropertyChanged(nameof(DmHeaderTitle));
        OnPropertyChanged(nameof(MessagePlaceholderText));
        IsPinsOpen = false;
        StopTypingDebounced();
        _typingUsersInChannel.Clear();
        OnPropertyChanged(nameof(TypingText));
        if (value is not null)
            _ = LoadDmMessagesAsync(value.ConversationId);
        else
            Messages.Clear();
    }

    partial void OnSelectedChannelChanged(ChannelDto? value)
    {
        OnPropertyChanged(nameof(ShowChannelUiElements));
        OnPropertyChanged(nameof(MessagePlaceholderText));
        OnPropertyChanged(nameof(CurrentChannelSlowmodeLabel));
        OnPropertyChanged(nameof(CurrentChannelHasSlowmode));
        IsPinsOpen = false;
    }
    partial void OnIsStreamingChanged(bool value)
    {
        OnPropertyChanged(nameof(StreamButtonLabel));
        OnPropertyChanged(nameof(StreamButtonColor));
        OnPropertyChanged(nameof(HasActiveStream));
        OnPropertyChanged(nameof(IsWaitingForStream));
        OnPropertyChanged(nameof(StreamHeaderText));
        OnPropertyChanged(nameof(StreamTabOpacity));
        OnPropertyChanged(nameof(CanOfferControl));
        OnPropertyChanged(nameof(CanRequestControl));
    }
    partial void OnActiveStreamerIdChanged(int value) => OnPropertyChanged(nameof(CanRequestControl));
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
    partial void OnPendingAttachmentUrlChanged(string? value)
    {
        OnPropertyChanged(nameof(HasPendingAttachment));
        OnPropertyChanged(nameof(PendingIsImage));
    }

    partial void OnMessageInputChanged(string value)
    {
        if (!hub.IsConnected) return;
        bool inDm = IsDmMode && SelectedDmConversation is not null;
        if (!inDm && SelectedChannel is null) return;

        if (string.IsNullOrEmpty(value))
        {
            StopTypingDebounced();
            return;
        }

        if (!_isTyping)
        {
            _isTyping = true;
            if (inDm)
                _ = hub.StartDmTypingAsync(SelectedDmConversation!.ConversationId);
            else
                _ = hub.StartTypingAsync(SelectedChannel!.Id);
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
        if (!hub.IsConnected) return;
        if (IsDmMode && SelectedDmConversation is not null)
            _ = hub.StopDmTypingAsync(SelectedDmConversation.ConversationId);
        else if (SelectedChannel is not null)
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
        hub.ChannelUpdated    += OnChannelUpdated;
        hub.ChannelDeleted    += OnChannelDeleted;
        hub.ForceJoinChannel  += OnForceJoinChannel;
        hub.UserJoinedChannel += OnUserJoinedChannel;
        hub.UserLeftChannel   += OnUserLeftChannel;
        hub.VoiceDataReceived += OnVoiceDataReceived;
        hub.StreamStarted        += OnStreamStarted;
        hub.StreamStopped        += OnStreamStopped;
        hub.StreamFrameReceived  += OnStreamFrameReceived;
        hub.StreamAudioReceived  += OnStreamAudioReceived;
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
        hub.DirectMessageReceived   += OnDirectMessageReceived;
        hub.DirectMessageDeleted    += OnDirectMessageDeleted;
        hub.DmUserTyping            += OnDmUserTyping;
        hub.DmUserStoppedTyping     += OnDmUserStoppedTyping;
        hub.DmConversationRead      += OnDmConversationRead;
        hub.MessagePinned           += OnMessagePinned;
        hub.MessageUnpinned         += OnMessageUnpinned;
        hub.DmMessagePinned         += OnDmMessagePinned;
        hub.DmMessageUnpinned       += OnDmMessageUnpinned;
        hub.KickedFromServer        += OnKickedFromServer;
        hub.CategoryCreated         += OnCategoryCreated;
        hub.CategoryDeleted         += OnCategoryDeleted;
        hub.CategoryRenamed         += OnCategoryRenamed;
        hub.ChannelCategoryChanged  += OnChannelCategoryChanged;
        hub.MemberRoleChanged += OnMemberRoleChanged;
        hub.ChannelSlowmodeUpdated += OnChannelSlowmodeUpdated;
        hub.ThreadReplyReceived += OnThreadReplyReceived;
        hub.UserMuted += OnUserMuted;
        hub.UserTempBanned += OnUserTempBanned;
        hub.ControlRequested    += OnControlRequested;
        hub.ControlOffered      += OnControlOffered;
        hub.ControlActive       += OnControlActive;
        hub.ControlGranted      += OnControlGranted;
        hub.ControlDeclined     += OnControlDeclined;
        hub.ControlBusy         += OnControlBusy;
        hub.ControlEnded        += OnControlEnded;
        hub.RemoteInputReceived += OnRemoteInputReceived;
        _hubReconnectingHandler = _ => MainThread.BeginInvokeOnMainThread(() => HubConnectionState = "Reconnecting");
        hub.Reconnecting += _hubReconnectingHandler;
        _hubReconnectedHandler = _ => MainThread.BeginInvokeOnMainThread(async () =>
        {
            HubConnectionState = "Connected";
            // Server always tears down control sessions on disconnect, so reset stale client state.
            IsBeingControlled = false;
            ControllerName = null;
            IsControlling = false;
            ControlledName = null;
            await OnReconnectedAsync();
        });
        hub.Reconnected  += _hubReconnectedHandler;
        _hubDisconnectedHandler = _ => MainThread.BeginInvokeOnMainThread(() => HubConnectionState = "Disconnected");
        hub.Disconnected += _hubDisconnectedHandler;
        screen.FrameCaptured       += OnScreenFrameCaptured;
        screen.StreamAudioCaptured += OnStreamAudioCaptured;
        _micLevelHandler = level => MainThread.BeginInvokeOnMainThread(() => MicLevel = level);
        audio.MicLevelChanged += _micLevelHandler;
        speech.TextRecognized  += OnSpeechRecognized;
        _hypothesisChangedHandler = text => MainThread.BeginInvokeOnMainThread(() => HypothesisText = text);
        speech.HypothesisChanged += _hypothesisChangedHandler;
        ptt.PttDown    += OnPttDown;
        ptt.PttUp      += OnPttUp;
        _keyLearnedHandler = name =>
        {
            PttKeyName = name;
            IsSettingPttKey = false;
            _ = Shell.Current.DisplayAlert("Push to Talk Key Set", $"Push to Talk key bound to: {name}\n\nHold that key while in a voice channel to transmit.", "OK");
        };
        ptt.KeyLearned += _keyLearnedHandler;
        PttKeyName = ptt.PttKeyName;
        IsStreamChatOverlayVisible = Preferences.Get("stream_chat_overlay", false);
    }

    [RelayCommand]
    public async Task LoadServersAsync()
    {
        var list = await api.GetServersAsync();
        Servers = new ObservableCollection<ServerViewItem>(
            (list ?? []).Select(s => ServerViewItem.FromDto(s, s.HasIcon ? api.GetServerIconUrl(s.Id) : null)));
        var blocks = await api.GetBlockedUsersAsync();
        _blockedUserIds.Clear();
        foreach (var b in blocks ?? [])
            _blockedUserIds.Add(b.UserId);
        _ = SyncClientToServerAsync();
    }

    private async Task SyncClientToServerAsync()
    {
        if (VersionSyncInProgress) return;   // a sync is already underway; don't re-enter

        var serverVersion = await api.GetServerVersionAsync();
        if (string.IsNullOrEmpty(serverVersion)) return;          // can't evaluate -> connect as-is

        var mine = updateService.InstalledVersion;
        if (mine is null) return;                                 // dev / not Velopack-installed

        if (FatGuysSpeak.Shared.VersionCompat.SameMajor(mine, serverVersion))
            return;                                               // compatible -> connect, no UI

        if (_versionSyncCheckedFor == serverVersion) return;      // already handled this server this session
        _versionSyncCheckedFor = serverVersion;

        var downgrade = FatGuysSpeak.Shared.SemVer.Compare(mine, serverVersion) > 0;
        var verb = downgrade ? "Downgrading" : "Updating";
        MainThread.BeginInvokeOnMainThread(() =>
        {
            VersionMismatch = false;
            VersionSyncTitle = $"{verb} to v{serverVersion}…";
            VersionSyncStage = "Downloading…";
            VersionSyncProgress = 0;
            VersionSyncInProgress = true;
        });

        var progress = new Progress<int>(p =>
            MainThread.BeginInvokeOnMainThread(() => VersionSyncProgress = p / 100.0));

        var outcome = await updateService.PrepareAsync(serverVersion, progress);

        if (outcome == Services.UpdateSyncOutcome.Prepared)
        {
            MainThread.BeginInvokeOnMainThread(() => VersionSyncStage = "Installing…");
            await Task.Delay(600);
            for (var n = 3; n >= 1; n--)
            {
                var sec = n;
                MainThread.BeginInvokeOnMainThread(() =>
                    VersionSyncStage = $"Restarting to apply v{serverVersion} in {sec}…");
                await Task.Delay(1000);
            }
            updateService.ApplyAndRestart();                      // swaps files + relaunches; process exits
            return;
        }

        // Compatible (defensive) or Unavailable -> tear down the overlay
        MainThread.BeginInvokeOnMainThread(() =>
        {
            VersionSyncInProgress = false;
            if (outcome == Services.UpdateSyncOutcome.Unavailable)
            {
                VersionMismatchText = $"This server needs client v{serverVersion}, but auto-update isn't "
                    + $"available right now (you're on v{mine}). It'll retry next time you connect.";
                VersionMismatch = true;
            }
        });
    }

    [RelayCommand]
    private void DismissMismatch() => VersionMismatch = false;

    [RelayCommand]
    public async Task SelectServerAsync(ServerViewItem item)
    {
        foreach (var s in Servers) s.IsSelected = false;
        item.IsSelected = true;
        SelectedServer = item.Server;
        var cats = await api.GetCategoriesAsync(item.Server.Id);
        _serverCategories = cats ?? [];
        var channelList = await api.GetChannelsAsync(item.Server.Id);
        Channels = new ObservableCollection<ChannelViewItem>((channelList ?? []).Select(c => new ChannelViewItem(c)));
        RebuildCategorizedChannels();
        var onlineList = hub.IsConnected ? await hub.GetOnlineUsersAsync(item.Server.Id) : [];
        // Load role map first so MemberViewItems are built with correct roles
        _memberRoles.Clear();
        try
        {
            var roleList = await api.GetMemberRolesAsync(item.Server.Id);
            if (roleList is not null)
                foreach (var m in roleList)
                    _memberRoles[m.UserId] = m.Role;
        }
        catch { /* role map unavailable; icons will default to Member */ }
        Members = new ObservableCollection<MemberViewItem>(
            onlineList.Select(u => new MemberViewItem(u, _memberRoles.GetValueOrDefault(u.Id))));
        SelectedChannel = null;
        Messages.Clear();
    }

    [RelayCommand]
    public async Task SelectChannelAsync(ChannelViewItem item)
    {
        // Stop watching any previous stream
        if (ActiveStreamerName is not null && SelectedChannel is not null && !IsStreaming)
        {
            await hub.StopWatchingAsync(SelectedChannel.Id);
            audio.StopStreamPlayback();
        }
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
        item.MentionCount = 0;
        _slowmodeCts?.Cancel();
        SlowmodeCooldown = 0;
        CloseThread();
        SelectedChannel = item.Channel;
        await hub.JoinChannelAsync(item.Channel.Id);

        var occupants = hub.IsConnected ? await hub.GetChannelOccupantsAsync(item.Channel.Id) : [];
        MainThread.BeginInvokeOnMainThread(() =>
        {
            item.Occupants.Clear();
            foreach (var u in occupants)
                item.Occupants.Add(u);
        });

        // If there's an active stream in this channel, start receiving frames and audio now
        if (!IsStreaming && _streamChannelId == item.Channel.Id)
        {
            _ = hub.WatchStreamAsync(item.Channel.Id);
            audio.StartStreamPlayback();
        }

        var msgs = await api.GetMessagesAsync(item.Channel.Id);
        _textMsgs.Clear();
        _voiceMsgs.Clear();
        _streamMsgs.Clear();
        foreach (var m in (msgs ?? []).OrderBy(m => m.CreatedAt))
        {
            if (_blockedUserIds.Contains(m.AuthorId)) continue;
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
        if (userId == 0) return;
        var vm = new UserProfileViewModel(api, SelectedServer?.Id ?? 0, _blockedUserIds.Contains(userId));
        var page = new Pages.UserProfilePage { BindingContext = vm };
        var window = new Window(page)
        {
            Title = "User Profile",
            Width = 340,
            Height = 520,
            MinimumWidth = 280,
            MinimumHeight = 400,
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
    public void ToggleSidebar() => IsSidebarOpen = !IsSidebarOpen;

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
    public void TogglePins() => IsPinsOpen = !IsPinsOpen;

    private async Task LoadPinnedMessagesAsync()
    {
        PinnedMessages.Clear();
        if (IsDmMode && SelectedDmConversation is not null)
        {
            var dtos = await api.GetDmPinsAsync(SelectedDmConversation.ConversationId);
            if (dtos is null) return;
            MainThread.BeginInvokeOnMainThread(() =>
            {
                foreach (var dto in dtos)
                    PinnedMessages.Add(new MessageViewItem(DmToMessageDto(dto), api.CurrentUsername, api.CurrentUserId));
            });
        }
        else if (SelectedChannel is not null)
        {
            var dtos = await api.GetChannelPinsAsync(SelectedChannel.Id);
            if (dtos is null) return;
            MainThread.BeginInvokeOnMainThread(() =>
            {
                foreach (var dto in dtos)
                    PinnedMessages.Add(Wire(new MessageViewItem(dto, api.CurrentUsername, api.CurrentUserId)));
            });
        }
    }

    [RelayCommand]
    public async Task TogglePinMessage(MessageViewItem item)
    {
        if (item.IsPinned)
        {
            if (IsDmMode && SelectedDmConversation is not null)
                await api.UnpinDmMessageAsync(SelectedDmConversation.ConversationId, item.Message.Id);
            else if (SelectedChannel is not null)
                await api.UnpinChannelMessageAsync(SelectedChannel.Id, item.Message.Id);
        }
        else
        {
            if (IsDmMode && SelectedDmConversation is not null)
                await api.PinDmMessageAsync(SelectedDmConversation.ConversationId, item.Message.Id);
            else if (SelectedChannel is not null)
                await api.PinChannelMessageAsync(SelectedChannel.Id, item.Message.Id);
        }
    }

    [RelayCommand]
    public async Task UnpinMessage(MessageViewItem item)
    {
        if (IsDmMode && SelectedDmConversation is not null)
            await api.UnpinDmMessageAsync(SelectedDmConversation.ConversationId, item.Message.Id);
        else if (SelectedChannel is not null)
            await api.UnpinChannelMessageAsync(SelectedChannel.Id, item.Message.Id);
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
        IsSearchingGifs = true;
        GifErrorText = "";
        try
        {
            var results = string.IsNullOrWhiteSpace(GifSearchQuery)
                ? await api.GetTrendingGifsAsync()
                : await api.SearchGifsAsync(GifSearchQuery);
            GifResults.Clear();
            foreach (var r in results ?? []) GifResults.Add(r);
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
        await PostMessageAsync("", source, gifResult.Url, null);
    }

    [RelayCommand]
    public async Task SendMessageAsync()
    {
        if (IsDmMode && SelectedDmConversation is not null)
        {
            await SendDmMessageAsync();
            return;
        }

        if (SelectedChannel is null) return;
        if (string.IsNullOrWhiteSpace(MessageInput) && PendingAttachmentUrl is null) return;

        StopTypingDebounced();
        IsEmojiPickerOpen = false;

        var text = MessageInput.Trim();
        var attachmentUrl = PendingAttachmentUrl;
        var attachmentFileName = PendingAttachmentFileName;
        MessageInput = "";
        PendingAttachmentUrl = null;
        PendingAttachmentFileName = null;

        var source = IsStreamTab ? MessageSource.Stream : MessageSource.Text;
        await PostMessageAsync(text, source, attachmentUrl, attachmentFileName);
    }

    private async Task SendDmMessageAsync()
    {
        if (string.IsNullOrWhiteSpace(MessageInput) && PendingAttachmentUrl is null) return;
        StopTypingDebounced();
        IsEmojiPickerOpen = false;
        var text = MessageInput.Trim();
        var url = PendingAttachmentUrl;
        var fname = PendingAttachmentFileName;
        MessageInput = "";
        PendingAttachmentUrl = null;
        PendingAttachmentFileName = null;
        await api.SendDmAsync(SelectedDmConversation!.ConversationId,
            text.Length > 0 ? text : null, url, fname);
    }

    [ObservableProperty] private int _slowmodeCooldown;
    [ObservableProperty] private bool _isCooldownActive;
    public string SlowmodeCooldownLabel => SlowmodeCooldown > 0 ? $"⏱ Please wait {SlowmodeCooldown}s" : "";

    partial void OnSlowmodeCooldownChanged(int value)
    {
        IsCooldownActive = value > 0;
        OnPropertyChanged(nameof(SlowmodeCooldownLabel));
    }

    private CancellationTokenSource? _slowmodeCts;

    private async Task StartSlowmodeCooldownAsync(int seconds)
    {
        _slowmodeCts?.Cancel();
        _slowmodeCts = new CancellationTokenSource();
        var cts = _slowmodeCts;
        var remaining = seconds;
        MainThread.BeginInvokeOnMainThread(() => SlowmodeCooldown = remaining);
        while (remaining > 0)
        {
            try { await Task.Delay(1000, cts.Token); }
            catch (OperationCanceledException) { break; }
            if (cts.IsCancellationRequested) break;
            MainThread.BeginInvokeOnMainThread(() => SlowmodeCooldown = --remaining);
        }
    }

    [ObservableProperty] private MessageViewItem? _threadRootItem;
    [ObservableProperty] private bool _isThreadOpen;
    [ObservableProperty] private string _threadInput = "";
    public ObservableCollection<MessageViewItem> ThreadMessages { get; } = [];

    [RelayCommand]
    private async Task OpenThreadAsync(MessageViewItem item)
    {
        ThreadRootItem = item;
        IsThreadOpen = true;
        ThreadMessages.Clear();
        ThreadInput = "";
        if (SelectedChannel is null) return;
        var dtos = await api.GetThreadMessagesAsync(SelectedChannel.Id, item.Message.Id);
        if (dtos is null) return;
        MainThread.BeginInvokeOnMainThread(() =>
        {
            ThreadMessages.Clear();
            foreach (var dto in dtos)
            {
                var m = Wire(new MessageViewItem(dto, api.CurrentUsername, api.CurrentUserId));
                if (!ThreadMessages.Any(x => x.Message.Id == m.Message.Id))
                    ThreadMessages.Add(m);
            }
        });
    }

    [RelayCommand]
    private void CloseThread()
    {
        IsThreadOpen = false;
        ThreadRootItem = null;
        ThreadMessages.Clear();
        ThreadInput = "";
    }

    [RelayCommand]
    private async Task SendThreadMessageAsync()
    {
        if (ThreadRootItem is null || SelectedChannel is null) return;
        var text = ThreadInput.Trim();
        if (string.IsNullOrEmpty(text)) return;
        ThreadInput = "";
        var (dto, err) = await api.SendMessageAsync(SelectedChannel.Id, text, threadId: ThreadRootItem.Message.Id);
        if (dto is not null)
        {
            MainThread.BeginInvokeOnMainThread(() =>
            {
                var item = Wire(new MessageViewItem(dto, api.CurrentUsername, api.CurrentUserId));
                if (!ThreadMessages.Any(x => x.Message.Id == item.Message.Id))
                    ThreadMessages.Add(item);
            });
        }
        else if (!string.IsNullOrEmpty(err))
        {
            var clean = err.Trim('"');
            toast.Show("Cannot send message", clean);
        }
    }

    private void OnThreadReplyReceived(MessageDto reply, int rootMessageId, int newCount)
    {
        MainThread.BeginInvokeOnMainThread(() =>
        {
            var root = _textMsgs.FirstOrDefault(m => m.Message.Id == rootMessageId);
            if (root is not null) root.ReplyCount = newCount;

            if (IsThreadOpen && ThreadRootItem?.Message.Id == rootMessageId
                && !ThreadMessages.Any(m => m.Message.Id == reply.Id))
            {
                var item = Wire(new MessageViewItem(reply, api.CurrentUsername, api.CurrentUserId));
                ThreadMessages.Add(item);
            }
        });
    }

    private void OnUserMuted(int userId, DateTime? mutedUntil)
    {
        MainThread.BeginInvokeOnMainThread(() =>
        {
            if (userId == api.CurrentUserId)
            {
                var msg = mutedUntil.HasValue
                    ? $"You have been muted until {mutedUntil.Value.ToLocalTime():HH:mm}."
                    : "Your mute has been lifted.";
                toast.Show("Muted", msg);
            }
        });
    }

    private void OnUserTempBanned(int userId, DateTime expiresAt)
    {
        if (userId == api.CurrentUserId)
            MainThread.BeginInvokeOnMainThread(() =>
                toast.Show("Banned", $"You have been temporarily banned until {expiresAt.ToLocalTime():HH:mm}."));
    }

    [RelayCommand]
    public async Task MuteUserByIdAsync(int userId)
    {
        if (SelectedServer is null) return;
        var result = await Shell.Current.DisplayActionSheet(
            "Mute duration", "Cancel", null,
            "5 minutes", "30 minutes", "1 hour", "24 hours");
        var seconds = result switch
        {
            "5 minutes"  => 300,
            "30 minutes" => 1800,
            "1 hour"     => 3600,
            "24 hours"   => 86400,
            _ => 0
        };
        if (seconds == 0) return;
        await api.MuteUserAsync(SelectedServer.Id, userId, seconds);
    }

    [RelayCommand]
    public async Task TempBanUserByIdAsync(int userId)
    {
        if (SelectedServer is null) return;
        var result = await Shell.Current.DisplayActionSheet(
            "Temp ban duration", "Cancel", null,
            "1 hour", "24 hours", "7 days", "30 days");
        var seconds = result switch
        {
            "1 hour"  => 3600,
            "24 hours" => 86400,
            "7 days"  => 604800,
            "30 days" => 2592000,
            _ => 0
        };
        if (seconds == 0) return;
        await api.TempBanUserAsync(SelectedServer.Id, userId, seconds);
    }

    private async Task PostMessageAsync(string text, MessageSource source, string? attachmentUrl = null, string? attachmentFileName = null)
    {
        if (SelectedChannel is null) return;
        if (string.IsNullOrWhiteSpace(text) && attachmentUrl is null) return;
        if (IsCooldownActive)
        {
            toast.Show("Slowmode", SlowmodeCooldownLabel);
            return;
        }
        var replyToId = _replyingToItem?.Message.Id;
        CancelReply();
        var (msg, err) = await api.SendMessageAsync(SelectedChannel.Id, text, source, attachmentUrl, replyToId, attachmentFileName);
        if (msg is not null)
        {
            var item = Wire(new MessageViewItem(msg, api.CurrentUsername, api.CurrentUserId));
            AddToStore(item);
            _ = LoadPreviewAsync(item);

            var channelItem = Channels.FirstOrDefault(c => c.Channel.Id == SelectedChannel.Id);
            if (channelItem?.SlowmodeSeconds > 0
                && _memberRoles.GetValueOrDefault(api.CurrentUserId) < ServerRole.Moderator)
                _ = StartSlowmodeCooldownAsync(channelItem.SlowmodeSeconds);
        }
        else if (!string.IsNullOrEmpty(err))
        {
            var clean = err.Trim('"');
            toast.Show("Cannot send message", clean);
        }
    }

    [RelayCommand]
    public async Task PickAttachmentAsync()
    {
        try
        {
            var result = await FilePicker.PickAsync(new PickOptions
            {
                PickerTitle = "Choose a file or image to share",
            });
            if (result is null) return;

            using var stream = await result.OpenReadAsync();
            var contentType = result.ContentType ?? "application/octet-stream";
            var dto = await api.UploadAttachmentAsync(stream, result.FileName, contentType);
            if (dto is not null)
            {
                PendingAttachmentUrl = dto.Url;
                PendingAttachmentFileName = dto.OriginalFileName ?? result.FileName;
            }
        }
        catch (Exception ex)
        {
            await Shell.Current.DisplayAlert("Upload Error", ex.Message, "OK");
        }
    }

    [RelayCommand]
    public void ClearAttachment()
    {
        PendingAttachmentUrl = null;
        PendingAttachmentFileName = null;
    }

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
        bool ok;
        if (IsDmMode && SelectedDmConversation is not null)
            ok = await api.DeleteDmMessageAsync(SelectedDmConversation.ConversationId, item.Message.Id);
        else
            ok = await api.DeleteMessageAsync(item.Message.ChannelId, item.Message.Id);
        if (ok) RemoveFromStore(item);
    }

    [RelayCommand]
    public async Task ToggleBlockUser(MessageViewItem item)
    {
        await BlockUserByIdAsync(item.Message.AuthorId);
    }

    [RelayCommand]
    public async Task BlockUserByIdAsync(int userId)
    {
        if (userId == 0 || userId == api.CurrentUserId) return;
        if (_blockedUserIds.Contains(userId))
        {
            await api.UnblockUserAsync(userId);
            _blockedUserIds.Remove(userId);
        }
        else
        {
            var ok = await api.BlockUserAsync(userId);
            if (!ok) return;
            _blockedUserIds.Add(userId);
            MainThread.BeginInvokeOnMainThread(() =>
            {
                var purge = _textMsgs.Where(m => m.Message.AuthorId == userId).ToList();
                foreach (var m in purge) _textMsgs.Remove(m);
                purge = _voiceMsgs.Where(m => m.Message.AuthorId == userId).ToList();
                foreach (var m in purge) _voiceMsgs.Remove(m);
                Messages = SelectedTab == "Voice"
                    ? new ObservableCollection<MessageViewItem>(_voiceMsgs)
                    : new ObservableCollection<MessageViewItem>(_textMsgs);
            });
        }
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
    public async Task JoinServerAsync(int serverId)
    {
        var ok = await api.JoinServerAsync(serverId);
        if (ok)
        {
            var list = await api.GetServersAsync();
            Servers = new ObservableCollection<ServerViewItem>(
                (list ?? []).Select(s => ServerViewItem.FromDto(s, s.HasIcon ? api.GetServerIconUrl(s.Id) : null)));
        }
    }

    [RelayCommand]
    public async Task ChangeServerIconAsync()
    {
        if (SelectedServer is null || !IsServerAdmin) return;

        var result = await FilePicker.PickAsync(new PickOptions
        {
            PickerTitle = "Pick a server icon",
            FileTypes = FilePickerFileType.Images,
        });
        if (result is null) return;

        using var stream = await result.OpenReadAsync();
        var contentType = result.ContentType ?? "image/png";
        bool ok = await api.UploadServerIconAsync(SelectedServer.Id, stream, result.FileName, contentType);
        if (ok)
            await RefreshServerListAsync();
        else
            await Shell.Current.DisplayAlert("Error", "Failed to upload icon.", "OK");
    }

    [RelayCommand]
    public async Task RemoveServerIconAsync()
    {
        if (SelectedServer is null || !IsServerAdmin) return;
        bool ok = await api.DeleteServerIconAsync(SelectedServer.Id);
        if (ok)
            await RefreshServerListAsync();
    }

    private async Task RefreshServerListAsync()
    {
        var list = await api.GetServersAsync();
        var currentId = SelectedServer?.Id;
        Servers = new ObservableCollection<ServerViewItem>(
            (list ?? []).Select(s => ServerViewItem.FromDto(s, s.HasIcon ? api.GetServerIconUrl(s.Id) : null)));
        if (currentId.HasValue)
        {
            var selected = Servers.FirstOrDefault(s => s.Server.Id == currentId.Value);
            if (selected is not null) selected.IsSelected = true;
        }
    }

    [RelayCommand]
    public async Task SetServerNotifLevelAsync(string levelStr)
    {
        if (SelectedServer is null) return;
        var level = Enum.Parse<NotifLevel>(levelStr);
        await api.SetServerNotifLevelAsync(SelectedServer.Id, level);
        var serverItem = Servers.FirstOrDefault(s => s.IsSelected);
        if (serverItem is not null) serverItem.ServerNotifLevel = level;
    }

    [RelayCommand]
    public async Task SetChannelNotifLevelAsync(string levelStr)
    {
        if (SelectedChannel is null) return;
        var level = Enum.Parse<NotifLevel>(levelStr);
        await api.SetChannelNotifLevelAsync(SelectedChannel.Id, level);
        var channelItem = Channels.FirstOrDefault(c => c.Channel.Id == SelectedChannel.Id);
        if (channelItem is not null) channelItem.NotifLevelOverride = level;
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
            var item = ServerViewItem.FromDto(server);
            Servers.Add(item);
            await SelectServerAsync(item);
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

        // SignalR group memberships are lost on reconnect — re-join channel and voice groups
        await hub.JoinChannelAsync(SelectedChannel.Id);
        if (InVoice && _voiceChannelId != 0)
            await hub.JoinVoiceChannelAsync(_voiceChannelId);

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

    private void OnKickedFromServer(int serverId)
    {
        MainThread.BeginInvokeOnMainThread(async () =>
        {
            try
            {
                var item = Servers.FirstOrDefault(s => s.Server.Id == serverId);
                if (item is null) return;
                Servers.Remove(item);
                if (SelectedServer?.Id == serverId)
                {
                    if (Servers.Count > 0)
                        await SelectServerAsync(Servers[0]);
                    else
                    {
                        SelectedServer = null;
                        Channels.Clear();
                        Members.Clear();
                        Messages.Clear();
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[OnKickedFromServer] {ex}");
            }
        });
    }

    private void OnKickedFromVoice()
    {
        if (!InVoice) return;
        MainThread.BeginInvokeOnMainThread(async () =>
        {
            try
            {
                await LeaveVoiceAsync();
                await Shell.Current.DisplayAlert("Removed from Voice", "You were removed from the voice channel by an admin.", "OK");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[OnKickedFromVoice] {ex}");
            }
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
        _voiceChannelId = 0;
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
            bool consented = Preferences.Get("PttConsentGiven", false);
            if (!consented)
            {
                bool accept = await Shell.Current.DisplayAlert(
                    "Push to Talk — Keyboard Access",
                    "PTT requires a global keyboard hook that reads key events from all applications, including when FatGuysSpeak is in the background. No keystrokes are logged or transmitted.\n\nAllow this?",
                    "Allow", "Cancel");
                if (!accept) return;
                Preferences.Set("PttConsentGiven", true);
            }
            ptt.TryInstall();
            await Task.Delay(200);
            if (!ptt.IsHookInstalled)
            {
                await Shell.Current.DisplayAlert("Error", "Keyboard hook could not be installed. Try restarting the app.", "OK");
                return;
            }
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
        UnsubscribeEvents();
        api.ClearToken();
        _initialized = false;
        await Shell.Current.GoToAsync("//login");
    }

    private void UnsubscribeEvents()
    {
        hub.MessageReceived        -= OnMessageReceived;
        hub.UserJoinedVoice        -= OnUserJoinedVoice;
        hub.UserLeftVoice          -= OnUserLeftVoice;
        hub.UserConnected          -= OnUserConnected;
        hub.UserDisconnected       -= OnUserDisconnected;
        hub.UserJoinedServer       -= OnUserJoinedServer;
        hub.ChannelCreated         -= OnChannelCreated;
        hub.ChannelUpdated         -= OnChannelUpdated;
        hub.ChannelDeleted         -= OnChannelDeleted;
        hub.ForceJoinChannel       -= OnForceJoinChannel;
        hub.UserJoinedChannel      -= OnUserJoinedChannel;
        hub.UserLeftChannel        -= OnUserLeftChannel;
        hub.VoiceDataReceived      -= OnVoiceDataReceived;
        hub.StreamStarted          -= OnStreamStarted;
        hub.StreamStopped          -= OnStreamStopped;
        hub.StreamFrameReceived    -= OnStreamFrameReceived;
        hub.StreamAudioReceived    -= OnStreamAudioReceived;
        hub.StreamNotification     -= OnStreamNotification;
        hub.UserTyping             -= OnUserTyping;
        hub.UserStoppedTyping      -= OnUserStoppedTyping;
        hub.MessageEdited          -= OnMessageEdited;
        hub.MessageDeleted         -= OnMessageDeleted;
        hub.NewMessageNotification -= OnNewMessageNotification;
        hub.ReactionsUpdated       -= OnReactionsUpdated;
        hub.UserStatusChanged      -= OnUserStatusChanged;
        hub.KickedFromVoice        -= OnKickedFromVoice;
        hub.UserSpeaking           -= OnUserSpeaking;
        hub.CameraStarted          -= OnCameraStarted;
        hub.CameraStopped          -= OnCameraStopped;
        hub.CameraFrameReceived    -= OnCameraFrameReceived;
        hub.DirectMessageReceived  -= OnDirectMessageReceived;
        hub.DirectMessageDeleted   -= OnDirectMessageDeleted;
        hub.DmUserTyping           -= OnDmUserTyping;
        hub.DmUserStoppedTyping    -= OnDmUserStoppedTyping;
        hub.DmConversationRead     -= OnDmConversationRead;
        hub.MessagePinned          -= OnMessagePinned;
        hub.MessageUnpinned        -= OnMessageUnpinned;
        hub.DmMessagePinned        -= OnDmMessagePinned;
        hub.DmMessageUnpinned      -= OnDmMessageUnpinned;
        hub.KickedFromServer       -= OnKickedFromServer;
        hub.CategoryCreated        -= OnCategoryCreated;
        hub.CategoryDeleted        -= OnCategoryDeleted;
        hub.CategoryRenamed        -= OnCategoryRenamed;
        hub.ChannelCategoryChanged -= OnChannelCategoryChanged;
        hub.MemberRoleChanged      -= OnMemberRoleChanged;
        hub.ChannelSlowmodeUpdated -= OnChannelSlowmodeUpdated;
        hub.ThreadReplyReceived    -= OnThreadReplyReceived;
        hub.UserMuted              -= OnUserMuted;
        hub.UserTempBanned         -= OnUserTempBanned;
        hub.ControlRequested       -= OnControlRequested;
        hub.ControlOffered         -= OnControlOffered;
        hub.ControlActive          -= OnControlActive;
        hub.ControlGranted         -= OnControlGranted;
        hub.ControlDeclined        -= OnControlDeclined;
        hub.ControlBusy            -= OnControlBusy;
        hub.ControlEnded           -= OnControlEnded;
        hub.RemoteInputReceived    -= OnRemoteInputReceived;
        if (_hubReconnectingHandler is not null)  hub.Reconnecting -= _hubReconnectingHandler;
        if (_hubReconnectedHandler is not null)   hub.Reconnected  -= _hubReconnectedHandler;
        if (_hubDisconnectedHandler is not null)  hub.Disconnected -= _hubDisconnectedHandler;
        screen.FrameCaptured       -= OnScreenFrameCaptured;
        screen.StreamAudioCaptured -= OnStreamAudioCaptured;
        if (_micLevelHandler is not null)         audio.MicLevelChanged    -= _micLevelHandler;
        speech.TextRecognized                     -= OnSpeechRecognized;
        if (_hypothesisChangedHandler is not null) speech.HypothesisChanged -= _hypothesisChangedHandler;
        ptt.PttDown -= OnPttDown;
        ptt.PttUp   -= OnPttUp;
        if (_keyLearnedHandler is not null) ptt.KeyLearned -= _keyLearnedHandler;
    }

    private int _voiceChannelId;

    private async Task JoinVoiceAsync(ChannelDto channel)
    {
        _voiceChannelId = channel.Id;
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
        // Dispatch to main thread to serialize with PostMessageAsync's AddToStore call.
        // The server echoes ReceiveMessage back to the sender; PostMessageAsync also adds the
        // message after the REST response. Both paths call AddToStore whose List<>.Any()+Add()
        // is not atomic, so without this dispatch the same message can appear twice in the store.
        MainThread.BeginInvokeOnMainThread(() =>
        {
            if (SelectedChannel?.Id != msg.ChannelId) return;
            if (_blockedUserIds.Contains(msg.AuthorId)) return;

            var item = Wire(new MessageViewItem(msg, api.CurrentUsername, api.CurrentUserId));
            AddToStore(item);
            _ = LoadPreviewAsync(item);

            if (item.IsMention)
            {
                var preview = msg.Content.Length > 80 ? msg.Content[..80] + "…" : msg.Content;
                toast.Show($"@Mention from {msg.AuthorUsername}", preview);
            }
        });
    }

    private void OnNewMessageNotification(MessageDto msg)
    {
        // Server broadcasts this to server-{id} group so every connected client hears about
        // every new message, regardless of which channel they are currently viewing.
        // We use it exclusively for unread badge tracking and background toasts.
        if (SelectedChannel?.Id == msg.ChannelId) return; // already shown via ReceiveMessage

        var channelItem = Channels.FirstOrDefault(c => c.Channel.Id == msg.ChannelId);
        bool isMention = NotificationRules.IsMentionOf(msg.Content, api.CurrentUsername);

        var serverItem = Servers.FirstOrDefault(s => s.IsSelected);
        var effectiveLevel = channelItem?.NotifLevelOverride
            ?? serverItem?.ServerNotifLevel
            ?? NotifLevel.All;

        if (effectiveLevel == NotifLevel.Muted) return;

        if (channelItem is not null)
            MainThread.BeginInvokeOnMainThread(() =>
            {
                if (effectiveLevel == NotifLevel.OnlyMentions)
                {
                    if (isMention) channelItem.MentionCount++;
                }
                else
                {
                    channelItem.UnreadCount++;
                    if (isMention) channelItem.MentionCount++;
                }
            });

        if (msg.AuthorId != api.CurrentUserId && (effectiveLevel == NotifLevel.All || isMention))
        {
            var preview = msg.Content.Length > 80 ? msg.Content[..80] + "…" : msg.Content;
            if (isMention)
                toast.Show($"@Mention from {msg.AuthorUsername}", preview);
            else
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
            if (item is not null) RemoveFromStore(item);
        });
    }

    private void OnUserStatusChanged(int userId, UserStatus status)
    {
        MainThread.BeginInvokeOnMainThread(() =>
        {
            var existing = Members.FirstOrDefault(m => m.Id == userId);
            if (existing is null) return;
            existing.Status = status;
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
        item.CanModerate = IsServerAdmin;
        item.AuthorRole = _memberRoles.GetValueOrDefault(item.Message.AuthorId, ServerRole.Member);
        return item;
    }

    private void RemoveFromStore(MessageViewItem item)
    {
        _textMsgs.Remove(item);
        _voiceMsgs.Remove(item);
        _streamMsgs.Remove(item);
        Messages.Remove(item);
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

    private void OnDmUserTyping(int userId, string username, int conversationId)
    {
        if (!IsDmMode || SelectedDmConversation?.ConversationId != conversationId) return;
        if (userId == api.CurrentUserId) return;
        MainThread.BeginInvokeOnMainThread(() =>
        {
            _typingUsersInChannel[userId] = username;
            OnPropertyChanged(nameof(TypingText));
        });
    }

    private void OnDmUserStoppedTyping(int userId, int conversationId)
    {
        if (!IsDmMode || SelectedDmConversation?.ConversationId != conversationId) return;
        MainThread.BeginInvokeOnMainThread(() =>
        {
            _typingUsersInChannel.Remove(userId);
            OnPropertyChanged(nameof(TypingText));
        });
    }

    private void OnDmConversationRead(int conversationId, int readByUserId, DateTime readAt)
    {
        if (!IsDmMode || SelectedDmConversation?.ConversationId != conversationId) return;
        if (readByUserId == api.CurrentUserId) return;
        MainThread.BeginInvokeOnMainThread(() => UpdateSeenReceipts(readAt));
    }

    private void OnMessagePinned(int messageId, int channelId)
    {
        if (SelectedChannel?.Id != channelId) return;
        MainThread.BeginInvokeOnMainThread(() =>
        {
            var item = Messages.FirstOrDefault(m => m.Message.Id == messageId);
            if (item is not null) item.IsPinned = true;
            if (IsPinsOpen) _ = LoadPinnedMessagesAsync();
        });
    }

    private void OnMessageUnpinned(int messageId, int channelId)
    {
        if (SelectedChannel?.Id != channelId) return;
        MainThread.BeginInvokeOnMainThread(() =>
        {
            var item = Messages.FirstOrDefault(m => m.Message.Id == messageId);
            if (item is not null) item.IsPinned = false;
            if (IsPinsOpen) _ = LoadPinnedMessagesAsync();
        });
    }

    private void OnDmMessagePinned(int messageId, int conversationId)
    {
        if (!IsDmMode || SelectedDmConversation?.ConversationId != conversationId) return;
        MainThread.BeginInvokeOnMainThread(() =>
        {
            var item = Messages.FirstOrDefault(m => m.Message.Id == messageId);
            if (item is not null) item.IsPinned = true;
            if (IsPinsOpen) _ = LoadPinnedMessagesAsync();
        });
    }

    private void OnDmMessageUnpinned(int messageId, int conversationId)
    {
        if (!IsDmMode || SelectedDmConversation?.ConversationId != conversationId) return;
        MainThread.BeginInvokeOnMainThread(() =>
        {
            var item = Messages.FirstOrDefault(m => m.Message.Id == messageId);
            if (item is not null) item.IsPinned = false;
            if (IsPinsOpen) _ = LoadPinnedMessagesAsync();
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
        if (PreviewCache.Count >= 1000)
            PreviewCache.TryRemove(PreviewCache.Keys.First(), out _);
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

    // ── Direct Messages ───────────────────────────────────────────────────────

    [RelayCommand]
    public void ToggleDmMode() => IsDmMode = !IsDmMode;

    [RelayCommand]
    public void SelectDmConversation(DmConversationItem item)
    {
        item.UnreadCount = 0;
        SelectedDmConversation = item;
    }

    [RelayCommand]
    public async Task StartDmWithUser(int userId)
    {
        var dto = await api.OpenDmConversationAsync(userId);
        if (dto is null) return;
        IsDmMode = true;
        var existing = DmConversations.FirstOrDefault(dc => dc.ConversationId == dto.Id);
        if (existing is null)
        {
            await LoadDmConversationsAsync();
            existing = DmConversations.FirstOrDefault(dc => dc.ConversationId == dto.Id);
        }
        if (existing is not null)
        {
            existing.UnreadCount = 0;
            SelectedDmConversation = existing;
        }
    }

    private async Task LoadDmConversationsAsync()
    {
        var convos = await api.GetDmConversationsAsync();
        if (convos is null) return;
        MainThread.BeginInvokeOnMainThread(() =>
        {
            DmConversations.Clear();
            foreach (var dto in convos)
                DmConversations.Add(DmConversationItem.FromDto(dto));
        });
    }

    private async Task LoadDmMessagesAsync(int conversationId)
    {
        var dtos = await api.GetDmMessagesAsync(conversationId);
        if (dtos is null) return;
        var readState = await api.MarkDmAsReadAsync(conversationId);
        MainThread.BeginInvokeOnMainThread(() =>
        {
            Messages.Clear();
            foreach (var dto in dtos)
                Messages.Add(new MessageViewItem(DmToMessageDto(dto), api.CurrentUsername, api.CurrentUserId));
            if (readState is not null)
                UpdateSeenReceipts(readState.OtherUserLastReadAt);
            ScrollToLatestRequested?.Invoke();
        });
    }

    private void UpdateSeenReceipts(DateTime? otherUserReadAt)
    {
        foreach (var m in Messages) m.ShowSeenReceipt = false;
        if (otherUserReadAt is null) return;
        var last = Messages.LastOrDefault(m => m.IsOwnMessage && !m.Message.IsDeleted
                                               && m.Message.CreatedAt <= otherUserReadAt);
        if (last is not null) last.ShowSeenReceipt = true;
    }

    private void OnDirectMessageReceived(DirectMessageDto dto)
    {
        if (_blockedUserIds.Contains(dto.AuthorId)) return;
        MainThread.BeginInvokeOnMainThread(() =>
        {
            if (IsDmMode && SelectedDmConversation?.ConversationId == dto.ConversationId)
            {
                var item = new MessageViewItem(DmToMessageDto(dto), api.CurrentUsername, api.CurrentUserId);
                Messages.Add(item);
                if (_isAtBottom) ScrollToLatestRequested?.Invoke();
                if (dto.AuthorId != api.CurrentUserId)
                    _ = api.MarkDmAsReadAsync(dto.ConversationId);
            }

            var convo = DmConversations.FirstOrDefault(dc => dc.ConversationId == dto.ConversationId);
            bool isActiveConvo = IsDmMode && SelectedDmConversation?.ConversationId == dto.ConversationId;
            if (convo is not null)
            {
                convo.LastMessagePreview = dto.IsDeleted ? "(message deleted)" : dto.Content;
                convo.LastMessageAt = dto.CreatedAt;
                if (!isActiveConvo && dto.AuthorId != api.CurrentUserId)
                    convo.UnreadCount++;
            }
            else if (IsDmMode && dto.AuthorId != api.CurrentUserId)
            {
                _ = LoadDmConversationsAsync();
            }

            if (NotificationRules.ShouldToastDm(dto.AuthorId, api.CurrentUserId, isActiveConvo))
            {
                var preview = dto.Content.Length > 80 ? dto.Content[..80] + "…" : dto.Content;
                toast.Show($"DM from {dto.AuthorUsername}", preview);
            }
        });
    }

    private void OnDirectMessageDeleted(int conversationId, int messageId)
    {
        if (!IsDmMode || SelectedDmConversation?.ConversationId != conversationId) return;
        MainThread.BeginInvokeOnMainThread(() =>
        {
            var item = Messages.FirstOrDefault(m => m.Message.Id == messageId);
            item?.ApplyDelete();
        });
    }

    private static MessageDto DmToMessageDto(DirectMessageDto dm) => new(
        dm.Id, dm.Content, dm.AuthorUsername, dm.AuthorId, dm.CreatedAt,
        dm.ConversationId, MessageSource.Text, dm.AttachmentUrl, dm.IsDeleted,
        null, null, dm.AuthorAvatarUrl, null, null, null, dm.AttachmentFileName);


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
                Members.Add(new MemberViewItem(user, _memberRoles.GetValueOrDefault(user.Id)));
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
                Members.Add(new MemberViewItem(user, _memberRoles.GetValueOrDefault(user.Id)));
            if (!_memberRoles.ContainsKey(user.Id))
                _memberRoles[user.Id] = ServerRole.Member;
        });
    }

    private void OnChannelCreated(ChannelDto channel)
    {
        if (SelectedServer?.Id == channel.ServerId)
            MainThread.BeginInvokeOnMainThread(() =>
            {
                if (!Channels.Any(c => c.Channel.Id == channel.Id))
                    Channels.Add(new ChannelViewItem(channel));
                RebuildCategorizedChannels();
            });
    }

    private void OnForceJoinChannel(int channelId)
    {
        var item = Channels.FirstOrDefault(c => c.Channel.Id == channelId);
        if (item is null) return;
        _ = SelectChannelAsync(item);
    }

    private void OnChannelUpdated(ChannelDto dto)
    {
        MainThread.BeginInvokeOnMainThread(() =>
        {
            var idx = -1;
            for (var i = 0; i < Channels.Count; i++)
            {
                if (Channels[i].Channel.Id == dto.Id) { idx = i; break; }
            }
            if (idx < 0) return;
            Channels[idx] = new ChannelViewItem(dto);
            if (SelectedChannel?.Id == dto.Id)
                SelectedChannel = dto;
            RebuildCategorizedChannels();
        });
    }

    private void OnChannelDeleted(int channelId)
    {
        MainThread.BeginInvokeOnMainThread(() =>
        {
            var item = Channels.FirstOrDefault(c => c.Channel.Id == channelId);
            if (item is null) return;
            Channels.Remove(item);
            if (SelectedChannel?.Id == channelId)
            {
                SelectedChannel = null;
                Messages.Clear();
            }
            RebuildCategorizedChannels();
        });
    }

    private void RebuildCategorizedChannels()
    {
        CategorizedChannels.Clear();
        var catItems = _serverCategories
            .OrderBy(c => c.Position)
            .Select(CategoryViewItem.FromDto)
            .ToList();
        var uncategorized = CategoryViewItem.Uncategorized();
        foreach (var ch in Channels)
        {
            if (ch.CategoryId.HasValue)
            {
                var cat = catItems.FirstOrDefault(c => c.Id == ch.CategoryId.Value);
                (cat ?? uncategorized).Channels.Add(ch);
            }
            else
                uncategorized.Channels.Add(ch);
        }
        if (uncategorized.Channels.Count > 0) CategorizedChannels.Add(uncategorized);
        foreach (var cat in catItems) CategorizedChannels.Add(cat);
    }

    private void OnCategoryCreated(CategoryDto dto)
    {
        MainThread.BeginInvokeOnMainThread(() =>
        {
            if (SelectedServer?.Id != dto.ServerId) return;
            if (!_serverCategories.Any(c => c.Id == dto.Id))
                _serverCategories.Add(dto);
            RebuildCategorizedChannels();
        });
    }

    private void OnCategoryDeleted(int categoryId)
    {
        MainThread.BeginInvokeOnMainThread(() =>
        {
            _serverCategories.RemoveAll(c => c.Id == categoryId);
            foreach (var ch in Channels.Where(c => c.CategoryId == categoryId))
                ch.CategoryId = null;
            RebuildCategorizedChannels();
        });
    }

    private void OnCategoryRenamed(int categoryId, string name)
    {
        MainThread.BeginInvokeOnMainThread(() =>
        {
            var dto = _serverCategories.FirstOrDefault(c => c.Id == categoryId);
            if (dto is null) return;
            _serverCategories.Remove(dto);
            _serverCategories.Add(dto with { Name = name });
            RebuildCategorizedChannels();
        });
    }

    private void OnChannelCategoryChanged(int channelId, int? categoryId)
    {
        MainThread.BeginInvokeOnMainThread(() =>
        {
            var item = Channels.FirstOrDefault(c => c.Channel.Id == channelId);
            if (item is null) return;
            item.CategoryId = categoryId;
            RebuildCategorizedChannels();
        });
    }

    private void OnMemberRoleChanged(int userId, string roleName)
    {
        if (!Enum.TryParse<ServerRole>(roleName, out var role)) return;
        MainThread.BeginInvokeOnMainThread(() =>
        {
            _memberRoles[userId] = role;
            // Update sidebar icon for this member
            var member = Members.FirstOrDefault(m => m.Id == userId);
            if (member is not null) member.Role = role;
            // Update author role icon on any visible messages from this user
            foreach (var m in _textMsgs.Concat(_voiceMsgs).Concat(_streamMsgs).Where(m => m.Message.AuthorId == userId))
                m.AuthorRole = role;
            if (userId == api.CurrentUserId && SelectedServer is not null)
            {
                SelectedServer = SelectedServer with { MyRole = role };
                OnPropertyChanged(nameof(CurrentServerRole));
                OnPropertyChanged(nameof(IsServerAdmin));
                OnPropertyChanged(nameof(IsAdminOrModerator));
                var isAdmin = role >= ServerRole.Admin;
                foreach (var m in _textMsgs.Concat(_voiceMsgs).Concat(_streamMsgs))
                    m.CanModerate = isAdmin;
            }
        });
    }

    private void OnChannelSlowmodeUpdated(int channelId, int seconds)
    {
        MainThread.BeginInvokeOnMainThread(() =>
        {
            var channelItem = Channels.FirstOrDefault(c => c.Channel.Id == channelId);
            if (channelItem is null) return;
            channelItem.SlowmodeSeconds = seconds;
            if (SelectedChannel?.Id == channelId)
            {
                OnPropertyChanged(nameof(CurrentChannelSlowmodeLabel));
                OnPropertyChanged(nameof(CurrentChannelHasSlowmode));
                if (seconds == 0) { _slowmodeCts?.Cancel(); SlowmodeCooldown = 0; }
            }
        });
    }

    [RelayCommand]
    public async Task SetSlowmodeAsync(ChannelViewItem channelItem)
    {
        if (!IsServerAdmin || SelectedServer is null) return;
        var options = new[] { "Off", "5 seconds", "10 seconds", "30 seconds", "1 minute", "5 minutes", "10 minutes" };
        var pick = await Shell.Current.DisplayActionSheet($"Set Slowmode for #{channelItem.Channel.Name}", "Cancel", null, options);
        var seconds = pick switch
        {
            "Off"        => 0,
            "5 seconds"  => 5,
            "10 seconds" => 10,
            "30 seconds" => 30,
            "1 minute"   => 60,
            "5 minutes"  => 300,
            "10 minutes" => 600,
            _            => -1
        };
        if (seconds < 0) return;
        await api.SetSlowmodeAsync(SelectedServer.Id, channelItem.Channel.Id, seconds);
    }

    [RelayCommand]
    public async Task ElevateToAdminAsync(int userId)
    {
        if (!IsServerAdmin || SelectedServer is null) return;
        if (_memberRoles.GetValueOrDefault(userId) == ServerRole.Admin) return;
        var ok = await api.SetMemberRoleAsync(SelectedServer.Id, userId, ServerRole.Admin);
        if (!ok)
            toast.Show("FatGuysSpeak", "Failed to update role.");
    }

    [RelayCommand]
    public async Task PromoteToModeratorAsync(int userId)
    {
        if (!IsServerAdmin || SelectedServer is null) return;
        if (_memberRoles.GetValueOrDefault(userId) >= ServerRole.Moderator) return;
        var ok = await api.SetMemberRoleAsync(SelectedServer.Id, userId, ServerRole.Moderator);
        if (!ok)
            toast.Show("FatGuysSpeak", "Failed to update role.");
    }

    [RelayCommand]
    public async Task CreateChannelAsync()
    {
        if (!IsServerAdmin || SelectedServer is null) return;
        var name = await Shell.Current.DisplayPromptAsync(
            "New Channel", "Enter channel name:", "Create", "Cancel");
        if (string.IsNullOrWhiteSpace(name)) return;
        var result = await api.CreateChannelAsync(
            SelectedServer.Id, new CreateChannelRequest(name.Trim(), ChannelType.Text));
        if (result is null)
            toast.Show("FatGuysSpeak", "Failed to create channel.");
        // ChannelCreated SignalR event already appends the channel to the list for all clients
    }

    [RelayCommand]
    public async Task ManageWordFilterAsync()
    {
        if (!IsServerAdmin || SelectedServer is null) return;
        var action = await Shell.Current.DisplayActionSheet(
            "Word Filter", "Cancel", null,
            "Add pattern", "Remove pattern", "View all patterns");
        if (action is null or "Cancel") return;

        if (action == "Add pattern")
        {
            var pattern = await Shell.Current.DisplayPromptAsync(
                "Add Word Filter", "Enter a word or phrase to filter:", "Add", "Cancel",
                placeholder: "e.g. badword");
            if (string.IsNullOrWhiteSpace(pattern)) return;
            var result = await api.AddWordFilterAsync(SelectedServer.Id, pattern.Trim());
            toast.Show("Word Filter", result is not null ? $"Added \"{pattern.Trim()}\"." : "Failed to add pattern.");
        }
        else if (action == "Remove pattern")
        {
            var filters = await api.GetWordFiltersAsync(SelectedServer.Id);
            if (filters is null || filters.Count == 0)
            {
                toast.Show("Word Filter", "No patterns configured.");
                return;
            }
            var pick = await Shell.Current.DisplayActionSheet(
                "Remove Pattern", "Cancel", null,
                filters.Select(f => f.Pattern).ToArray());
            if (pick is null or "Cancel") return;
            var target = filters.First(f => f.Pattern == pick);
            var ok = await api.RemoveWordFilterAsync(SelectedServer.Id, target.Id);
            toast.Show("Word Filter", ok ? $"Removed \"{pick}\"." : "Failed to remove pattern.");
        }
        else if (action == "View all patterns")
        {
            var filters = await api.GetWordFiltersAsync(SelectedServer.Id);
            if (filters is null || filters.Count == 0)
            {
                await Shell.Current.DisplayAlert("Word Filter", "No patterns configured.", "OK");
                return;
            }
            var list = string.Join("\n", filters.Select(f => $"• {f.Pattern}"));
            await Shell.Current.DisplayAlert($"Word Filter ({filters.Count})", list, "OK");
        }
    }

    [RelayCommand]
    public void ToggleCategory(CategoryViewItem item) => item.IsCollapsed = !item.IsCollapsed;

    [RelayCommand]
    public async Task CreateCategoryPromptAsync()
    {
        if (SelectedServer is null || !IsServerAdmin) return;
        var name = await Shell.Current.DisplayPromptAsync(
            "New Category", "Category name:", placeholder: "e.g. General");
        if (string.IsNullOrWhiteSpace(name)) return;
        await api.CreateCategoryAsync(SelectedServer.Id, new CreateCategoryRequest(name.Trim()));
    }

    [RelayCommand]
    public async Task RenameCategoryAsync(CategoryViewItem item)
    {
        if (SelectedServer is null || item.Id == 0) return;
        var name = await Shell.Current.DisplayPromptAsync(
            "Rename Category", "New name:", initialValue: item.Name);
        if (string.IsNullOrWhiteSpace(name)) return;
        await api.RenameCategoryAsync(SelectedServer.Id, item.Id, new RenameCategoryRequest(name.Trim()));
    }

    [RelayCommand]
    public async Task DeleteCategoryAsync(CategoryViewItem item)
    {
        if (SelectedServer is null || item.Id == 0) return;
        var confirm = await Shell.Current.DisplayAlert(
            "Delete Category", $"Delete '{item.Name}'? Channels will become uncategorized.", "Delete", "Cancel");
        if (!confirm) return;
        await api.DeleteCategoryAsync(SelectedServer.Id, item.Id);
    }

    [RelayCommand]
    public async Task SetChannelCategoryAsync(ChannelViewItem channelItem)
    {
        if (SelectedServer is null) return;
        var choices = _serverCategories.Select(c => c.Name).Prepend("None").ToArray();
        var picked = await Shell.Current.DisplayActionSheet("Move channel to:", "Cancel", null, choices);
        if (picked is null or "Cancel") return;
        int? catId = picked == "None" ? null : _serverCategories.FirstOrDefault(c => c.Name == picked)?.Id;
        await api.SetChannelCategoryAsync(
            SelectedServer.Id, channelItem.Channel.Id, new SetChannelCategoryRequest(catId));
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
            {
                _ = hub.WatchStreamAsync(channelId);
                audio.StartStreamPlayback();
            }
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
            {
                _ = hub.StopWatchingAsync(channelId);
                audio.StopStreamPlayback();
            }
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

    private void OnStreamAudioCaptured(byte[] data) =>
        _ = hub.SendStreamAudioAsync(data);

    private void OnStreamAudioReceived(int streamerId, byte[] data)
    {
        if (ActiveStreamerId == streamerId)
            audio.PlayStreamAudio(data);
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

    // ── Remote Control ────────────────────────────────────────────────────────

    private void OnControlRequested(int controllerId, string controllerName)
    {
        MainThread.BeginInvokeOnMainThread(async () =>
        {
            bool allow = await Shell.Current.DisplayAlert(
                "Remote control request",
                $"{controllerName} wants to control your screen.\n\nThey will be able to control your WHOLE PC — not just the shared window — until you stop it.",
                "Allow", "Deny");
            if (allow)
                await hub.GrantControl(controllerId);
            else
                await hub.DenyControl(controllerId);
        });
    }

    private void OnControlOffered(int streamerId, string streamerName)
    {
        MainThread.BeginInvokeOnMainThread(async () =>
        {
            bool accept = await Shell.Current.DisplayAlert(
                "Remote control offered",
                $"{streamerName} is giving you control of their screen. Accept?",
                "Accept", "Decline");
            if (accept)
                await hub.AcceptControl(streamerId);
            else
                await hub.DenyControl(streamerId);
        });
    }

    private void OnControlActive(int controllerId, string controllerName)
    {
        MainThread.BeginInvokeOnMainThread(() =>
        {
            IsBeingControlled = true;
            ControllerName = controllerName;
        });
    }

    private void OnControlGranted(int streamerId, string streamerName)
    {
        MainThread.BeginInvokeOnMainThread(() =>
        {
            IsControlling = true;
            ControlledName = streamerName;
        });
    }

    private void OnControlDeclined(int _)
    {
        MainThread.BeginInvokeOnMainThread(async () =>
            await Shell.Current.DisplayAlert("Remote control", "Request was declined.", "OK"));
    }

    private void OnControlBusy()
    {
        MainThread.BeginInvokeOnMainThread(async () =>
            await Shell.Current.DisplayAlert("Remote control", "Someone already has control of that screen.", "OK"));
    }

    private void OnControlEnded(int _)
    {
        MainThread.BeginInvokeOnMainThread(() =>
        {
            IsBeingControlled = false;
            ControllerName = null;
            IsControlling = false;
            ControlledName = null;
        });
    }

    private void OnRemoteInputReceived(RemoteInputDto dto)
    {
        if (IsBeingControlled)
            remoteInput.Inject(dto);
    }

    [RelayCommand]
    public void RequestControl()
    {
        if (ActiveStreamerId <= 0) return;
        _ = hub.RequestControl(ActiveStreamerId);
    }

    [RelayCommand]
    public void StopControl() => _ = hub.StopControl();

    [RelayCommand]
    public void ReleaseControl() => _ = hub.ReleaseControl();

    [RelayCommand]
    public void OfferControl(int viewerId) => _ = hub.OfferControl(viewerId);
}
