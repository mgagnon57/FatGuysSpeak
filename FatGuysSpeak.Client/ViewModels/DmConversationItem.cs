using CommunityToolkit.Mvvm.ComponentModel;
using FatGuysSpeak.Shared;

namespace FatGuysSpeak.Client.ViewModels;

public partial class DmConversationItem : ObservableObject
{
    public int ConversationId { get; init; }
    public int OtherUserId { get; init; }
    public string OtherUsername { get; init; } = "";
    public string? OtherAvatarUrl { get; init; }
    public bool HasOtherAvatar => OtherAvatarUrl is not null;
    public string Initials => OtherUsername.Length > 0 ? OtherUsername[..1].ToUpperInvariant() : "?";

    [ObservableProperty] private bool _isSelected;
    [ObservableProperty] private int _unreadCount;
    [ObservableProperty] private string? _lastMessagePreview;
    [ObservableProperty] private DateTime? _lastMessageAt;

    public bool HasUnread => UnreadCount > 0;
    public string LastAtText => LastMessageAt.HasValue
        ? LastMessageAt.Value.ToLocalTime().ToString("h:mm tt")
        : "";

    partial void OnUnreadCountChanged(int value) => OnPropertyChanged(nameof(HasUnread));
    partial void OnLastMessageAtChanged(DateTime? value) => OnPropertyChanged(nameof(LastAtText));

    public static DmConversationItem FromDto(DirectConversationDto dto) => new()
    {
        ConversationId = dto.Id,
        OtherUserId = dto.OtherUserId,
        OtherUsername = dto.OtherUsername,
        OtherAvatarUrl = dto.OtherAvatarUrl,
        LastMessagePreview = dto.LastMessagePreview,
        LastMessageAt = dto.LastMessageAt,
    };
}
