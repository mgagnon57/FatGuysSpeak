using System.Collections.ObjectModel;
using System.Text.RegularExpressions;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FatGuysSpeak.Client.Pages;
using FatGuysSpeak.Shared;

namespace FatGuysSpeak.Client.ViewModels;

public partial class MessageViewItem : ObservableObject
{
    private static int _systemIdCounter = -1;

    [ObservableProperty] private MessageDto _message;
    [ObservableProperty] private bool _isDeleted;
    [ObservableProperty] private bool _isEdited;
    [ObservableProperty] private LinkPreviewDto? _preview;
    [ObservableProperty] private bool _showSeenReceipt;
    [ObservableProperty] private bool _isPinned;
    [ObservableProperty] private int _replyCount;

    public bool HasThread => ReplyCount > 0;
    public string ThreadLabel => ReplyCount == 1 ? "1 reply" : $"{ReplyCount} replies";

    partial void OnReplyCountChanged(int value)
    {
        OnPropertyChanged(nameof(HasThread));
        OnPropertyChanged(nameof(ThreadLabel));
    }

    // GIF playback control
    private string? _attachmentDisplayUrl;
    private Timer? _gifTimer;

    public string? AttachmentDisplayUrl
    {
        get => _attachmentDisplayUrl;
        private set
        {
            if (_attachmentDisplayUrl == value) return;
            _attachmentDisplayUrl = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsGifPlaying));
            OnPropertyChanged(nameof(GifPaused));
        }
    }

    public bool IsSystemMessage { get; private init; }
    public bool IsNewMessageDivider { get; private init; }
    // Day-grouping header (collapsible). A synthetic, non-message row like the divider above.
    public bool     IsDayHeader      { get; private init; }
    public DateTime DayDate          { get; private init; }
    public bool     IsDayCollapsed   { get; private init; }
    public string   DayLabel         { get; private init; } = "";
    public int      DayMessageCount  { get; private init; }
    public string   DayChevron       => IsDayCollapsed ? "▶" : "▼";
    public string   DayCountLabel    => DayMessageCount == 1 ? "1 message" : $"{DayMessageCount} messages";
    // PorkChop's recap row, shown when a past day is expanded (instead of its raw messages).
    public bool   IsDaySummary  { get; private init; }
    public string SummaryText   { get; private init; } = "";
    public bool   MessagesShown { get; private init; }
    public string ShowMessagesLabel => MessagesShown ? "Hide messages" : "Show full messages";
    public bool IsRegularMessage => !IsSystemMessage && !IsNewMessageDivider && !IsDayHeader && !IsDaySummary;
    public bool IsMention { get; private init; }
    public bool IsOwnMessage { get; private init; }
    public bool IsBot => Message.Source == MessageSource.AI;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(CanDelete))] private bool _canModerate;
    public bool CanDelete => IsOwnMessage || CanModerate;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AuthorRoleIcon), nameof(HasAuthorRoleIcon))]
    private ServerRole _authorRole;
    public string AuthorRoleIcon => AuthorRole switch
    {
        ServerRole.Admin => "👑",
        _ => ""
    };
    public bool HasAuthorRoleIcon => AuthorRole >= ServerRole.Admin;

    public bool HasAttachment => Message.AttachmentUrl is not null;
    public bool HasGifAttachment => IsGifUrl(Message.AttachmentUrl);
    public bool IsImageAttachment => HasAttachment && IsImageUrl(Message.AttachmentUrl);
    public bool HasStaticAttachment => IsImageAttachment && !HasGifAttachment;
    public bool IsFileAttachment => HasAttachment && !IsImageAttachment;
    public string AttachmentFileName => Message.AttachmentFileName
        ?? (Message.AttachmentUrl is not null ? Path.GetFileName(new Uri(Message.AttachmentUrl).LocalPath) : "");
    public string AttachmentFileIcon => Path.GetExtension(AttachmentFileName).ToLowerInvariant() switch
    {
        ".pdf"  => "📄",
        ".zip" or ".7z" or ".rar" or ".tar" or ".gz" => "🗜",
        ".mp3" or ".wav" or ".ogg" => "🎵",
        ".mp4" or ".mov" or ".mkv" or ".webm" => "🎬",
        ".doc" or ".docx" => "📝",
        ".xls" or ".xlsx" => "📊",
        ".ppt" or ".pptx" => "📋",
        _ => "📁"
    };
    public bool IsGifPlaying => HasGifAttachment && _attachmentDisplayUrl is not null;
    public bool GifPaused => HasGifAttachment && _attachmentDisplayUrl is null;

    public bool HasAvatarImage => !string.IsNullOrEmpty(Message.AuthorAvatarUrl);
    public bool HasReply => Message.ReplyToId.HasValue;

    // Link / video helpers
    private static readonly Regex ContentUrlRegex = new(@"https?://\S+", RegexOptions.Compiled);

    public string? ContentUrl
    {
        get
        {
            if (string.IsNullOrEmpty(Message.Content)) return null;
            var m = ContentUrlRegex.Match(Message.Content);
            return m.Success ? m.Value.TrimEnd('.', ',', ')', ']', '!', '?') : null;
        }
    }

    public bool IsVideoLink => VideoUrlHelper.GetEmbedUrl(ContentUrl) is not null;
    public string? VideoEmbedUrl => VideoUrlHelper.GetEmbedUrl(ContentUrl);
    public string ReplyAuthor => Message.ReplyToUsername ?? "";
    public string ReplyBodyPreview => Message.ReplyPreview ?? "";
    public bool HasReactions => Reactions.Count > 0;

    public ObservableCollection<ReactionCountItem> Reactions { get; } = [];

    // Set by the ViewModel so the command can reach the API without coupling to ApiService
    public Action<MessageViewItem, string>? ReactionRequested { get; set; }

    // Called from both the quick-react context menu items (with a fixed emoji) and by pills
    public IRelayCommand<string> ToggleReactionCommand { get; }

    public MessageViewItem(MessageDto message, string currentUsername = "", int currentUserId = 0)
    {
        _message = message;
        _isDeleted = message.IsDeleted;
        _isEdited = message.EditedAt.HasValue;
        _isPinned = message.IsPinned;
        IsSystemMessage = false;
        IsOwnMessage = message.AuthorId != 0 && message.AuthorId == currentUserId;
        IsMention = message.AuthorId != 0
            && message.AuthorUsername != currentUsername
            && NotificationRules.IsMentionOf(message.Content, currentUsername);

        ToggleReactionCommand = new RelayCommand<string>(emoji =>
            ReactionRequested?.Invoke(this, emoji ?? ""));

        if (message.Reactions is not null)
            ApplyReactionsCore(message.Reactions);

        Reactions.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasReactions));

        _attachmentDisplayUrl = message.AttachmentUrl;
        if (HasGifAttachment)
            StartGifTimer();
    }

    private static readonly HashSet<string> ImageExts =
        [".jpg", ".jpeg", ".png", ".gif", ".webp"];

    private static bool IsGifUrl(string? url)
    {
        if (url is null) return false;
        var path = url.Split('?')[0];
        return path.EndsWith(".gif", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsImageUrl(string? url)
    {
        if (url is null) return false;
        var ext = Path.GetExtension(url.Split('?')[0]).ToLowerInvariant();
        return ImageExts.Contains(ext);
    }

    private void StartGifTimer()
    {
        _gifTimer?.Dispose();
        _gifTimer = new Timer(_ =>
            MainThread.BeginInvokeOnMainThread(() => AttachmentDisplayUrl = null),
            null, TimeSpan.FromSeconds(10), Timeout.InfiniteTimeSpan);
    }

    [RelayCommand]
    public async Task ReplayGif()
    {
        if (!HasGifAttachment) return;
        _gifTimer?.Dispose();
        AttachmentDisplayUrl = null;          // force Image to unload
        await Task.Delay(50);                 // one render pass so MAUI releases the source
        AttachmentDisplayUrl = Message.AttachmentUrl;
        StartGifTimer();
    }

    [RelayCommand]
    public async Task OpenFile()
    {
        if (Message.AttachmentUrl is null) return;
        try { await Launcher.OpenAsync(Message.AttachmentUrl); }
        catch { }
    }

    [RelayCommand]
    public async Task OpenLink()
    {
        var url = ContentUrl;
        if (url is null) return;
        try { await Launcher.OpenAsync(url); }
        catch { }
    }

    [RelayCommand]
    public void WatchVideo()
    {
        var embedUrl = VideoEmbedUrl;
        var originalUrl = ContentUrl;
        if (embedUrl is null || originalUrl is null) return;
        var title = Preview?.Title ?? "Video";
        MainThread.BeginInvokeOnMainThread(() =>
        {
            try
            {
                var page = new VideoPlayerPage(embedUrl, title, originalUrl);
                var window = new Window(page)
                {
                    Title = "FatGuysSpeak — Video Player",
                    Width = 960,
                    Height = 580,
                    MinimumWidth = 640,
                    MinimumHeight = 400,
                };
                Application.Current?.OpenWindow(window);
            }
            catch (Exception ex)
            {
                _ = Application.Current?.MainPage?.DisplayAlert("Video Player", ex.Message, "OK");
            }
        });
    }

    public void ApplyEdit(MessageDto updated)
    {
        Message = updated;
        IsEdited = updated.EditedAt.HasValue;
    }

    public void ApplyDelete()
    {
        Message = Message with { IsDeleted = true };
        IsDeleted = true;
    }

    public void ApplyReactions(List<ReactionDto> reactions)
    {
        Reactions.Clear();
        ApplyReactionsCore(reactions);
    }

    private void ApplyReactionsCore(List<ReactionDto> reactions)
    {
        foreach (var r in reactions)
            Reactions.Add(new ReactionCountItem(r.Emoji, r.Count, r.IsOwn,
                emoji => ReactionRequested?.Invoke(this, emoji)));
    }

    partial void OnMessageChanged(MessageDto value)
    {
        OnPropertyChanged(nameof(HasAttachment));
        OnPropertyChanged(nameof(HasGifAttachment));
        OnPropertyChanged(nameof(IsImageAttachment));
        OnPropertyChanged(nameof(HasStaticAttachment));
        OnPropertyChanged(nameof(IsFileAttachment));
        OnPropertyChanged(nameof(AttachmentFileName));
        OnPropertyChanged(nameof(HasAvatarImage));
        OnPropertyChanged(nameof(HasReply));
        OnPropertyChanged(nameof(ReplyAuthor));
        OnPropertyChanged(nameof(ReplyBodyPreview));
        OnPropertyChanged(nameof(ContentUrl));
        OnPropertyChanged(nameof(IsVideoLink));
        OnPropertyChanged(nameof(VideoEmbedUrl));
    }

    public static MessageViewItem CreateSystem(string text, int channelId, MessageSource source = MessageSource.Text)
    {
        var id = Interlocked.Decrement(ref _systemIdCounter);
        var dto = new MessageDto(id, text, string.Empty, 0, DateTime.UtcNow, channelId, source);
        return new MessageViewItem(dto) { IsSystemMessage = true };
    }

    public static MessageViewItem CreateNewMessagesDivider(int channelId)
    {
        var id = Interlocked.Decrement(ref _systemIdCounter);
        var dto = new MessageDto(id, "", string.Empty, 0, DateTime.UtcNow, channelId);
        return new MessageViewItem(dto) { IsNewMessageDivider = true };
    }

    public static MessageViewItem CreateDayHeader(DateTime day, string label, int count, bool collapsed)
    {
        var id = Interlocked.Decrement(ref _systemIdCounter);
        var dto = new MessageDto(id, "", string.Empty, 0, DateTime.UtcNow, 0);
        return new MessageViewItem(dto)
        {
            IsDayHeader = true, DayDate = day, DayLabel = label,
            DayMessageCount = count, IsDayCollapsed = collapsed,
        };
    }

    public static MessageViewItem CreateDaySummary(DateTime day, string text, bool messagesShown)
    {
        var id = Interlocked.Decrement(ref _systemIdCounter);
        var dto = new MessageDto(id, "", string.Empty, 0, DateTime.UtcNow, 0);
        return new MessageViewItem(dto)
        {
            IsDaySummary = true, DayDate = day, SummaryText = text, MessagesShown = messagesShown,
        };
    }
}
