using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using FatGuysSpeak.Shared;

namespace FatGuysSpeak.Client.ViewModels;

public partial class ChannelViewItem(ChannelDto channel) : ObservableObject
{
    public ChannelDto Channel { get; } = channel;
    public ObservableCollection<UserDto> Occupants { get; } = [];
    [ObservableProperty] private bool _isSelected;
}
