using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using FatGuysSpeak.Shared;

namespace FatGuysSpeak.Client.ViewModels;

public partial class ChannelViewItem(ChannelDto channel) : ObservableObject
{
    public ChannelDto Channel { get; } = channel;
    public ObservableCollection<UserDto> Occupants { get; } = [];

    [ObservableProperty] private bool _isSelected;
    [ObservableProperty] private int _unreadCount;
    [ObservableProperty] private int? _categoryId = channel.CategoryId;

    public bool HasUnread => UnreadCount > 0;
    public string UnreadBadge => UnreadCount > 99 ? "99+" : UnreadCount.ToString();

    partial void OnUnreadCountChanged(int value)
    {
        OnPropertyChanged(nameof(HasUnread));
        OnPropertyChanged(nameof(UnreadBadge));
    }
}
