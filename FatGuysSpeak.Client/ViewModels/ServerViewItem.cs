using CommunityToolkit.Mvvm.ComponentModel;
using FatGuysSpeak.Shared;

namespace FatGuysSpeak.Client.ViewModels;

public partial class ServerViewItem : ObservableObject
{
    public ServerDto Server { get; init; } = null!;
    public string? IconUrl { get; init; }
    public NotifLevel? ServerNotifLevel { get; set; }
    [ObservableProperty] private bool _isSelected;

    public string Initials => Server.Name.Length > 0
        ? Server.Name[..1].ToUpperInvariant()
        : "?";
    public bool HasIcon => IconUrl is not null;

    public static ServerViewItem FromDto(ServerDto dto, string? iconUrl = null) =>
        new() { Server = dto, IconUrl = iconUrl, ServerNotifLevel = dto.UserNotifLevel };
}
