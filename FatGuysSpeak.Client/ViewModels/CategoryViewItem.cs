using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using FatGuysSpeak.Shared;

namespace FatGuysSpeak.Client.ViewModels;

public partial class CategoryViewItem : ObservableObject
{
    public int Id { get; init; }     // 0 = virtual "uncategorized" group
    public string Name { get; init; } = "";
    [ObservableProperty] private bool _isCollapsed;
    public ObservableCollection<ChannelViewItem> Channels { get; } = [];

    partial void OnIsCollapsedChanged(bool _) => OnPropertyChanged(nameof(ToggleIcon));
    public string ToggleIcon => IsCollapsed ? "▸" : "▾";
    public bool IsRealCategory => Id != 0;

    public static CategoryViewItem Uncategorized() => new() { Id = 0, Name = "CHANNELS" };
    public static CategoryViewItem FromDto(CategoryDto dto) => new() { Id = dto.Id, Name = dto.Name.ToUpperInvariant() };
}
